using System;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
namespace Flow.Launcher.Plugin.YtmPlugin
{
    public class PlayerState
    {
        public string Type { get; set; } = "";
        public SongInfo Song { get; set; } = new SongInfo();
        public bool IsPlaying { get; set; }
        public bool Muted { get; set; }
        public int Position { get; set; }
        public int Volume { get; set; } = 100;
        public string Repeat { get; set; } = "none";
    }

    public class SongInfo
    {
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string ImageSrc { get; set; } = "";
        public string Album { get; set; } = "";
        public string VideoId { get; set; } = "";
        public int Duration { get; set; }
        public bool IsPaused { get; set; }
        public int ElapsedSeconds { get; set; }

        public string ToString()
        {
            return JsonSerializer.Serialize(this);
        }

    }

    [JsonConverter(typeof(PlayerStateConverter))]
    public class PlayerStateUpdate
    {
        [JsonPropertyName("type")]
        public string Type { get; set; }
        [JsonPropertyName("song")]
        public SongInfoUpdate Song { get; set; }
        [JsonPropertyName("isPlaying")]
        public bool? IsPlaying { get; set; }
        [JsonPropertyName("muted")]
        public bool? Muted { get; set; }
        [JsonPropertyName("position")]
        public int? Position { get; set; }
        [JsonPropertyName("volume")]
        public int? Volume { get; set; }
        [JsonPropertyName("repeat")]
        public string Repeat { get; set; }

        public string ToString()
        {
            return JsonSerializer.Serialize(this);
        }
    }

    public class SongInfoUpdate
    {
        [JsonPropertyName("title")]
        public string Title { get; set; }
        [JsonPropertyName("artist")]
        public string Artist { get; set; }
        [JsonPropertyName("imageSrc")]
        public string ImageSrc { get; set; }
        [JsonPropertyName("album")]
        public string Album { get; set; }
        [JsonPropertyName("videoId")]
        public string VideoId { get; set; }
        [JsonPropertyName("songDuration")]
        public int? Duration { get; set; }
        [JsonPropertyName("isPaused")]
        public bool? IsPaused { get; set; }
        [JsonPropertyName("elapsedSeconds")]
        public int? ElapsedSeconds { get; set; }
    }

    public class PlayerStateConverter : JsonConverter<PlayerStateUpdate>
    {
        public override PlayerStateUpdate Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options
        )
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;

            return new PlayerStateUpdate
            {
                Type = root.GetProperty("type").GetString(),
                Song = root.TryGetProperty("song", out var songElement)
                    ? JsonSerializer.Deserialize<SongInfoUpdate>(songElement.GetRawText(), options)
                    : null,
                IsPlaying = root.TryGetProperty("isPlaying", out var playingElement)
                    ? playingElement.GetBoolean()
                    : (bool?)null,
                Muted = root.TryGetProperty("muted", out var mutedElement)
                    ? mutedElement.GetBoolean()
                    : (bool?)null,
                Position = root.TryGetProperty("position", out var positionElement)
                    ? positionElement.GetInt32()
                    : (int?)null,
                Volume = root.TryGetProperty("volume", out var volumeElement)
                    ? volumeElement.GetInt32()
                    : (int?)null,
                Repeat = root.TryGetProperty("repeat", out var repeatElement)
                    ? repeatElement.GetString()
                    : null,
            };
        }

        public override void Write(Utf8JsonWriter writer, PlayerStateUpdate value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();

            if (value.Type != null)
                writer.WriteString("type", value.Type);

            if (value.Song != null)
            {
                writer.WritePropertyName("song");
                JsonSerializer.Serialize(writer, value.Song, options);
            }

            if (value.IsPlaying.HasValue)
                writer.WriteBoolean("isPlaying", value.IsPlaying.Value);

            if (value.Muted.HasValue)
                writer.WriteBoolean("muted", value.Muted.Value);

            if (value.Position.HasValue)
                writer.WriteNumber("position", value.Position.Value);

            if (value.Volume.HasValue)
                writer.WriteNumber("volume", value.Volume.Value);

            if (value.Repeat != null)
                writer.WriteString("repeat", value.Repeat);

            writer.WriteEndObject();
        }
    }

    public class YtmApi : IDisposable
    {
        private readonly string host;
        private readonly string port;
        private ClientWebSocket webSocket;
        private CancellationTokenSource cts = new CancellationTokenSource();
        private readonly object _stateLock = new object(); // Thread-safe state updates

        public event Action<PlayerState> OnPlayerStateReceived;
        public event Action<SongInfo> OnSongUpdate;

        private Task _receiveTask;
        private PlayerState _currentState = new PlayerState();


        public YtmApi(string host, string port)
        {
            this.host = host;
            this.port = port;
            this.webSocket = new ClientWebSocket();
        }

        public async Task ConnectAsync()
        {
            var uri = new Uri($"ws://{host}:{port}");

            // Recreate CTS if previously canceled
            if (cts.IsCancellationRequested)
            {
                cts.Dispose();
                cts = new CancellationTokenSource();
            }

            try
            {
                // Use linked token for connection timeout
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeoutCts.Token);

                await webSocket.ConnectAsync(uri, linkedCts.Token);
            }
            catch (Exception ex)
            {
                YtmPlugin._logger.Info($"⚠️ Connection failed: {ex.Message}");
                return;
            }

            if (webSocket.State == WebSocketState.Open)
            {
                YtmPlugin._logger.Info($"✅ Connected to WebSocket server at {uri}");
                _receiveTask = ReceiveLoopAsync(cts.Token);
            }
        }

        public bool IsConnected => webSocket?.State == WebSocketState.Open;

        public async Task DisconnectAsync()
        {
            try
            {
                // Cancel ongoing operations first
                cts.Cancel();

                if (webSocket.State == WebSocketState.Open)
                {
                    await webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Client closing",
                        CancellationToken.None
                    );
                }

                // Wait for receive loop to exit gracefully
                if (_receiveTask != null && !_receiveTask.IsCompleted)
                {
                    await Task.WhenAny(_receiveTask, Task.Delay(1000));
                }
            }
            finally
            {
                OnPlayerStateReceived = null;
                OnSongUpdate = null;
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            var buffer = new byte[4096];

            try
            {
                while (webSocket.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    // Process messages
                    var result = await webSocket.ReceiveAsync(
                        new ArraySegment<byte>(buffer),
                        token
                    );

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        YtmPlugin._logger.Info("⚠️ Server closed the connection.");
                        await webSocket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "Closing",
                            token
                        );
                        break;
                    }

                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    ProcessWebSocketMessage(json);
                }
            }
            catch (OperationCanceledException)
            {
                YtmPlugin._logger.Info("🔴 WebSocket operation canceled normally");
            }
            catch (WebSocketException ex) when (
                ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely ||
                ex.WebSocketErrorCode == WebSocketError.InvalidState)
            {
                YtmPlugin._logger.Info("🟠 WebSocket connection closed unexpectedly");
            }
            catch (Exception ex)
            {
                YtmPlugin._logger.Exception($"❌ WebSocket error: {ex.Message}", ex);
            }
            finally
            {
                // Cleanup resources
                if (webSocket.State == WebSocketState.Open)
                {
                    try
                    {
                        await webSocket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "Closing connection",
                            CancellationToken.None
                        );
                    }
                    catch { /* Ignore close errors during shutdown */ }
                }
            }
        }

        private void ProcessWebSocketMessage(string json)
        {
            try
            {
                if (!json.Contains("\"type\":\"PLAYER_STATE\"")) return;

                YtmPlugin._logger.Info($"RawJson: {json}");

                var update = JsonSerializer.Deserialize<PlayerStateUpdate>(json);
                YtmPlugin._logger.Info($"DeserializedJson: {update.ToString()}");



                lock (_stateLock)
                {
                    UpdatePlayerState(update);
                }
            }
            catch (Exception ex)
            {
                YtmPlugin._logger.Exception($"❌ JSON processing error: {ex.Message}", ex);
            }
        }

        private void UpdatePlayerState(PlayerStateUpdate update)
        {
            YtmPlugin._logger.Info(update + " " + update.ToString());

            if (update == null) return;

            if (update.IsPlaying.HasValue) _currentState.IsPlaying = update.IsPlaying.Value;
            if (update.Muted.HasValue) _currentState.Muted = update.Muted.Value;
            if (update.Position.HasValue) _currentState.Position = update.Position.Value;
            if (update.Volume.HasValue) _currentState.Volume = update.Volume.Value;
            if (!string.IsNullOrEmpty(update.Repeat)) _currentState.Repeat = update.Repeat;
            if (!string.IsNullOrEmpty(update.Type)) _currentState.Type = update.Type;

            if (update.Song != null)
            {
                var song = update.Song;
                var currentSong = _currentState.Song;
                var oldVideoId = currentSong.VideoId;

                if (!string.IsNullOrEmpty(song.Title)) currentSong.Title = song.Title;
                if (!string.IsNullOrEmpty(song.Artist)) currentSong.Artist = song.Artist;
                if (!string.IsNullOrEmpty(song.Album)) currentSong.Album = song.Album;
                if (!string.IsNullOrEmpty(song.ImageSrc)) currentSong.ImageSrc = song.ImageSrc;
                if (!string.IsNullOrEmpty(song.VideoId)) currentSong.VideoId = song.VideoId;
                if (song.Duration.HasValue) currentSong.Duration = song.Duration.Value;
                if (song.IsPaused.HasValue) currentSong.IsPaused = song.IsPaused.Value;
                if (song.ElapsedSeconds.HasValue) currentSong.ElapsedSeconds = song.ElapsedSeconds.Value;

                YtmPlugin._logger.Info($"New: {currentSong.VideoId}, Old: {oldVideoId}");

                if (currentSong.VideoId != oldVideoId)
                {
                    YtmPlugin._logger.Info($"🎵 Song changed: {oldVideoId} → {currentSong.VideoId}");
                    OnSongUpdate?.Invoke(currentSong);
                }
            }

            OnPlayerStateReceived?.Invoke(_currentState);
        }

        public async Task SendActionAsync(string action, object? data = null)
        {
            if (!IsConnected) return;

            try
            {
                var message = data != null
                    ? JsonSerializer.Serialize(new { type = "ACTION", action, data })
                    : JsonSerializer.Serialize(new { type = "ACTION", action });

                var bytes = Encoding.UTF8.GetBytes(message);
                await webSocket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    true,
                    cts.Token
                );
            }
            catch (Exception ex)
            {
                YtmPlugin._logger.Exception($"❌ Send action failed: {ex.Message}", ex);
            }
        }

        // Simplified action methods
        public Task ResumePlayback() => SendActionAsync("play");
        public Task PausePlayback() => SendActionAsync("pause");
        public Task Skip() => SendActionAsync("next");
        public Task SkipBack() => SendActionAsync("previous");
        public Task Mute() => SendActionAsync("mute");
        public Task Shuffle() => SendActionAsync("shuffle");
        public Task Repeat() => SendActionAsync("repeat");
        public Task SetVolume(int volumePercent) => SendActionAsync("setVolume", volumePercent);
        public Task SetPosition(int position) => SendActionAsync("seek", position);

        public PlayerState PlaybackContext
        {
            get
            {
                lock (_stateLock)
                {
                    return _currentState;
                }
            }
        }

        public void Dispose()
        {
            try
            {
                cts?.Cancel();
                webSocket?.Dispose();
                cts?.Dispose();
            }
            catch { /* Ignore disposal errors */ }
        }
    }

}