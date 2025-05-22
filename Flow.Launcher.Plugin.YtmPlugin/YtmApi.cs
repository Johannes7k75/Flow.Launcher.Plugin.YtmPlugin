using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Flow.Launcher.Plugin.YtmPlugin
{
    public class YtmApi
    {
        private readonly string host;
        private readonly string port;
        private ClientWebSocket webSocket;
        private readonly CancellationTokenSource cts = new CancellationTokenSource();

        public event Action<ResolvedPlayerState> OnPlayerStateReceived;

        public event Action<ResolvedSongInfo> OnSongUpdate;

        private PlayerState? _latestPlayerState;

        public YtmApi(string host, string port)
        {
            this.host = host;
            this.port = port;
            this.webSocket = new ClientWebSocket();
        }

        public async Task ConnectAsync()
        {
            var uri = new Uri($"ws://{host}:{port}");
            await webSocket.ConnectAsync(uri, CancellationToken.None);
            YtmPlugin._logger.Info($"✅ Connected to WebSocket server at {uri}");

            _ = Task.Run(() => ReceiveLoopAsync(cts.Token));
        }

        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            var buffer = new byte[4096];

            while (webSocket.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                try
                {
                    var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        YtmPlugin._logger.Info("⚠️ Server closed the connection.");
                        await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", token);
                        break;
                    }

                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);

                    var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("type", out var typeElement) &&
                        typeElement.GetString() == "PLAYER_STATE")
                    {
                        var deserializedJson = JsonSerializer.Deserialize<PlayerState>(json);
                        YtmPlugin._logger.Info(json);
                        var state = UpdatePlayerState(deserializedJson);
                        if (state != null)
                        {
                            _latestPlayerState = state;               // ✅ store the latest state
                            OnPlayerStateReceived?.Invoke(state.ToResolved());     // Raise event with resolved state
                        }
                    }
                }
                catch (Exception ex)
                {
                    YtmPlugin._logger.Exception($"❌ Error: {ex.Message}", ex);
                    break;
                }
            }
        }

        private PlayerState UpdatePlayerState(PlayerState? state)
        {
            if (state == null)
                return _latestPlayerState ?? new PlayerState();

            if (_latestPlayerState == null)
                return state;

            var oldVideoId = _latestPlayerState.song.videoId;
            var newState = _latestPlayerState;


            if (state.song != null)
            {
                if (newState.song == null)
                    newState.song = new SongInfo();

                // Update fields individually to avoid losing existing data if partial update
                if (!string.IsNullOrEmpty(state.song.title)) newState.song.title = state.song.title;
                if (!string.IsNullOrEmpty(state.song.artist)) newState.song.artist = state.song.artist;
                if (!string.IsNullOrEmpty(state.song.album)) newState.song.album = state.song.album;
                if (!string.IsNullOrEmpty(state.song.imageSrc)) newState.song.imageSrc = state.song.imageSrc;
                if (!string.IsNullOrEmpty(state.song.videoId)) newState.song.videoId = state.song.videoId;

                newState.song.isPaused = state.song.isPaused;
                newState.song.elapsedSeconds = state.song.elapsedSeconds;

                // Keep position synced with song.elapsedSeconds if available
                newState.position = state.song.elapsedSeconds;
            }

            if (state.isPlaying.HasValue) newState.isPlaying = state.isPlaying;
            if (state.position.HasValue) newState.position = state.position;
            if (state.volume.HasValue) newState.volume = state.volume;
            if (state.muted.HasValue) newState.muted = state.muted;
            if (!string.IsNullOrEmpty(state.repeat)) newState.repeat = state.repeat;
            if (!string.IsNullOrEmpty(state.type)) newState.type = state.type;

            YtmPlugin._logger.Info($"Old VideoId: {oldVideoId}, New VideoId: {newState.song.videoId}");
            if (newState.song.videoId != oldVideoId)
            {
                YtmPlugin._logger.Info("Song update");
                OnSongUpdate?.Invoke(newState.ToResolved().song);
            }

            return newState;
        }

        public async Task SendActionAsync(string action, object? data = null)
        {
            if (webSocket.State != WebSocketState.Open) return;

            var message = data != null
                ? JsonSerializer.Serialize(new { type = "ACTION", action, data })
                : JsonSerializer.Serialize(new { type = "ACTION", action });

            var bytes = Encoding.UTF8.GetBytes(message);
            await webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        public async Task DisconnectAsync()
        {
            cts.Cancel();
            if (webSocket.State == WebSocketState.Open)
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client closing", CancellationToken.None);
        }

        public ResolvedPlayerState PlaybackContext => _latestPlayerState.ToResolved();

        // Convenience wrapper methods
        public void ResumePlayback() => _ = SendSingleActionAsync("play");
        public void PausePlayback() => _ = SendSingleActionAsync("pause");
        public void Skip() => _ = SendSingleActionAsync("next");
        public void SkipBack() => _ = SendSingleActionAsync("previous");
        public void Mute() => _ = SendSingleActionAsync("mute");
        public void Shuffle() => _ = SendSingleActionAsync("shuffle");
        public void Repeat() => _ = SendSingleActionAsync("repeat");
        public void SetVolume(int volumePercent = 0) => _ = SendDataActionAsync("setVolume", volumePercent);

        private async Task SendSingleActionAsync(string action)
        {
            if (webSocket.State != WebSocketState.Open) return;
            var message = JsonSerializer.Serialize(new { type = "ACTION", action });
            await SendActionAsync(message);
        }

        private async Task SendDataActionAsync(string action, object data)
        {
            if (webSocket.State != WebSocketState.Open) return;
            var message = JsonSerializer.Serialize(new { type = "ACTION", action, data });
            await SendActionAsync(message);
        }

        private async Task SendActionAsync(string message)
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            await webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
    }

    // Nullable model used for deserialization and partial updates
    public class PlayerState
    {
        [JsonRequired]
        public string? type { get; set; }
        public SongInfo? song { get; set; }
        public bool? isPlaying { get; set; }
        public bool? muted { get; set; }
        public int? position { get; set; }
        public int? volume { get; set; }
        public string? repeat { get; set; }
    }

    public class SongInfo
    {
        public string? title { get; set; }
        public string? artist { get; set; }
        public string? imageSrc { get; set; }
        public string? album { get; set; }
        public string? videoId { get; set; }

        [JsonPropertyName("isPaused")]
        public bool isPaused { get; set; }

        [JsonPropertyName("elapsedSeconds")]
        public int elapsedSeconds { get; set; }
    }

    // Non-nullable model for safe consumption
    public class ResolvedPlayerState
    {
        public string type { get; set; } = string.Empty;
        public ResolvedSongInfo song { get; set; } = new ResolvedSongInfo();
        public bool isPlaying { get; set; }
        public bool muted { get; set; }
        public int position { get; set; }
        public int volume { get; set; }
        public string repeat { get; set; } = "none";
    }

    public class ResolvedSongInfo
    {
        public string title { get; set; } = string.Empty;
        public string artist { get; set; } = string.Empty;
        public string imageSrc { get; set; } = string.Empty;
        public string album { get; set; } = string.Empty;
        public string videoId { get; set; } = string.Empty;
        public bool isPaused { get; set; }
        public int elapsedSeconds { get; set; }
    }

    // Extension method to convert nullable PlayerState to resolved non-nullable
    public static class PlayerStateExtensions
    {
        public static ResolvedPlayerState ToResolved(this PlayerState? state)
        {
            state ??= new PlayerState();

            return new ResolvedPlayerState
            {
                type = state.type ?? string.Empty,
                isPlaying = state.isPlaying ?? false,
                muted = state.muted ?? false,
                position = state.position ?? 0,
                volume = state.volume ?? 100,
                repeat = state.repeat ?? "none",
                song = new ResolvedSongInfo
                {
                    title = state.song?.title ?? string.Empty,
                    artist = state.song?.artist ?? string.Empty,
                    album = state.song?.album ?? string.Empty,
                    imageSrc = state.song?.imageSrc ?? string.Empty,
                    videoId = state.song?.videoId ?? string.Empty,
                    isPaused = state.song?.isPaused ?? false,
                    elapsedSeconds = state.song?.elapsedSeconds ?? 0
                }
            };
        }
    }
}
