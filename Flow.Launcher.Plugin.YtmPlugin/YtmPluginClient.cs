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


        public void AddOnSongUpdate(Action<ResolvedSongInfo> cb)
        {
            _ytmApi.OnSongUpdate += cb;
        }


        public bool IsConnected => PlaybackContext != null;

        public bool MuteStatus
        {
            get { return PlaybackContext?.muted ?? false; }
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
                return PlaybackContext.repeat switch
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
            get { return (int)PlaybackContext.volume; }
        }

        public string CurrentPlaybackName
        {
            get { return PlaybackContext.song.title ?? "Unknown"; }
        }

        public ResolvedPlayerState PlaybackContext
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
                    YtmPlugin._logger.Debug("👤 Artist: " + state.song?.artist);
                    YtmPlugin._logger.Debug("▶️ Is playing: " + state.isPlaying);
                    YtmPlugin._logger.Debug("⏱ Position: " + state.position + " sec");
                    YtmPlugin._logger.Debug("🔁 Repeat: " + state.repeat);
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
            if (!PlaybackContext.isPlaying)
            {
                _ytmApi.ResumePlayback();
            }
        }

        public void Pause()
        {
            YtmPlugin._logger.Info($"IsPlaying: {PlaybackContext.isPlaying.ToString()}");
            if (PlaybackContext.isPlaying)
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
            var currentPosition = PlaybackContext.position;

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


        public Task<string> GetArtworkAsync(ResolvedPlayerState state) => GetArtworkAsync(state.song);
        public Task<string> GetArtworkAsync(ResolvedSongInfo song) => GetArtworkAsync(song.imageSrc, song.videoId);

        public Task<string> GetArtworkAsync(PlayerState state) => GetArtworkAsync(state.song);
        public Task<string> GetArtworkAsync(SongInfo song) => GetArtworkAsync(song.imageSrc, song.videoId);

        private async Task<string> GetArtworkAsync(string url, string uniqueId)
        {
            return await DownloadImageAaync(uniqueId, url);
        }

        private async Task<string> DownloadImageAaync(string uniqueId, string url)
        {
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
