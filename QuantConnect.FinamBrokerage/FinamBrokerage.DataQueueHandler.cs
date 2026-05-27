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
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QuantConnect.Brokerages.Finam.Api;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Logging;
using QuantConnect.Packets;

namespace QuantConnect.Brokerages.Finam
{
    /// <summary>
    /// <see cref="IDataQueueHandler"/> half of the Finam brokerage.
    /// </summary>
    /// <remarks>
    /// Live data is served primarily by the Finam WebSocket stream (<see cref="FinamWebSocketClient"/>)
    /// as <see cref="Tick"/>s:
    /// <list type="bullet">
    ///   <item><c>QUOTES</c> → quote <see cref="Tick"/> (<c>TickType.Quote</c>);</item>
    ///   <item><c>INSTRUMENT_TRADES</c> → trade <see cref="Tick"/> (<c>TickType.Trade</c>).</item>
    /// </list>
    /// Bars are not pushed: the default <c>AggregationManager</c> consolidates these ticks into the
    /// requested resolution (tick-typed consolidators filter by <c>TickType</c>). Historical bars are
    /// served separately by <c>GetHistory</c> via REST.
    /// The REST <c>LastQuote</c> poll is retained as a <em>fallback</em>: it only emits for a symbol
    /// when the WebSocket is down or has not delivered data for that symbol within
    /// <see cref="FinamConstants.WebSocketStaleness"/> — keeping live data flowing if the socket
    /// drops, without double-feeding when the stream is healthy.
    /// </remarks>
    public partial class FinamBrokerage
    {
        private IDataAggregator _aggregator;
        private FinamWebSocketClient _webSocket;

        private readonly ConcurrentDictionary<Symbol, byte> _subscribedSymbols = new();
        private readonly ConcurrentDictionary<string, Symbol> _brokerageToLean = new();
        private readonly ConcurrentDictionary<Symbol, DateTime> _lastWsDataUtc = new();

        /// <inheritdoc />
        public IEnumerator<BaseData> Subscribe(SubscriptionDataConfig dataConfig, EventHandler newDataAvailableHandler)
        {
            if (!CanSubscribe(dataConfig.Symbol))
            {
                return null;
            }

            _subscribedSymbols.TryAdd(dataConfig.Symbol, 0);

            var brokerageSymbol = _symbolMapper.GetBrokerageSymbol(dataConfig.Symbol);
            _brokerageToLean[brokerageSymbol] = dataConfig.Symbol;

            OpenWebSocketSubscriptions(brokerageSymbol);

            var enumerator = _aggregator.Add(dataConfig, newDataAvailableHandler);
            Log.Trace($"FinamBrokerage.Subscribe: {dataConfig.Symbol.Value} ({dataConfig.Resolution})");
            return enumerator;
        }

        /// <inheritdoc />
        public void Unsubscribe(SubscriptionDataConfig dataConfig)
        {
            _subscribedSymbols.TryRemove(dataConfig.Symbol, out _);

            if (_webSocket != null)
            {
                var brokerageSymbol = _symbolMapper.GetBrokerageSymbol(dataConfig.Symbol);
                _ = _webSocket.UnsubscribeQuotesAsync(brokerageSymbol);
                _ = _webSocket.UnsubscribeInstrumentTradesAsync(brokerageSymbol);
            }

            _aggregator?.Remove(dataConfig);
        }

        /// <inheritdoc />
        public void SetJob(LiveNodePacket job)
        {
            // No-op: configuration is consumed by FinamBrokerageFactory at construction time.
        }

        bool IDataQueueHandler.IsConnected => IsConnected;

        private static bool CanSubscribe(Symbol symbol)
        {
            if (symbol == null || symbol.IsCanonical()) return false;
            return symbol.SecurityType is SecurityType.Equity or SecurityType.Future or SecurityType.Option or SecurityType.Index;
        }

        private void OpenWebSocketSubscriptions(string brokerageSymbol)
        {
            if (_webSocket == null) return;

            // Live bars are NOT requested from Finam's BARS stream: the default IDataAggregator
            // (AggregationManager) wires every bar subscription to a tick-based consolidator
            // (TickConsolidator / TickQuoteBarConsolidator), so it consumes ticks and builds the
            // bars itself. We therefore feed quote and trade ticks; LEAN consolidates to the
            // requested resolution. Historical bars are served separately via REST in GetHistory.
            _ = _webSocket.SubscribeQuotesAsync(brokerageSymbol);
            _ = _webSocket.SubscribeInstrumentTradesAsync(brokerageSymbol);
        }

        /// <summary>
        /// Brings up the WebSocket stream (primary) and the REST poll fallback. Invoked by
        /// <see cref="Connect"/> once the REST session is authenticated.
        /// </summary>
        private void StartStreaming(CancellationToken ct)
        {
            _webSocket = new FinamWebSocketClient(_wsUrl, _api.GetValidJwtAsync);
            _webSocket.EnvelopeReceived += OnWebSocketEnvelope;
            _webSocket.ConnectionError += msg => Log.Trace($"FinamBrokerage WS: {msg}");
            _webSocket.Start(ct);

            // Account-level push: own order state changes (ORDERS) and own executions (TRADES).
            _ = _webSocket.SubscribeOrdersAsync(_accountId);
            _ = _webSocket.SubscribeAccountTradesAsync(_accountId);

            // Replay subscriptions that arrived before the socket was up.
            foreach (var symbol in _subscribedSymbols.Keys)
            {
                var brokerageSymbol = _symbolMapper.GetBrokerageSymbol(symbol);
                _brokerageToLean[brokerageSymbol] = symbol;
                OpenWebSocketSubscriptions(brokerageSymbol);
            }

            StartRestFallbackLoop(ct);
        }

        private void OnWebSocketEnvelope(WsEnvelope envelope)
        {
            try
            {
                if (envelope.IsError)
                {
                    Log.Trace($"FinamBrokerage WS error: {envelope.ErrorInfo?.Type} {envelope.ErrorInfo?.Message}");
                    return;
                }
                if (envelope.IsEvent)
                {
                    Log.Trace($"FinamBrokerage WS event: {envelope.EventInfo?.Event} {envelope.EventInfo?.Reason}");
                    return;
                }
                if (!envelope.IsData || envelope.Payload == null)
                {
                    return;
                }

                switch (envelope.SubscriptionType)
                {
                    case WsSubscriptionType.Quotes:
                        EmitQuotes(envelope.Payload.ToObject<WsQuotePayload>());
                        break;
                    case WsSubscriptionType.InstrumentTrades:
                        EmitTrades(envelope.Payload.ToObject<WsTradesPayload>());
                        break;
                    case WsSubscriptionType.Trades:
                        OnAccountTrades(envelope.Payload.ToObject<WsAccountTradesPayload>());
                        break;
                    case WsSubscriptionType.Orders:
                        OnOrderStates(envelope.Payload.ToObject<WsOrdersPayload>());
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Debug($"FinamBrokerage.OnWebSocketEnvelope: {ex.Message}");
            }
        }

        private void EmitQuotes(WsQuotePayload payload)
        {
            if (payload?.Quote == null) return;

            foreach (var quote in payload.Quote)
            {
                if (!_brokerageToLean.TryGetValue(quote.Symbol ?? string.Empty, out var symbol)) continue;

                var bid = WsParse.Dec(quote.Bid);
                var ask = WsParse.Dec(quote.Ask);
                var time = quote.Timestamp == default ? DateTime.UtcNow : quote.Timestamp;
                if (bid > 0m && ask > 0m)
                {
                    _aggregator?.Update(new Tick(time, symbol, string.Empty, string.Empty,
                        WsParse.Dec(quote.BidSize), bid, WsParse.Dec(quote.AskSize), ask));
                    MarkWsData(symbol);
                }
            }
        }

        private void EmitTrades(WsTradesPayload payload)
        {
            if (payload?.Trades == null || !_brokerageToLean.TryGetValue(payload.Symbol ?? string.Empty, out var symbol))
            {
                return;
            }

            foreach (var trade in payload.Trades)
            {
                var price = WsParse.Dec(trade.Price);
                if (price <= 0m) continue;
                var time = trade.Timestamp == default ? DateTime.UtcNow : trade.Timestamp;
                _aggregator?.Update(new Tick(time, symbol, string.Empty, string.Empty, WsParse.Dec(trade.Size), price));
                MarkWsData(symbol);
            }
        }

        private void MarkWsData(Symbol symbol) => _lastWsDataUtc[symbol] = DateTime.UtcNow;

        // ------------------------------------------------------------------ REST fallback

        /// <summary>
        /// Slow REST poll of <c>LastQuote</c>. Acts only as a safety net: a symbol is polled
        /// when the WebSocket is closed or its last stream update is older than
        /// <see cref="FinamConstants.WebSocketStaleness"/>.
        /// </summary>
        private void StartRestFallbackLoop(CancellationToken ct)
        {
            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        foreach (var symbol in _subscribedSymbols.Keys)
                        {
                            if (!ShouldRestPoll(symbol)) continue;
                            await PollAndEmitQuoteAsync(symbol, ct).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        Log.Error($"FinamBrokerage REST fallback loop error: {ex.Message}");
                    }
                    try { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }, ct);
        }

        private bool ShouldRestPoll(Symbol symbol)
        {
            if (_webSocket == null || !_webSocket.IsOpen) return true;
            if (_lastWsDataUtc.TryGetValue(symbol, out var last) && DateTime.UtcNow - last < FinamConstants.WebSocketStaleness)
            {
                return false;
            }
            return true;
        }

        private async Task PollAndEmitQuoteAsync(Symbol symbol, CancellationToken ct)
        {
            try
            {
                var brokerageSymbol = _symbolMapper.GetBrokerageSymbol(symbol);
                var response = await _api.GetLatestQuoteAsync(brokerageSymbol, ct).ConfigureAwait(false);
                if (response?.Quote == null) return;

                var bid = response.Quote.Bid?.AsDecimal() ?? 0m;
                var ask = response.Quote.Ask?.AsDecimal() ?? 0m;
                var last = response.Quote.Last?.AsDecimal() ?? 0m;
                var time = response.Quote.Timestamp == default ? DateTime.UtcNow : response.Quote.Timestamp;

                if (bid > 0m && ask > 0m)
                {
                    _aggregator?.Update(new Tick(time, symbol, string.Empty, string.Empty,
                        response.Quote.BidSize?.AsDecimal() ?? 0m, bid,
                        response.Quote.AskSize?.AsDecimal() ?? 0m, ask));
                }
                if (last > 0m)
                {
                    _aggregator?.Update(new Tick(time, symbol, string.Empty, string.Empty,
                        response.Quote.LastSize?.AsDecimal() ?? 0m, last));
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Debug($"FinamBrokerage.PollAndEmitQuoteAsync({symbol}): {ex.Message}");
            }
        }
    }
}
