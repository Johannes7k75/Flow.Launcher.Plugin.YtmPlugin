using System;
using System.Collections.Generic;
using System.Linq;
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

        private readonly Dictionary<string, Func<string, List<Result>>> _terms = new(StringComparer.InvariantCultureIgnoreCase);
        private readonly Dictionary<string, Func<string, Task<List<Result>>>> _expensiveTerms = new(StringComparer.InvariantCultureIgnoreCase);


        public Task InitAsync(PluginInitContext context)
        {
            _context = context;
            lastQueryTime = DateTime.UtcNow;

            _ = Task.Run(() => _client = new YtmPluginClient(context.API, _context.CurrentPluginMetadata.PluginDirectory));

            _terms.Add("play", Play);

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
                _context.API.LogException("YtmPlugin", null, ex);
                return SingleResultInList("There was an error with your requet", ex.GetBaseException().Message);
            }
        }

        public List<Result> SingleResultInList(string title, string subtitle = "", string iconPath = YtmIcon, Action action = default, bool hideAfterAction = true, bool requery = true)
        {
            return new List<Result>()
            {
                new()
                {
                    Title = title,
                    SubTitle = subtitle,
                    IcoPath = iconPath,
                    Action = _ =>
                    {
                        action?.Invoke();
                        if (requery) RefreshDisplayInfo();

                        return hideAfterAction;
                    }
                }
            };
        }

        public List<Result> GetPlaying()
        {
            var playbackContext = _client.PlaybackContext;

            var song = playbackContext.song;
            var status = playbackContext.isPlaying ? "Now Playing" : "Paused";

            _context.API.LogDebug("YtmPlugin", JsonSerializer.Serialize(playbackContext));

            var icon = _client.GetArtworkAsync(song);

            return SingleResultInList(
                    song.title ?? "Not Available",
                    $"{status} | by {song.artist}",
                    icon != null ? icon.Result : YtmIcon
                );
        }

        public List<Result> Play(string arg) 
        {
            
            return new List<Result>();
        }

        private void RefreshDisplayInfo() => _context.API.ChangeQuery(currentQuery, true);
    }
}