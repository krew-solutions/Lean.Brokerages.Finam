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
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuantConnect.Brokerages.Finam.Api;
using QuantConnect.Brokerages.LevelOneOrderBook;
using QuantConnect.Data;
using QuantConnect.Interfaces;
using QuantConnect.Logging;
using QuantConnect.Packets;

namespace QuantConnect.Brokerages.Finam
{
    /// <summary>
    /// <see cref="IDataQueueHandler"/> half of the Finam brokerage.
    /// </summary>
    /// <remarks>
    /// Follows the standard LEAN live-data pattern (as in the Coinbase / Tastytrade / ThetaData
    /// providers): the brokerage emits <em>ticks</em> and the engine's <c>IDataAggregator</c>
    /// consolidates them into the requested bar resolution. We use LEAN's
    /// <see cref="LevelOneServiceManager"/>, feeding it quotes (<c>QUOTES</c> →
    /// <see cref="LevelOneServiceManager.HandleQuote"/>) and trades (<c>INSTRUMENT_TRADES</c> →
    /// <see cref="LevelOneServiceManager.HandleLastTrade"/>).
    /// <para>
    /// The Finam WS market-data feed has been observed to be <em>snapshot-only</em>: during live MOEX
    /// trading it replays only the previous session's last quote/trade (resent as a heartbeat) and never
    /// streams fresh prints, while REST returns a fully live tape with the same token. So live data is
    /// routed by <c>finam-marketdata-source</c>: <c>auto</c> (default) keeps WS primary but falls back to
    /// REST polling (<c>LatestTrades</c>/<c>LatestQuote</c>) per symbol whenever the WS feed delivers no
    /// fresh data for <c>finam-marketdata-staleness-seconds</c>; <c>rest</c> always polls; <c>ws</c>
    /// disables the fallback. Both paths share the same dedup + emit helpers
    /// (<see cref="EmitTrades"/>/<see cref="EmitQuote"/>), so the aggressor SaleCondition and the 5-minute
    /// resend frontier behave identically regardless of source.
    /// </para>
    /// On (re)subscribe Finam replays a snapshot of recent trades, so the trade tape is de-duplicated by a
    /// 5-minute frontier plus a monotonic trade id per symbol. Historical bars are served separately by
    /// <c>GetHistory</c> over REST.
    /// </remarks>
    public partial class FinamBrokerage
    {
        private IDataAggregator _aggregator;
        private LevelOneServiceManager _levelOneServiceManager;
        private FinamWebSocketClient _webSocket;

        private static readonly TimeSpan TradeResendFrontier = TimeSpan.FromMinutes(5);
        private readonly ConcurrentDictionary<string, Symbol> _brokerageToLean = new();
        private readonly ConcurrentDictionary<Symbol, (string TradeId, DateTime TimeUtc)> _lastTradeBySymbol = new();

        // Live market-data routing (assigned in the constructor). See class remarks.
        private readonly string _marketDataSource;
        private readonly TimeSpan _marketDataPollInterval;
        private readonly TimeSpan _marketDataStaleness;

        // Per-symbol freshness/dedup state. _lastFresh*Utc is bumped ONLY by the WS path (frame-level
        // freshness), so it reflects WS health and is not reset by our own REST polling.
        private readonly ConcurrentDictionary<Symbol, DateTime> _subscribedAtUtc = new();
        private readonly ConcurrentDictionary<Symbol, DateTime> _lastFreshTradeUtc = new();
        private readonly ConcurrentDictionary<Symbol, DateTime> _lastFreshQuoteUtc = new();
        private readonly ConcurrentDictionary<Symbol, DateTime> _lastQuoteEmitUtc = new();
        private readonly ConcurrentDictionary<Symbol, object> _emitLock = new();
        private readonly ConcurrentDictionary<Symbol, byte> _restTradesLogged = new();
        private readonly ConcurrentDictionary<Symbol, byte> _restQuotesLogged = new();

        /// <summary>Maps the <c>finam-marketdata-source</c> config value to a normalized token.</summary>
        private static string NormalizeMarketDataSource(string value)
        {
            switch (value?.Trim().ToLowerInvariant())
            {
                case FinamConstants.MarketDataSourceRest: return FinamConstants.MarketDataSourceRest;
                case FinamConstants.MarketDataSourceWs: return FinamConstants.MarketDataSourceWs;
                default: return FinamConstants.MarketDataSourceAuto;
            }
        }

        /// <inheritdoc />
        public IEnumerator<BaseData> Subscribe(SubscriptionDataConfig dataConfig, EventHandler newDataAvailableHandler)
        {
            if (!CanSubscribe(dataConfig.Symbol))
            {
                return null;
            }

            var enumerator = _aggregator.Add(dataConfig, newDataAvailableHandler);
            _levelOneServiceManager.Subscribe(dataConfig);
            return enumerator;
        }

        /// <inheritdoc />
        public void Unsubscribe(SubscriptionDataConfig dataConfig)
        {
            _levelOneServiceManager.Unsubscribe(dataConfig);
            _aggregator.Remove(dataConfig);
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

        /// <summary>
        /// <see cref="LevelOneServiceManager"/> subscribe callback: maps the LEAN symbols to Finam
        /// channels (<c>QUOTES</c> for quote/open-interest tick types, <c>INSTRUMENT_TRADES</c> for trades).
        /// </summary>
        private bool SubscribeMarketData(IEnumerable<Symbol> symbols, TickType tickType)
        {
            foreach (var symbol in symbols)
            {
                var brokerageSymbol = _symbolMapper.GetBrokerageSymbol(symbol);
                _brokerageToLean[brokerageSymbol] = symbol;
                _subscribedAtUtc.TryAdd(symbol, DateTime.UtcNow);

                // Before the socket is up the WS client is null; StartStreaming replays everything on connect.
                if (_webSocket == null) continue;

                if (tickType == TickType.Trade)
                {
                    _ = _webSocket.SubscribeInstrumentTradesAsync(brokerageSymbol);
                }
                else
                {
                    _ = _webSocket.SubscribeQuotesAsync(brokerageSymbol);
                }
            }
            return true;
        }

        private bool UnsubscribeMarketData(IEnumerable<Symbol> symbols, TickType tickType)
        {
            if (_webSocket == null) return true;
            foreach (var symbol in symbols)
            {
                var brokerageSymbol = _symbolMapper.GetBrokerageSymbol(symbol);
                if (tickType == TickType.Trade)
                {
                    _ = _webSocket.UnsubscribeInstrumentTradesAsync(brokerageSymbol);
                }
                else
                {
                    _ = _webSocket.UnsubscribeQuotesAsync(brokerageSymbol);
                }
            }
            return true;
        }

        /// <summary>
        /// Brings up the WebSocket stream (market quotes/trades + account order/trade push).
        /// Invoked by <see cref="Connect"/> once the REST session is authenticated.
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

            // Replay market-data subscriptions registered before the socket was up.
            foreach (var symbol in _levelOneServiceManager.GetSubscribedSymbols())
            {
                var brokerageSymbol = _symbolMapper.GetBrokerageSymbol(symbol);
                _brokerageToLean[brokerageSymbol] = symbol;
                _subscribedAtUtc.TryAdd(symbol, DateTime.UtcNow);
                _ = _webSocket.SubscribeQuotesAsync(brokerageSymbol);
                _ = _webSocket.SubscribeInstrumentTradesAsync(brokerageSymbol);
            }

            // The WS market-data feed is snapshot-only (see class remarks); bring up the REST fallback poll.
            StartMarketDataRestLoop(ct);
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
                        OnQuotes(envelope.PayloadAs<WsQuotePayload>());
                        break;
                    case WsSubscriptionType.InstrumentTrades:
                        OnInstrumentTrades(envelope.PayloadAs<WsTradesPayload>());
                        break;
                    case WsSubscriptionType.Trades:
                        OnAccountTrades(envelope.PayloadAs<WsAccountTradesPayload>());
                        break;
                    case WsSubscriptionType.Orders:
                        OnOrderStates(envelope.PayloadAs<WsOrdersPayload>());
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Debug($"FinamBrokerage.OnWebSocketEnvelope: {ex.Message}");
            }
        }

        private void OnQuotes(WsQuotePayload payload)
        {
            if (payload?.Quote == null) return;

            var frontier = DateTime.UtcNow - TradeResendFrontier;
            foreach (var quote in payload.Quote)
            {
                if (!_brokerageToLean.TryGetValue(quote.Symbol ?? string.Empty, out var symbol)) continue;

                // Mark WS liveness only for a genuinely recent quote — the snapshot-only feed replays a
                // stale last-session quote that must not reset the freshness clock (else REST never polls).
                if (ToUtc(quote.Timestamp) >= frontier) _lastFreshQuoteUtc[symbol] = DateTime.UtcNow;

                EmitQuote(symbol, quote.Timestamp,
                    quote.Bid?.AsDecimal(), quote.BidSize?.AsDecimal(),
                    quote.Ask?.AsDecimal(), quote.AskSize?.AsDecimal());
            }
        }

        /// <summary>
        /// Dedups and forwards one quote for a symbol. Shared by the WS <c>QUOTES</c> path and the REST
        /// <c>LatestQuote</c> poll: drops the stale last-session snapshot (older than the resend frontier)
        /// and never moves a symbol's quote backwards in time.
        /// </summary>
        private void EmitQuote(Symbol symbol, DateTime timestamp, decimal? bid, decimal? bidSize, decimal? ask, decimal? askSize)
        {
            var time = ToUtc(timestamp);
            if (time < DateTime.UtcNow - TradeResendFrontier) return;

            lock (_emitLock.GetOrAdd(symbol, _ => new object()))
            {
                if (_lastQuoteEmitUtc.TryGetValue(symbol, out var last) && time <= last) return;
                _levelOneServiceManager.HandleQuote(symbol, time, bid, bidSize, ask, askSize);
                _lastQuoteEmitUtc[symbol] = time;
            }
        }

        private void OnInstrumentTrades(WsTradesPayload payload)
        {
            if (payload?.Trades == null || !_brokerageToLean.TryGetValue(payload.Symbol ?? string.Empty, out var symbol))
            {
                return;
            }

            // Mark WS liveness if this frame carries any genuinely recent trade (proves the tape is
            // streaming for this symbol). A stale snapshot resend must not reset the freshness clock.
            var frontier = DateTime.UtcNow - TradeResendFrontier;
            if (payload.Trades.Any(t => ToUtc(t.Timestamp) >= frontier))
            {
                _lastFreshTradeUtc[symbol] = DateTime.UtcNow;
            }

            EmitTrades(symbol, payload);
        }

        /// <summary>
        /// Dedups and forwards a batch of public trades for one symbol. Shared by the WS
        /// <c>INSTRUMENT_TRADES</c> path and the REST <c>LatestTrades</c> poll, so dedup state, the 5-minute
        /// resend frontier, and the aggressor SaleCondition behave identically on both paths.
        /// </summary>
        /// <remarks>
        /// Dedup is by monotonically increasing <c>tradeId</c> when ids are numeric (the Finam tape advances
        /// ~140 trades/s, so a millisecond timestamp is too coarse), falling back to a strictly-newer
        /// (timestamp, id) check otherwise. Held under a per-symbol lock because the WS callback thread and
        /// the REST poll task can both call this for the same symbol.
        /// </remarks>
        private void EmitTrades(Symbol symbol, WsTradesPayload payload)
        {
            if (payload?.Trades == null) return;

            var frontier = DateTime.UtcNow - TradeResendFrontier;
            lock (_emitLock.GetOrAdd(symbol, _ => new object()))
            {
                foreach (var trade in payload.Trades
                    .OrderBy(t => t.Timestamp)
                    .ThenBy(t => ParseTradeId(t.TradeId) ?? long.MinValue))
                {
                    var price = trade.Price?.AsDecimal() ?? 0m;
                    if (price <= 0m) continue;

                    var time = ToUtc(trade.Timestamp);
                    if (time < frontier) continue;                 // drop the stale last-session snapshot
                    if (!IsNewerTrade(symbol, trade.TradeId, time)) continue;

                    // Preserve the aggressor side (Finam Trade.side) as the trade tick's SaleCondition
                    // ("B" = buyer-initiated, "S" = seller-initiated, "" = unknown). Dropping it (as a bare
                    // HandleLastTrade call does) makes a real Cumulative Volume Delta impossible. Lean
                    // serializes SaleCondition in equity trade-tick files too, so backtests see the same
                    // flag. Consumed by QuantConnect.TradingLean.Indicators.CumulativeVolumeDelta.
                    _levelOneServiceManager.HandleLastTrade(
                        symbol, time, trade.Size?.AsDecimal() ?? 0m, price, ToAggressorCode(trade.Side));
                    _lastTradeBySymbol[symbol] = (trade.TradeId, time);
                }
            }
        }

        /// <summary>Whether a trade is strictly newer than the last one forwarded for the symbol.</summary>
        private bool IsNewerTrade(Symbol symbol, string tradeId, DateTime timeUtc)
            => !_lastTradeBySymbol.TryGetValue(symbol, out var last)
               || IsNewerTrade(last.TradeId, last.TimeUtc, tradeId, timeUtc);

        /// <summary>
        /// Pure dedup decision shared by the WS and REST paths: a trade is newer when its numeric
        /// <c>tradeId</c> exceeds the last one (the Finam tape advances ~140 trades/s, so a millisecond
        /// timestamp is too coarse), falling back to a strictly-newer (timestamp, id) check for
        /// non-numeric ids.
        /// </summary>
        internal static bool IsNewerTrade(string lastTradeId, DateTime lastTimeUtc, string tradeId, DateTime timeUtc)
        {
            var cur = ParseTradeId(tradeId);
            var prev = ParseTradeId(lastTradeId);
            if (cur.HasValue && prev.HasValue) return cur.Value > prev.Value;

            return timeUtc > lastTimeUtc || (timeUtc == lastTimeUtc && tradeId != lastTradeId);
        }

        private static long? ParseTradeId(string id)
            => long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : (long?)null;

        /// <summary>
        /// Background REST fallback poll. For each subscribed symbol it polls <c>LatestTrades</c> and/or
        /// <c>LatestQuote</c> whenever that channel should be served by REST (see <see cref="ShouldPoll"/>):
        /// always in <c>rest</c> mode, never in <c>ws</c> mode, and in <c>auto</c> mode while the WS feed has
        /// delivered nothing fresh for <see cref="_marketDataStaleness"/>. Cancelled with the connection.
        /// </summary>
        private void StartMarketDataRestLoop(CancellationToken ct)
        {
            if (_marketDataSource == FinamConstants.MarketDataSourceWs) return;   // REST fallback disabled

            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        foreach (var symbol in _levelOneServiceManager.GetSubscribedSymbols())
                        {
                            if (ShouldPoll(symbol, _lastFreshTradeUtc))
                            {
                                if (_restTradesLogged.TryAdd(symbol, 0))
                                    Log.Trace($"FinamBrokerage: {symbol} trades -> REST polling (WS feed not fresh, source={_marketDataSource}).");
                                await PollAndEmitTradesAsync(symbol, ct).ConfigureAwait(false);
                            }
                            if (ShouldPoll(symbol, _lastFreshQuoteUtc))
                            {
                                if (_restQuotesLogged.TryAdd(symbol, 0))
                                    Log.Trace($"FinamBrokerage: {symbol} quotes -> REST polling (WS feed not fresh, source={_marketDataSource}).");
                                await PollAndEmitQuoteAsync(symbol, ct).ConfigureAwait(false);
                            }
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { Log.Error($"FinamBrokerage market-data REST loop error: {ex.Message}"); }

                    try { await Task.Delay(_marketDataPollInterval, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }, ct);
        }

        /// <summary>
        /// Whether the symbol's channel (identified by its <paramref name="lastFresh"/> clock) should be
        /// served by a REST poll right now.
        /// </summary>
        private bool ShouldPoll(Symbol symbol, ConcurrentDictionary<Symbol, DateTime> lastFresh)
        {
            if (_marketDataSource == FinamConstants.MarketDataSourceWs) return false;
            if (_marketDataSource == FinamConstants.MarketDataSourceRest) return true;

            // auto: poll while WS has been silent (no fresh data) for the staleness window. Baseline off the
            // subscribe time so a never-streaming symbol starts polling after one staleness window.
            var since = lastFresh.TryGetValue(symbol, out var lf)
                ? lf
                : _subscribedAtUtc.TryGetValue(symbol, out var sub) ? sub : DateTime.UtcNow;
            return DateTime.UtcNow - since >= _marketDataStaleness;
        }

        private async Task PollAndEmitTradesAsync(Symbol symbol, CancellationToken ct)
        {
            try
            {
                var brokerageSymbol = _symbolMapper.GetBrokerageSymbol(symbol);
                var payload = await _api.GetLatestTradesAsync(brokerageSymbol, ct).ConfigureAwait(false);
                if (payload?.Trades == null) return;
                EmitTrades(symbol, payload);   // shared dedup/emit; never bumps the WS freshness clock
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Debug($"FinamBrokerage.PollAndEmitTradesAsync({symbol}): {ex.Message}"); }
        }

        private async Task PollAndEmitQuoteAsync(Symbol symbol, CancellationToken ct)
        {
            try
            {
                var brokerageSymbol = _symbolMapper.GetBrokerageSymbol(symbol);
                var response = await _api.GetLatestQuoteAsync(brokerageSymbol, ct).ConfigureAwait(false);
                var q = response?.Quote;
                if (q == null) return;
                EmitQuote(symbol, q.Timestamp, q.Bid?.AsDecimal(), q.BidSize?.AsDecimal(),
                    q.Ask?.AsDecimal(), q.AskSize?.AsDecimal());
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Debug($"FinamBrokerage.PollAndEmitQuoteAsync({symbol}): {ex.Message}"); }
        }

        private static DateTime ToUtc(DateTime value)
            => value == default ? DateTime.UtcNow : value.ToUniversalTime();

        /// <summary>
        /// Decides whether an <c>INSTRUMENT_TRADES</c>/<c>LatestTrades</c> payload carries at least one
        /// <em>real</em> public trade, versus the "stub" frame the WS channel is suspected of sending for
        /// MOEX spot (<c>MISX</c>) instead of real prints. Shared by the WS path and the diagnostic test
        /// so detection has a single source of truth.
        /// </summary>
        /// <remarks>
        /// FINALIZE AFTER DIAGNOSTIC: the exact stub shape is revealed by
        /// <c>FinamBrokerageSmokeTests.ContrastsSpotVsFuturesInstrumentTrades</c> (it dumps the raw stub
        /// JSON). Replace the body with whichever single candidate cleanly separates stub from real:
        /// <list type="bullet">
        ///   <item>(a) empty trades:            <c>Trades == null || Trades.Count == 0</c>;</item>
        ///   <item>(b) non-positive price/size: no trade has <c>price &gt; 0 &amp;&amp; size &gt; 0</c>;</item>
        ///   <item>(c) sentinel marker:         every trade's <c>TradeId</c>/<c>Mpid</c> is a known stub value.</item>
        /// </list>
        /// Current default = (a) OR (b): "real" iff ≥1 trade has <c>price &gt; 0 &amp;&amp; size &gt; 0</c> —
        /// the safest pre-diagnostic guess, consistent with the existing <c>price &lt;= 0</c> skip below.
        /// </remarks>
        internal static bool IsRealTradePayload(WsTradesPayload payload)
        {
            if (payload?.Trades == null || payload.Trades.Count == 0)
            {
                return false;
            }

            foreach (var trade in payload.Trades)
            {
                var price = trade.Price?.AsDecimal() ?? 0m;
                var size = trade.Size?.AsDecimal() ?? 0m;
                if (price > 0m && size > 0m)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Maps Finam's trade aggressor side (<c>Trade.side</c>) to a one-character SaleCondition flag
        /// carried on the Lean trade tick: "B" = buyer-initiated, "S" = seller-initiated, "" = unknown.
        /// Accepts the proto enum names (SIDE_BUY/SIDE_SELL), REST spellings (buy/sell) and numeric codes.
        /// </summary>
        internal static string ToAggressorCode(string side)
        {
            if (string.IsNullOrEmpty(side))
            {
                return string.Empty;
            }

            switch (side.Trim().ToUpperInvariant())
            {
                case "1":
                case "BUY":
                case "SIDE_BUY":
                    return "B";
                case "2":
                case "SELL":
                case "SIDE_SELL":
                    return "S";
                default:
                    return string.Empty;
            }
        }
    }
}
