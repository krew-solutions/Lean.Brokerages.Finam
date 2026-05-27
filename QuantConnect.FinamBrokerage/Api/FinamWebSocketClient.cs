/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using QuantConnect.Logging;

namespace QuantConnect.Brokerages.Finam.Api
{
    /// <summary>
    /// Minimal WebSocket client for the Finam Trade API <c>tradingInfo</c> channel
    /// (<c>wss://api.finam.ru/ws</c>).
    /// </summary>
    /// <remarks>
    /// Built directly on <see cref="ClientWebSocket"/> so the JWT can be supplied via the
    /// <c>Authorization</c> header on connect — LEAN's <c>WebSocketClientWrapper</c> only exposes
    /// the <c>x-session-token</c> header. The JWT is also echoed into every subscription message's
    /// required <c>token</c> field for compatibility with the payload-token auth scheme.
    ///
    /// The client owns a single background loop that connects, pumps frames, and on disconnect
    /// re-authenticates (fresh JWT) and replays all tracked subscriptions with exponential backoff.
    /// </remarks>
    public sealed class FinamWebSocketClient : IDisposable
    {
        private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

        private readonly Uri _wsUri;
        private readonly Func<CancellationToken, Task<string>> _jwtProvider;
        private readonly ConcurrentDictionary<string, WsRequest> _subscriptions = new();
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        private ClientWebSocket _socket;
        private CancellationTokenSource _cts;
        private Task _runLoop;

        /// <summary>Raised for every decoded server envelope (DATA / ERROR / EVENT).</summary>
        public event Action<WsEnvelope> EnvelopeReceived;

        /// <summary>Raised on connection-level faults (socket closed / handshake error).</summary>
        public event Action<string> ConnectionError;

        public FinamWebSocketClient(string wsUrl, Func<CancellationToken, Task<string>> jwtProvider)
        {
            _wsUri = new Uri(string.IsNullOrEmpty(wsUrl) ? FinamConstants.DefaultWsEndpoint : wsUrl);
            _jwtProvider = jwtProvider ?? throw new ArgumentNullException(nameof(jwtProvider));
        }

        public bool IsOpen => _socket?.State == WebSocketState.Open;

        /// <summary>Starts the background connect/maintain loop. Idempotent.</summary>
        public void Start(CancellationToken externalToken)
        {
            if (_runLoop != null) return;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
            _runLoop = Task.Run(() => RunAsync(_cts.Token), _cts.Token);
        }

        public Task SubscribeQuotesAsync(string brokerageSymbol) =>
            SendSubscriptionAsync(WsAction.Subscribe, WsSubscriptionType.Quotes,
                Key(WsSubscriptionType.Quotes, brokerageSymbol),
                new() { ["symbols"] = new[] { brokerageSymbol } });

        public Task SubscribeInstrumentTradesAsync(string brokerageSymbol) =>
            SendSubscriptionAsync(WsAction.Subscribe, WsSubscriptionType.InstrumentTrades,
                Key(WsSubscriptionType.InstrumentTrades, brokerageSymbol),
                new() { ["symbol"] = brokerageSymbol });

        public Task UnsubscribeQuotesAsync(string brokerageSymbol) =>
            SendSubscriptionAsync(WsAction.Unsubscribe, WsSubscriptionType.Quotes,
                Key(WsSubscriptionType.Quotes, brokerageSymbol),
                new() { ["symbols"] = new[] { brokerageSymbol } });

        public Task UnsubscribeInstrumentTradesAsync(string brokerageSymbol) =>
            SendSubscriptionAsync(WsAction.Unsubscribe, WsSubscriptionType.InstrumentTrades,
                Key(WsSubscriptionType.InstrumentTrades, brokerageSymbol),
                new() { ["symbol"] = brokerageSymbol });

        public Task SubscribeOrdersAsync(string accountId) =>
            SendSubscriptionAsync(WsAction.Subscribe, WsSubscriptionType.Orders,
                Key(WsSubscriptionType.Orders, accountId),
                new() { ["account_id"] = accountId });

        public Task SubscribeAccountTradesAsync(string accountId) =>
            SendSubscriptionAsync(WsAction.Subscribe, WsSubscriptionType.Trades,
                Key(WsSubscriptionType.Trades, accountId),
                new() { ["account_id"] = accountId });

        private async Task SendSubscriptionAsync(string action, string type, string key, System.Collections.Generic.Dictionary<string, object> data)
        {
            var request = new WsRequest { Action = action, Type = type, Data = data };

            if (action == WsAction.Subscribe)
            {
                _subscriptions[key] = request;
            }
            else
            {
                _subscriptions.TryRemove(key, out _);
            }

            if (IsOpen)
            {
                try
                {
                    await SendAsync(request, _cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Debug($"FinamWebSocketClient.SendSubscription({type}): {ex.Message} (will retry on reconnect)");
                }
            }
        }

        private async Task RunAsync(CancellationToken ct)
        {
            var backoff = MinBackoff;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await ConnectAndReplayAsync(ct).ConfigureAwait(false);
                    backoff = MinBackoff;
                    await ReceiveLoopAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    ConnectionError?.Invoke(ex.Message);
                    Log.Trace($"FinamWebSocketClient: connection lost ({ex.Message}); reconnecting in {backoff.TotalSeconds:0}s");
                }
                finally
                {
                    DisposeSocket();
                }

                if (ct.IsCancellationRequested) break;
                try { await Task.Delay(backoff, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                backoff = TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, backoff.TotalSeconds * 2));
            }
        }

        private async Task ConnectAndReplayAsync(CancellationToken ct)
        {
            var jwt = await _jwtProvider(ct).ConfigureAwait(false);

            _socket = new ClientWebSocket();
            _socket.Options.SetRequestHeader("Authorization", jwt);
            _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            await _socket.ConnectAsync(_wsUri, ct).ConfigureAwait(false);
            Log.Trace($"FinamWebSocketClient: connected to {_wsUri}");

            foreach (var sub in _subscriptions.Values)
            {
                await SendAsync(sub, ct).ConfigureAwait(false);
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            var buffer = new byte[16 * 1024];
            var builder = new StringBuilder();

            while (!ct.IsCancellationRequested && _socket?.State == WebSocketState.Open)
            {
                builder.Clear();
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        // Trigger the reconnect path in RunAsync (which catches any exception).
                        throw new InvalidOperationException(
                            $"WebSocket closed by server: {result.CloseStatus} {result.CloseStatusDescription}");
                    }
                    builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
                while (!result.EndOfMessage);

                Dispatch(builder.ToString());
            }
        }

        private void Dispatch(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return;
            try
            {
                var envelope = JsonConvert.DeserializeObject<WsEnvelope>(json);
                if (envelope != null)
                {
                    EnvelopeReceived?.Invoke(envelope);
                }
            }
            catch (Exception ex)
            {
                Log.Debug($"FinamWebSocketClient.Dispatch: failed to parse frame: {ex.Message}");
            }
        }

        private async Task SendAsync(WsRequest request, CancellationToken ct)
        {
            request.Token = await _jwtProvider(ct).ConfigureAwait(false);
            var payload = JsonConvert.SerializeObject(request);
            var bytes = Encoding.UTF8.GetBytes(payload);

            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private void DisposeSocket()
        {
            try { _socket?.Dispose(); } catch { /* ignore */ }
            _socket = null;
        }

        private static string Key(string type, string symbol) => $"{type}|{symbol}";

        public void Dispose()
        {
            try { _cts?.Cancel(); } catch { /* ignore */ }
            try { _runLoop?.Wait(TimeSpan.FromSeconds(5)); } catch { /* ignore */ }
            DisposeSocket();
            _cts?.Dispose();
            _sendLock.Dispose();
        }
    }
}
