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
    /// LEAN expects an enumerator-per-subscription producer. We register each subscription with
    /// a shared <see cref="IDataAggregator"/> and drive ticks into it from the streaming
    /// subscriptions opened by <see cref="StartStreaming"/>.
    /// As of v0.1 the streaming hook is a no-op placeholder — the integration relies on the
    /// REST <c>LastQuote</c> endpoint for synchronous quote refreshes and historical bars
    /// for backfills. The Finam gRPC <c>SubscribeQuote</c> / <c>SubscribeLatestTrades</c>
    /// streams should be wired here in a future revision.
    /// </remarks>
    public partial class FinamBrokerage
    {
        private IDataAggregator _aggregator;
        private readonly ConcurrentDictionary<Symbol, byte> _subscribedSymbols = new();

        /// <inheritdoc />
        public IEnumerator<BaseData> Subscribe(SubscriptionDataConfig dataConfig, EventHandler newDataAvailableHandler)
        {
            if (!CanSubscribe(dataConfig.Symbol))
            {
                return null;
            }

            _subscribedSymbols.TryAdd(dataConfig.Symbol, 0);
            var enumerator = _aggregator.Add(dataConfig, newDataAvailableHandler);
            Log.Trace($"FinamBrokerage.Subscribe: {dataConfig.Symbol.Value} ({dataConfig.Resolution})");
            return enumerator;
        }

        /// <inheritdoc />
        public void Unsubscribe(SubscriptionDataConfig dataConfig)
        {
            _subscribedSymbols.TryRemove(dataConfig.Symbol, out _);
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

        /// <summary>
        /// Entry point for the brokerage's background streaming work.
        /// Currently kicks off a slow REST poll for quotes. Replace with the gRPC
        /// <c>SubscribeQuote</c> stream once the gRPC client is wired in.
        /// </summary>
        private void StartStreaming(CancellationToken ct)
        {
            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        foreach (var symbol in _subscribedSymbols.Keys)
                        {
                            await PollAndEmitQuoteAsync(symbol, ct).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        Log.Error($"FinamBrokerage streaming loop error: {ex.Message}");
                    }
                    await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                }
            }, ct);
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
