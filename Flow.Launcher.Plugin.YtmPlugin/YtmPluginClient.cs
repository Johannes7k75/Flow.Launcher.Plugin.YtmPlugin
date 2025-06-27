using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace Flow.Launcher.Plugin.YtmPlugin
{
    public class YtmPluginClient
    {
        private YtmApi _ytmApi;
        private readonly object _lock = new object();
        private int mLastVolume = 10;
        private string pluginDirectory;
        private const string unknowicon = "icon.png";

        private string CacheFolder { get; }

        public YtmPluginClient(string pluginDir = null)
        {
            pluginDirectory = pluginDir ?? Directory.GetCurrentDirectory();
            CacheFolder = Path.Combine(pluginDirectory, "Cache");

            if (!Directory.Exists(CacheFolder)) Directory.CreateDirectory(CacheFolder);
        }


        public void AddOnSongUpdate(Action<SongInfo> cb)
        {
            _ytmApi.OnSongUpdate += cb;
        }

        public void AddOnStateUpdate(Action<PlayerState> cb)
        {
            _ytmApi.OnPlayerStateReceived += cb;
        }

        public bool IsConnected
        {
            get
            {
                try
                {
                    return _ytmApi.IsConnected;
                } catch
                {
                    return false;
                }
            }
        }
           

        public bool MuteStatus
        {
            get { return PlaybackContext.Muted; }
        }

        public enum RepeatState
        {
            None,
            One,
            All,
        }

        public RepeatState RepeatStatus
        {
            get
            {
                return PlaybackContext.Repeat switch
                {
                    "NONE" => RepeatState.None,
                    "ONE" => RepeatState.One,
                    "ALL" => RepeatState.All,
                    _ => throw new ArgumentOutOfRangeException()
                };
            }
        }

        public int CurrentVolume
        {
            get { return PlaybackContext.Volume; }
        }

        public string CurrentPlaybackName
        {
            get { return PlaybackContext.Song.Title ?? "Unknown"; }
        }

        public PlayerState PlaybackContext
        {
            get { return _ytmApi.PlaybackContext; }
        }

        public RepeatState GetNextRepeatAction(RepeatState currentStatus)
        {
            return currentStatus switch
            {
                RepeatState.None => RepeatState.All,
                RepeatState.All => RepeatState.One,
                RepeatState.One => RepeatState.None,
                _ => throw new ArgumentOutOfRangeException()
            };
        }




        public async Task ConnectAsync()
        {
            lock (_lock)
            {
                _ytmApi = new YtmApi("localhost", "26539");
                _ytmApi.OnPlayerStateReceived += (state) =>
                {
                    YtmPlugin._logger.Debug("🎵 Now playing: " + CurrentPlaybackName);
                    YtmPlugin._logger.Debug("👤 Artist: " + state.Song?.Artist);
                    YtmPlugin._logger.Debug("▶️ Is playing: " + state.IsPlaying);
                    YtmPlugin._logger.Debug("⏱ Position: " + state.Position + " sec");
                    YtmPlugin._logger.Debug("🔁 Repeat: " + state.Repeat);
                };

                Task.Run(async () => await _ytmApi.ConnectAsync());
            }
        }

        public async Task DisconnectAsync()
        { 
            await _ytmApi.DisconnectAsync();
            _ytmApi = null;
            YtmPlugin._logger.Info("🔌 Disconnected from YTM WebSocket.");
        }

        public async Task ReconnectAsync()
        { 
            if (IsConnected)
            {
                await DisconnectAsync();
            }
            await ConnectAsync();
        }



        public void Play()
        {
            if (!PlaybackContext.IsPlaying)
            {
                _ytmApi.ResumePlayback();
            }
        }

        public void Pause()
        {
            YtmPlugin._logger.Info($"IsPlaying: {PlaybackContext.IsPlaying.ToString()}");
            if (PlaybackContext.IsPlaying)
            {
                _ytmApi.PausePlayback();
            }
        }

        public void Skip()
        {
            _ytmApi.Skip();
        }

        public void SkipBack()
        {
            _ytmApi.SkipBack();
        }

        public void SetVolume(int volumePercent = 0)
        {
            var currentVolume = CurrentVolume;

            if (currentVolume == volumePercent) return;

            mLastVolume = currentVolume;
            _ytmApi.SetVolume(volumePercent);
        }

        public void SetPosition(int songPosition = 0)
        {
            var currentPosition = PlaybackContext.Position;

            if (currentPosition == songPosition) return;

            _ytmApi.SetPosition(songPosition);
        }


        public void Shuffle()
        {
            _ytmApi.Shuffle();
        }

        public void ToggleMute()
        {
            _ytmApi.Mute();
        }

        public void ToggleRepeat()
        {
            _ytmApi.Repeat();
        }


        public Task<string> GetArtworkAsync(PlayerState state) => GetArtworkAsync(state.Song);
        public Task<string> GetArtworkAsync(SongInfo song) => GetArtworkAsync(song.ImageSrc, song.VideoId);

        private async Task<string> GetArtworkAsync(string url, string uniqueId)
        {
            return await DownloadImageAsync(uniqueId, url);
        }

        private async Task<string> DownloadImageAsync(string uniqueId, string url)
        {
            if (uniqueId == string.Empty || url == string.Empty) return null;

            var path = $@"{CacheFolder}\{uniqueId}.jpg";

            if (File.Exists(path))
            {
                return path;
            }

            using var wc = new WebClient();
            await wc.DownloadFileTaskAsync(new Uri(url), path);

            return path;
        }


    }


}
