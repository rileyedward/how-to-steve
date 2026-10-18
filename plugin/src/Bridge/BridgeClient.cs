using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HowToSteve.Bridge
{
    /// <summary>
    /// WebSocket client to the bridge (protocol.md §1). Runs entirely off the Unity main thread:
    /// one connect/reconnect loop, one receive loop, one send loop and a heartbeat per connection.
    /// Anything that needs game state is handed to <see cref="MainThread"/>.
    /// Never throws into the game; while disconnected it just retries with backoff.
    /// </summary>
    internal sealed class BridgeClient
    {
        private static readonly int[] BackoffSeconds = { 1, 2, 4, 8, 10 };
        private const int MaxMissedPongs = 3;

        private readonly Uri _url;
        private readonly TimeSpan _pingInterval;
        private readonly Func<HelloPayload> _helloFactory;
        private readonly ManualLogSource _log;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly ConcurrentDictionary<string, long> _pendingPings = new ConcurrentDictionary<string, long>();
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private ConcurrentQueue<string> _sendQueue;
        private SemaphoreSlim _sendSignal;
        private long _seq;

        /// <summary>Raised on a thread-pool thread for every valid incoming envelope.</summary>
        public event Action<Envelope> MessageReceived;
        /// <summary>Raised on a thread-pool thread with the measured round trip of each ping.</summary>
        public event Action<double> PongReceived;

        /// <summary>Raised on a thread-pool thread when a connection ends (after it had been established).</summary>
        public event Action Disconnected;

        public bool IsConnected { get; private set; }

        public BridgeClient(string url, float pingIntervalSeconds, Func<HelloPayload> helloFactory, ManualLogSource log)
        {
            _url = new Uri(url);
            if (!_url.IsLoopback) throw new ArgumentException($"BridgeUrl must be loopback (local-only), got {url}");
            _pingInterval = TimeSpan.FromSeconds(Math.Max(1f, pingIntervalSeconds));
            _helloFactory = helloFactory;
            _log = log;
        }

        public void Start() => Task.Run(RunAsync);

        public void Stop() => _stop.Cancel();

        /// <summary>Thread-safe. Dropped silently while disconnected.</summary>
        public void Send(string type, object payload, long? re = null)
        {
            var queue = _sendQueue;
            if (!IsConnected || queue == null) return;
            var env = new Envelope
            {
                Type = type,
                Seq = Interlocked.Increment(ref _seq),
                Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Re = re,
                Payload = payload == null ? new JObject() : JObject.FromObject(payload),
            };
            queue.Enqueue(JsonConvert.SerializeObject(env));
            _sendSignal?.Release();
        }

        private async Task RunAsync()
        {
            var attempt = 0;
            while (!_stop.IsCancellationRequested)
            {
                using (var ws = new ClientWebSocket())
                using (var connCts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
                {
                    try
                    {
                        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(connCts.Token))
                        {
                            timeout.CancelAfter(TimeSpan.FromSeconds(5));
                            await ws.ConnectAsync(_url, timeout.Token).ConfigureAwait(false);
                        }

                        attempt = 0;
                        _log.LogInfo($"connected to bridge {_url}");
                        await RunConnectionAsync(ws, connCts).ConfigureAwait(false);
                    }
                    catch (Exception e) when (!(_stop.IsCancellationRequested))
                    {
                        if (IsConnected) _log.LogWarning($"bridge connection lost: {Describe(e)}");
                        else if (attempt == 0) _log.LogInfo($"bridge not reachable at {_url} ({Describe(e)})");
                    }
                    catch (Exception)
                    {
                        // shutting down
                    }
                    finally
                    {
                        if (IsConnected)
                        {
                            IsConnected = false;
                            try { Disconnected?.Invoke(); } catch (Exception e) { _log.LogError($"disconnect handler failed: {e}"); }
                        }
                        connCts.Cancel();
                        _pendingPings.Clear();
                    }
                }

                if (_stop.IsCancellationRequested) break;
                var delay = BackoffSeconds[Math.Min(attempt, BackoffSeconds.Length - 1)];
                attempt++;
                _log.LogInfo($"reconnecting to bridge in {delay}s (attempt {attempt})");
                try { await Task.Delay(TimeSpan.FromSeconds(delay), _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        private async Task RunConnectionAsync(ClientWebSocket ws, CancellationTokenSource connCts)
        {
            _sendQueue = new ConcurrentQueue<string>();
            _sendSignal = new SemaphoreSlim(0);
            IsConnected = true;

            var token = connCts.Token;
            var sendLoop = SendLoopAsync(ws, _sendQueue, _sendSignal, token);
            var heartbeat = HeartbeatLoopAsync(ws, connCts);

            Send(MessageTypes.Hello, _helloFactory());

            try
            {
                await ReceiveLoopAsync(ws, token).ConfigureAwait(false);
            }
            finally
            {
                connCts.Cancel();
                try { await Task.WhenAll(sendLoop, heartbeat).ConfigureAwait(false); } catch { /* cancelled */ }
            }
        }

        private async Task SendLoopAsync(ClientWebSocket ws, ConcurrentQueue<string> queue, SemaphoreSlim signal, CancellationToken token)
        {
            // ClientWebSocket allows one outstanding SendAsync at a time, so all sends go through this loop.
            while (!token.IsCancellationRequested)
            {
                await signal.WaitAsync(token).ConfigureAwait(false);
                while (queue.TryDequeue(out var json))
                {
                    var bytes = Encoding.UTF8.GetBytes(json);
                    await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
                }
            }
        }

        private async Task HeartbeatLoopAsync(ClientWebSocket ws, CancellationTokenSource connCts)
        {
            var token = connCts.Token;
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(_pingInterval, token).ConfigureAwait(false);
                if (_pendingPings.Count >= MaxMissedPongs)
                {
                    _log.LogWarning($"{MaxMissedPongs} pongs missed, dropping bridge connection");
                    ws.Abort();
                    connCts.Cancel();
                    return;
                }
                var nonce = Guid.NewGuid().ToString("N").Substring(0, 12);
                _pendingPings[nonce] = _clock.ElapsedTicks;
                Send(MessageTypes.Ping, new PingPayload { Nonce = nonce });
            }
        }

        private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken token)
        {
            var buffer = new byte[16 * 1024];
            using (var message = new MemoryStream())
            {
                while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _log.LogInfo($"bridge closed the connection ({(int?)result.CloseStatus} {result.CloseStatusDescription})");
                        try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false); }
                        catch { /* already gone */ }
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                    if (!result.EndOfMessage) continue;

                    var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                    message.SetLength(0);
                    Handle(text);
                }
            }
        }

        private void Handle(string text)
        {
            Envelope env;
            try { env = JsonConvert.DeserializeObject<Envelope>(text); }
            catch (Exception e)
            {
                _log.LogWarning($"bad message from bridge: {e.Message}");
                return;
            }
            if (env == null || string.IsNullOrEmpty(env.Type)) { _log.LogWarning("bad message from bridge: no type"); return; }
            if (env.V != Envelope.ProtocolVersion)
            {
                _log.LogError($"bridge speaks protocol v{env.V}, plugin speaks v{Envelope.ProtocolVersion}");
                return;
            }

            switch (env.Type)
            {
                case MessageTypes.Ping:
                    Send(MessageTypes.Pong, new PingPayload { Nonce = env.PayloadAs<PingPayload>()?.Nonce }, env.Seq);
                    break;
                case MessageTypes.Pong:
                    var nonce = env.PayloadAs<PingPayload>()?.Nonce;
                    if (nonce != null && _pendingPings.TryRemove(nonce, out var sentTicks))
                    {
                        // Any pong proves the link is alive; older unanswered pings no longer count as missed.
                        _pendingPings.Clear();
                        var rttMs = (_clock.ElapsedTicks - sentTicks) * 1000.0 / Stopwatch.Frequency;
                        PongReceived?.Invoke(rttMs);
                    }
                    break;
                default:
                    MessageReceived?.Invoke(env);
                    break;
            }
        }

        private static string Describe(Exception e)
        {
            while (e.InnerException != null && (e is AggregateException || e is WebSocketException)) e = e.InnerException;
            return $"{e.GetType().Name}: {e.Message}";
        }
    }
}
