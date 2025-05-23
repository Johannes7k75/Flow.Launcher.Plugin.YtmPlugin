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

            _ = Task.Run(async () =>
            {
                _client = new YtmPluginClient(_context.CurrentPluginMetadata.PluginDirectory);
                await _client.ConnectAsync();
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

            _terms.Add("reconnect", Reconnect);



            return Task.CompletedTask;
        }
        public async Task<List<Result>> QueryAsync(Query query, CancellationToken token)
        {
            currentQuery = query.RawQuery;

            if (!_client.IsConnected)
            {
                await _client.ConnectAsync();
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
        

        public struct BoundedAction<T>
        {
            public enum ActionType
            {
                DISPLAY,
                ABSOLUTE,
                INCREASE,
                DECREASE
            }

            public ActionType Action { get; private set; }
            public T Target { get; private set; }
            public T Current { get; }
            public bool IsValid { get; private set; }

            private readonly T min;
            private readonly T max;
            private readonly Func<string, T?> parser;
            private readonly Func<T, T, T> add;
            private readonly Func<T, T, T> subtract;
            private readonly Func<T, T, T> clamp;

            public BoundedAction(string input, T current, T min, T max,
                Func<string, T?> parser,
                Func<T, T, T> add,
                Func<T, T, T> subtract,
                Func<T, T, T> clamp = null)
            {
                this.Current = current;
                this.min = min;
                this.max = max;
                this.parser = parser;
                this.add = add;
                this.subtract = subtract;
                this.clamp = clamp ?? ((value, _) => value);  // no clamp by default

                Action = ActionType.DISPLAY;
                Target = current;
                IsValid = false;

                if (string.IsNullOrWhiteSpace(input))
                    return;

                string numericPart = input;
                Action = ActionType.ABSOLUTE;

                switch (input[0])
                {
                    case '+':
                        Action = ActionType.INCREASE;
                        numericPart = input.Substring(1);
                        break;
                    case '-':
                        Action = ActionType.DECREASE;
                        numericPart = input.Substring(1);
                        break;
                }

                var parsed = parser(numericPart);
                if (parsed != null)
                {
                    Target = Action switch
                    {
                        ActionType.INCREASE => add(current, parsed),
                        ActionType.DECREASE => subtract(current, parsed),
                        _ => parsed
                    };

                    if (Comparer<T>.Default.Compare(Target, min) < 0)
                        Target = min;
                    if (Comparer<T>.Default.Compare(Target, max) > 0)
                        Target = max;

                    Target = clamp(Target, current); // optional
                    IsValid = true;
                }
            }
        }


        public List<Result> SetVolume(string arg = null) 
        {
            var cachedVolume = _client.CurrentVolume;
            BoundedAction<int> volAction = new(
                    input: arg, 
                    current: cachedVolume,
                    min: 0,
                    max: 100,
                    parser: int.Parse,
                    add: (a,b)=>a+b,
                    subtract: (a,b)=>a-b
                    );

            if (volAction.IsValid)
            {
                return SingleResultInList($"Set Volume to {volAction.Target}", $"Current Volume: {cachedVolume}", action: () =>
                {
                    _client.SetVolume(volAction.Target);
                });
            }

            return SingleResultInList("Volume", $"Current Volume: {cachedVolume}", action: () => { });

        }

        private List<Result> Reconnect(string arg = null) => SingleResultInList("Reconnect", "Force a reconnection", action: async () =>
        {
               await _client.ReconnectAsync();
                _context.API.ChangeQuery(_context.CurrentPluginMetadata.ActionKeywords[0] + " ", true);

        });

        private void RefreshDisplayInfo() => _context.API.ChangeQuery(currentQuery, true);
    }
}