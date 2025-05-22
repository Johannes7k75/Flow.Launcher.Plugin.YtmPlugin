using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.Pkcs;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using Flow.Launcher.Plugin;

namespace Flow.Launcher.Plugin.YtmPlugin
{
    public class YtmPlugin : IAsyncPlugin
    {
        private PluginInitContext _context;

        private YtmPluginClient _client;

        private DateTime lastQueryTime;
        private string currentQuery;

        private const string YtmIcon = "icon.png";

        private bool optimizeclientUsage = true;
        private int OptimizeClientKeyDelay = 200;
        private int cachedVolume = -1;

        private readonly Dictionary<string, Func<string, List<Result>>> _terms = new(StringComparer.InvariantCultureIgnoreCase);
        private readonly Dictionary<string, Func<string, Task<List<Result>>>> _expensiveTerms = new(StringComparer.InvariantCultureIgnoreCase);

        public static Logger _logger;

        public class Logger
        {
            private IPublicAPI _api;
            private readonly string name;

            public Logger(IPublicAPI api, string name)
            {
                this.name = name;
                _api = api;
            }

            public void Info(string message) => _api.LogInfo(name, message);
            public void Debug(string message) => _api.LogDebug(name, message);
            public void Warn(string message) => _api.LogWarn(name, message);
            public void Exception(string message, Exception ex) => _api.LogException(name, message, ex);
        }

        public Task InitAsync(PluginInitContext context)
        {
            _context = context;
            lastQueryTime = DateTime.UtcNow;

            _ = Task.Run(() =>
            {
                _client = new YtmPluginClient(_context.CurrentPluginMetadata.PluginDirectory);
                _client.Connect();
                _client.AddOnSongUpdate(song => RefreshDisplayInfo());
            });

            _logger = new Logger(context.API, this.ToString());

            _terms.Add("next", PlayNext);
            _terms.Add("last", PlayLast);
            _terms.Add("pause", Pause);
            _terms.Add("play", Play);
            _terms.Add("muted", ToggleMute);
            _terms.Add("vol", SetVolume);
            _terms.Add("volume", SetVolume);
            _terms.Add("shuffle", Shuffle);
            _terms.Add("repeat", ToggleRepeat);

            return Task.CompletedTask;
        }
        public async Task<List<Result>> QueryAsync(Query query, CancellationToken token)
        {
            currentQuery = query.RawQuery;

            if (!_client.IsConnected)
            {
                await _client.Connect();
            }

            try
            {
                List<Result> results;

                if (string.IsNullOrWhiteSpace(query.Search))
                {
                    return GetPlaying();
                }

                if (_terms.ContainsKey(query.FirstSearch))
                {
                    return _terms[query.FirstSearch].Invoke(query.SecondToEndSearch);
                }

                if (optimizeclientUsage)
                {
                    await Task.Delay(OptimizeClientKeyDelay, token);
                    if (token.IsCancellationRequested)
                    {
                        return null;
                    }
                }

                if (_expensiveTerms.ContainsKey(query.FirstSearch))
                {
                    results = await _expensiveTerms[query.FirstSearch].Invoke(query.SecondToEndSearch);
                    return results;
                }

                return GetPlaying();

            }
            catch (Exception ex)
            {
                _logger.Exception(ex.Message, ex);
                return SingleResultInList("There was an error with your requet", ex.GetBaseException().Message);
            }
        }

        public List<Result> SingleResultInList(string title, string subtitle = "", string iconPath = YtmIcon, Action action = default, bool hideAfterAction = true, bool requery = true, int score = 1)
        {
            return new List<Result>()
            {
                new()
                {
                    Title = title,
                    SubTitle = subtitle,
                    IcoPath = iconPath,
                    Score = score,
                    Action = _ =>
                    {
                        action?.Invoke();
                        if (requery) RefreshDisplayInfo();

                        return hideAfterAction;
                    }
                }
            };
        }


        private List<Result> Play(string arg) => SingleResultInList("Play", $"Resume: {_client.PlaybackContext.song.title}", action: _client.Play);
        private List<Result> Pause(string arg = null) => SingleResultInList("Pause", $"Pause: {_client.PlaybackContext.song.title}", action: _client.Pause);
        private List<Result> PlayNext(string arg) => SingleResultInList("Next", $"Skip: {_client.PlaybackContext.song.title}", action: _client.Skip);
        private List<Result> PlayLast(string arg) => SingleResultInList("Last", $"Skip Backwards", action: _client.SkipBack);

        public List<Result> GetPlaying()
        {

            _logger.Info("GetPlaying");

            var playbackContext = _client.PlaybackContext;

            var song = playbackContext.song;

            var status = playbackContext.isPlaying ? "Now Playing" : "Paused";
            var toggleAction = playbackContext.isPlaying ? "Pause" : "Resume";


            _logger.Debug(JsonSerializer.Serialize(playbackContext));

            var icon = _client.GetArtworkAsync(song);

            return new List<Result>() {
                SingleResultInList(
                    song.title ?? "Not Available",
                    $"{status} | by {song.artist}",
                    icon != null ? icon.Result : YtmIcon,
                    score: 5
                ).First(),
                SingleResultInList(
                    "Pause / Resume",
                    $"{toggleAction}: {song.title}",
                    action: async () =>
                    {
                        if (playbackContext.isPlaying)
                        {
                            _client.Pause();
                        }
                        else
                        {
                            _client.Play();
                        }

                        await Task.Delay(500);


                        RefreshDisplayInfo();
                    },
                    hideAfterAction: false,
                    requery: true
                    ).First(),
                PlayNext(string.Empty).First(),
                PlayLast(string.Empty).First(),
                ToggleMute().First(),
                Shuffle().First(),
                ToggleRepeat().First(),
                SetVolume().First(),
            };
        }

        public List<Result> ToggleMute(string arg = null) 
        {
            var toggleAction = _client.MuteStatus ? "Unmute" : "Mute";
            return SingleResultInList("Toggle Mute", $"{toggleAction}: {_client.CurrentPlaybackName}", action: _client.ToggleMute);
        }
        public List<Result> Shuffle(string arg = null) => SingleResultInList("Shuffle", "Shuffle queue", action: _client.Shuffle);
        public List<Result> ToggleRepeat(string arg = null) {
            var currentRepeatStatus = _client.RepeatStatus;
            var nextRepeatStatus = _client.GetNextRepeatAction(currentRepeatStatus);
            var toggleAction = nextRepeatStatus switch
            {
                YtmPluginClient.RepeatState.None => "Repeat Off",
                YtmPluginClient.RepeatState.All => "Repeat Current Queue",
                YtmPluginClient.RepeatState.One => "Repeat Current Song",
                _ => "Unknown repeat status"
            };
            return SingleResultInList("Toggle Repeat", $"{toggleAction}: {_client.CurrentPlaybackName}", action: _client.ToggleRepeat);
        }
        
        private struct SetVolAction
        {
            public enum VolAction {
                DISPLAY,
                ABSOLUTE,
                DECREASE,
                INCREASE
            }

            public VolAction action;
            public int target;
            public int current;
            public bool validAction;

            public SetVolAction(string actionString, int current)
            {
                this.validAction = false;
                this.target = -1;
                this.current = current;

                if (string.IsNullOrWhiteSpace(actionString))
                {
                    this.action = VolAction.DISPLAY;
                    return;
                }
                
                string intString = actionString;
                this.action = VolAction.ABSOLUTE;
                switch (actionString[0])
                {
                    case '+':
                         this.action = VolAction.INCREASE;
                         intString = actionString.Substring(1);
                         break;
                    case '-':
                        this.action = VolAction.DECREASE;
                        intString = actionString.Substring(1);
                        break;

                    default:
                        break;
                }

                if (int.TryParse(intString, out var amt))
                {
                    switch (this.action)
                    {
                        case VolAction.ABSOLUTE:
                            this.target = amt;
                            break;
                        case VolAction.INCREASE:
                            this.target = this.current + amt;
                            if (this.target > 100) this.target = 100;
                            break;
                        case VolAction.DECREASE:
                            this.target = this.current - amt;
                            if (this.target < 0) this.target = 0;
                        break;
                    }

                    if (this.target is >= 0 and <= 100)
                    {
                        this.validAction = true;
                        return;
                    }
                }

                this.action = VolAction.DISPLAY;
            }
        }
        public List<Result> SetVolume(string arg = null) 
        {
            var cachedVolume = _client.CurrentVolume;
            SetVolAction volAction = new SetVolAction(arg, cachedVolume);

            if (volAction.validAction)
            {
                return SingleResultInList($"Set Volume to {volAction.target}", $"Current Volume: {cachedVolume}", action: () =>
                {
                    _client.SetVolume(volAction.target);
                });
            }

            return SingleResultInList("Volume", $"Current Volume: {cachedVolume}", action: () => { });

        }

        private void RefreshDisplayInfo() => _context.API.ChangeQuery(currentQuery, true);
    }
}