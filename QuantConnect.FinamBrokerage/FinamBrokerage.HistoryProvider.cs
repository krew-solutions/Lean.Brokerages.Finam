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
using System.Collections.Generic;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Logging;

namespace QuantConnect.Brokerages.Finam
{
    /// <summary>
    /// History-provider half of the brokerage. Translates LEAN <see cref="HistoryRequest"/>
    /// into Finam <c>/v1/instruments/{symbol}/bars</c> calls and yields LEAN bars.
    /// </summary>
    public partial class FinamBrokerage
    {
        public override IEnumerable<BaseData> GetHistory(HistoryRequest request)
        {
            if (request == null) yield break;
            if (request.Resolution == Resolution.Tick || request.Resolution == Resolution.Second)
            {
                yield break;
            }

            var timeframe = MapResolutionToTimeframe(request.Resolution);
            if (timeframe == null)
            {
                Log.Trace($"FinamBrokerage.GetHistory: resolution {request.Resolution} is not supported");
                yield break;
            }

            var brokerageSymbol = _symbolMapper.GetBrokerageSymbol(request.Symbol);
            BarsResponseEnumerableState state;
            try
            {
                var response = _api.GetBarsAsync(brokerageSymbol, timeframe, request.StartTimeUtc, request.EndTimeUtc).GetAwaiter().GetResult();
                state = new BarsResponseEnumerableState(response);
            }
            catch (Exception ex)
            {
                Log.Error($"FinamBrokerage.GetHistory: {ex.Message}");
                yield break;
            }

            if (state.Bars == null) yield break;

            var period = request.Resolution.ToTimeSpan();
            var exchangeTimeZone = request.ExchangeHours.TimeZone;
            foreach (var bar in state.Bars)
            {
                // Finam bar timestamps are UTC; LEAN bar Time must be in the symbol's exchange time zone.
                var barTime = bar.Timestamp.ToUniversalTime().ConvertFromUtc(exchangeTimeZone);
                yield return new TradeBar(
                    barTime,
                    request.Symbol,
                    bar.Open?.AsDecimal() ?? 0m,
                    bar.High?.AsDecimal() ?? 0m,
                    bar.Low?.AsDecimal() ?? 0m,
                    bar.Close?.AsDecimal() ?? 0m,
                    bar.Volume?.AsDecimal() ?? 0m,
                    period);
            }
        }

        private static string MapResolutionToTimeframe(Resolution resolution) => resolution switch
        {
            Resolution.Minute => "TIME_FRAME_M1",
            Resolution.Hour => "TIME_FRAME_H1",
            Resolution.Daily => "TIME_FRAME_D",
            _ => null
        };

        private readonly struct BarsResponseEnumerableState
        {
            public IReadOnlyList<Api.FinamBar> Bars { get; }
            public BarsResponseEnumerableState(Api.BarsResponse response)
            {
                Bars = response?.Bars;
            }
        }
    }
}
