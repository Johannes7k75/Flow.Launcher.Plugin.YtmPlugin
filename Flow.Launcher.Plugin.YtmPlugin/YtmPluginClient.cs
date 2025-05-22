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
        private readonly IPublicAPI _api;
        private YtmApi _ytmApi;
        private readonly object _lock = new object();
        private int mLastVolume = 10;
        private string pluginDirectory;
        private const string unknowicon = "icon.png";

        private string CacheFolder { get; }

        public YtmPluginClient(IPublicAPI api, string pluginDir = null)
        {
            _api = api;
            pluginDirectory = pluginDir ?? Directory.GetCurrentDirectory();
            CacheFolder = Path.Combine(pluginDirectory, "Cache");

            if (!Directory.Exists(CacheFolder)) Directory.CreateDirectory(CacheFolder);
        }

        public bool IsConnected => PlaybackContext != null;

        public async Task Connect()
        {
            lock (_lock)
            {
                _ytmApi = new YtmApi(_api, "localhost", "26539");
                _ytmApi.OnPlayerStateReceived += (state) =>
                {
                    _api.LogDebug("YtmPlugin", "🎵 Now playing: " + state.song?.title);
                    _api.LogDebug("YtmPlugin", "👤 Artist: " + state.song?.artist);
                    _api.LogDebug("YtmPlugin", "▶️ Is playing: " + state.isPlaying);
                    _api.LogDebug("YtmPlugin", "⏱ Position: " + state.position + " sec");
                    _api.LogDebug("YtmPlugin", "🔁 Repeat: " + state.repeat);
                };
                Task.Run(async () => await _ytmApi.ConnectAsync());
            }
        }

        public PlayerState PlaybackContext
        {
            get
            {
                return _ytmApi?.PlaybackContext;
            }
        }

        public bool MuteStatus
        { 
            get
            {
                return PlaybackContext?.volume == 0;
            }
        }

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
