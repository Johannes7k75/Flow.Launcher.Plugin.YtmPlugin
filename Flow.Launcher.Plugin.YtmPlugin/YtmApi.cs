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
        private readonly IPublicAPI _api;

        private readonly string host;
        private readonly string port;
        private ClientWebSocket webSocket;
        private readonly CancellationTokenSource cts = new CancellationTokenSource();

        public event Action<PlayerState> OnPlayerStateReceived;

        private PlayerState _latestPlayerState;

        public YtmApi(IPublicAPI api, string host, string port)
        {
            this.host = host;
            this.port = port;
            this.webSocket = new ClientWebSocket();
            _api = api;
        }

        public async Task ConnectAsync()
        {
            var uri = new Uri($"ws://{host}:{port}");
            await webSocket.ConnectAsync(uri, CancellationToken.None);
            _api.LogInfo("YtmPlugin", $"✅ Connected to WebSocket server at {uri}");

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
                        _api.LogInfo("YtmPlugin", "⚠️ Server closed the connection.");
                        await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", token);
                        break;
                    }

                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);

                    var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("type", out var typeElement) &&
                        typeElement.GetString() == "PLAYER_STATE")
                    {
                        var deserializedJson = JsonSerializer.Deserialize<PlayerState>(json);
                        _api.LogDebug("YtmPlugin", json);
                        var state = UpdatePlayerState(deserializedJson);
                        if (state != null)
                        {
                            _latestPlayerState = state;               // ✅ store the latest state
                            OnPlayerStateReceived?.Invoke(state);     // Raise event
                        }
                    }
                }
                catch (Exception ex)
                {
                    _api.LogException("YtmPlugin", $"❌ Error: {ex.Message}", ex);
                    break;
                }
            }
        }

        private PlayerState UpdatePlayerState(PlayerState state)
        {
            var newState = _latestPlayerState != null ? _latestPlayerState : state;
            if (state.song != null)
            {
                newState.song = state.song;
                newState.position = state.song.elapsedSeconds;
            }

            if (state.isPlaying != null) newState.isPlaying = state.isPlaying;
            if (state.position != null && state.position != newState.position) newState.position = state.position;
            if (state.volume != null) newState.volume = state.volume;
            if (state.repeat != null) newState.repeat = state.repeat;

            return newState;
        }

        public async Task SendActionAsync(string action, object data = null)
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

        public PlayerState PlaybackContext
        {
            get
            {
                return _latestPlayerState;
            }
        }
    }


    // Helper types
    public class PlayerState
    {
        public string type { get; set; }
        public SongInfo song { get; set; }
        public bool isPlaying { get; set; }
        public int position { get; set; }
        public int volume { get; set; }
        public string repeat { get; set; }
    }

    public class SongInfo
    {
        public string title { get; set; }
        public string artist { get; set; }
        public string imageSrc { get; set; }
        public string album { get; set; }
        public string videoId { get; set; }

        [JsonPropertyName("isPaused")]
        public bool isPaused { get; set; }

        [JsonPropertyName("elapsedSeconds")]
        public int elapsedSeconds { get; set; }
    }
}
