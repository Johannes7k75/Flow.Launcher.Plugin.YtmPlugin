using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Flow.Launcher.Plugin.YtmPlugin
{
    public class YtmPlugin : IAsyncPlugin
    {
        private PluginInitContext _context;

        private YtmPluginClient _client;

        private DateTime lastQueryTime;
        private string currentQuery;

        private const string YtmIcon = "icon.png";

        private bool optimizeclientUsage = false;
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
                try
                {
                    await _client.ConnectAsync();
                    _logger.Info("Conndted to websocket");
                }
                catch (Exception ex) { }
                _client.AddOnSongUpdate(song => RefreshDisplayInfo());
                _client.AddOnStateUpdate(state => RefreshDisplayInfo());
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
            _terms.Add("seek", SetPosition);

            _terms.Add("reconnect", Reconnect);



            return Task.CompletedTask;
        }
        public async Task<List<Result>> QueryAsync(Query query, CancellationToken token)
        {
            try
            {
                currentQuery = query.RawQuery;
                _logger.Info("YTM Query");

                if (_client == null || _client.PlaybackContext == null)
                {
                    return SingleResultInList("Initializing...", "Connecting to YouTube Music");
                }

                var playbackContext = _client.PlaybackContext;
                string contextInfo = playbackContext != null ? playbackContext.ToString() : "No playback data";

                _logger.Info($"Connected: {_client.IsConnected} {contextInfo}");

                if (!_client.IsConnected)
                {
                    return SingleResultInList("Not connected - Reconnect",
                        subtitle: "Flow Launcher is not connected to YouTube Music. Reconnect",
                        action: () =>
                        {
                            Reconnect().First().Action.Invoke(null);
                            _context.API.ChangeQuery(currentQuery, true);
                        },
                        hideAfterAction: false,
                        requery: true);
                }


                List<Result> results;

                if (string.IsNullOrWhiteSpace(query.Search))
                {
                    return GetPlaying();
                }

                if (_terms.TryGetValue(query.FirstSearch, out var termHandler))
                {
                    return termHandler.Invoke(query.SecondToEndSearch);
                }

                if (optimizeclientUsage)
                {
                    await Task.Delay(OptimizeClientKeyDelay, token);
                    if (token.IsCancellationRequested)
                    {
                        return new List<Result>();
                    }
                }

                if (_expensiveTerms.TryGetValue(query.FirstSearch, out var expensiveHandler))
                {

                    return await expensiveHandler.Invoke(query.SecondToEndSearch);
                }

                return GetPlaying();

            }
            catch (Exception ex)
            {
                _logger.Exception($"Query processing error: {ex.Message}", ex);
                return SingleResultInList("YouTube Music Error", ex.Message);
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


        private List<Result> Play(string arg) => SingleResultInList("Play", $"Resume: {_client?.PlaybackContext?.Song?.Title}", action: _client.Play);
        private List<Result> Pause(string arg = null) => SingleResultInList("Pause", $"Pause: {_client?.PlaybackContext?.Song?.Title}", action: _client.Pause);
        private List<Result> PlayNext(string arg) => SingleResultInList("Next", $"Skip: {_client?.PlaybackContext?.Song?.Title}", action: _client.Skip, hideAfterAction: false, requery: true);
        private List<Result> PlayLast(string arg) => SingleResultInList("Last", $"Skip Backwards", action: _client.SkipBack);

        public List<Result> GetPlaying()
        {
            try
            {
                _logger.Info("GetPlaying");
                var playbackContext = _client?.PlaybackContext;

                if (playbackContext == null || playbackContext?.Song == null)
                {
                    return SingleResultInList("No song playing", "Start playback in YouTube Music");
                }


                var song = playbackContext.Song;
                var status = playbackContext.IsPlaying ? "Now Playing" : "Paused";
                var toggleAction = playbackContext.IsPlaying ? "Pause" : "Resume";


                _logger.Debug(JsonSerializer.Serialize(playbackContext));


                string iconPath = YtmIcon;
                var artwork = _client.GetArtworkAsync(song);
                if (!string.IsNullOrEmpty(artwork.Result))
                {
                    iconPath = artwork.Result;
                }
               

                return new List<Result>() {
                    SingleResultInList(
                        title: string.IsNullOrEmpty(song.Title) ? "Not Available" : song.Title,
                        subtitle: $"{status} {SecondsToTime(_client.PlaybackContext.Position)}/{SecondsToTime(_client.PlaybackContext.Song.Duration)} | by {song.Artist}",
                        iconPath: iconPath,
                        score: 1000
                    ).First(),
                    SingleResultInList(
                        title: "Pause / Resume",
                        subtitle: $"{toggleAction}: {song.Title}",
                        action: () =>
                        {
                            if (playbackContext.IsPlaying)
                            {
                                _client.Pause();
                            }
                            else
                            {
                                _client.Play();
                            }
                        },
                        hideAfterAction: false,
                        requery: true,
                        score: 950
                    ).First(),
                    PlayNext(string.Empty).First(),
                    PlayLast(string.Empty).First(),
                    ToggleMute().First(),
                    Shuffle().First(),
                    SetPosition().First(),
                    ToggleRepeat().First(),
                    SetVolume().First(),
                };
            }
            catch (Exception ex)
            {
                _logger.Exception("Error in GetPlaying", ex);
                return SingleResultInList("Playback Error", ex.Message);

            }
        }

        public List<Result> ToggleMute(string arg = null)
        {
            var toggleAction = _client.MuteStatus ? "Unmute" : "Mute";
            return SingleResultInList("Toggle Mute", $"{toggleAction}: {_client.CurrentPlaybackName}", action: _client.ToggleMute);
        }
        public List<Result> Shuffle(string arg = null) => SingleResultInList("Shuffle", "Shuffle queue", action: _client.Shuffle);
        public List<Result> ToggleRepeat(string arg = null)
        {
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

        public string SecondsToTime(int value)
        {
            int minutes = value / 60;
            int seconds = value % 60;
            return $"{minutes}:{seconds}";
        }

        public List<Result> SetPosition(string arg = null)
        {
            int position = _client.PlaybackContext.Position;
            int duration = _client.PlaybackContext.Song.Duration;

            _logger.Info($"Position: {position}, Duration: {duration}");



            BoundedAction<int> seekAction = new(
                input: arg,
                current: position,
                min: 0,
                max: duration,
                parser: (intString) =>
                {
                    if (intString == null || intString.Length == 0) return 0;

                    if (intString.Contains(':'))
                    {
                        var splitted = intString.Split(':');
                        int minutes = int.Parse(splitted[0]);
                        int seconds = int.Parse(splitted[1]);

                        return minutes * 60 + seconds;
                    }
                    else
                    {
                        return int.Parse(intString);
                    }
                },
                add: (a, b) => a + b,
                subtract: (a, b) => a - b,
                clamp: (val, _) => val
                );
            if (seekAction.IsValid)
            {
                return SingleResultInList($"Set Volume to {SecondsToTime(seekAction.Target)}", $"Current Position: {SecondsToTime(position)}", action: () =>
                {
                    _client.SetPosition(seekAction.Target - position);
                });
            }

            return SingleResultInList("Position", $"Current Position: {SecondsToTime(position)}", action: () => { });
        }

        public List<Result> SetVolume(string arg = null)
        {
            var cachedVolume = _client.CurrentVolume;
            _logger.Info(arg + " " + cachedVolume);
            BoundedAction<int> volAction = new(
                    input: arg,
                    current: cachedVolume,
                    min: 0,
                    max: 100,
                    parser: (intString) =>
                    {
                        if (intString == null || intString.Length == 0) return 0;
                        return int.Parse(intString);
                    },
                    add: (a, b) => a + b,
                    subtract: (a, b) => a - b,
                    clamp: (val, _) => val
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

        private void RefreshDisplayInfo()
        {
            if (string.IsNullOrEmpty(currentQuery)) return;
            _context.API.ChangeQuery(currentQuery, true);
        }
    }
}