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
using System.Linq;
using System.Threading;
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
    /// <see cref="LevelOneServiceManager"/>, feeding it quotes and trades from the Finam WebSocket:
    /// <list type="bullet">
    ///   <item><c>QUOTES</c> → <see cref="LevelOneServiceManager.HandleQuote"/>;</item>
    ///   <item><c>INSTRUMENT_TRADES</c> → <see cref="LevelOneServiceManager.HandleLastTrade"/>.</item>
    /// </list>
    /// On (re)subscribe Finam replays a snapshot of recent trades, so the trade tape is de-duplicated
    /// by a 5-minute frontier plus the last seen trade id per symbol (same approach as Coinbase).
    /// Historical bars are served separately by <c>GetHistory</c> over REST.
    /// </remarks>
    public partial class FinamBrokerage
    {
        private IDataAggregator _aggregator;
        private LevelOneServiceManager _levelOneServiceManager;
        private FinamWebSocketClient _webSocket;

        private static readonly TimeSpan TradeResendFrontier = TimeSpan.FromMinutes(5);
        private readonly ConcurrentDictionary<string, Symbol> _brokerageToLean = new();
        private readonly ConcurrentDictionary<Symbol, (string TradeId, DateTime TimeUtc)> _lastTradeBySymbol = new();

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
                _ = _webSocket.SubscribeQuotesAsync(brokerageSymbol);
                _ = _webSocket.SubscribeInstrumentTradesAsync(brokerageSymbol);
            }
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

            foreach (var quote in payload.Quote)
            {
                if (!_brokerageToLean.TryGetValue(quote.Symbol ?? string.Empty, out var symbol)) continue;

                _levelOneServiceManager.HandleQuote(
                    symbol,
                    ToUtc(quote.Timestamp),
                    quote.Bid?.AsDecimal(),
                    quote.BidSize?.AsDecimal(),
                    quote.Ask?.AsDecimal(),
                    quote.AskSize?.AsDecimal());
            }
        }

        private void OnInstrumentTrades(WsTradesPayload payload)
        {
            if (payload?.Trades == null || !_brokerageToLean.TryGetValue(payload.Symbol ?? string.Empty, out var symbol))
            {
                return;
            }

            var frontier = DateTime.UtcNow - TradeResendFrontier;
            foreach (var trade in payload.Trades.OrderBy(t => t.Timestamp))
            {
                var price = trade.Price?.AsDecimal() ?? 0m;
                if (price <= 0m) continue;

                var time = ToUtc(trade.Timestamp);

                // Drop the recent-trades snapshot Finam replays on (re)subscribe: anything older than the
                // frontier, or not strictly newer than the last print we already forwarded for this symbol.
                if (time < frontier) continue;
                if (_lastTradeBySymbol.TryGetValue(symbol, out var last) &&
                    (time < last.TimeUtc || (time == last.TimeUtc && trade.TradeId == last.TradeId)))
                {
                    continue;
                }

                _levelOneServiceManager.HandleLastTrade(symbol, time, trade.Size?.AsDecimal() ?? 0m, price);
                _lastTradeBySymbol[symbol] = (trade.TradeId, time);
            }
        }

        private static DateTime ToUtc(DateTime value)
            => value == default ? DateTime.UtcNow : value.ToUniversalTime();
    }
}
