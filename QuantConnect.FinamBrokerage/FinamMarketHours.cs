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
using System.Linq;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.Finam
{
    /// <summary>
    /// Builds <see cref="SecurityExchangeHours"/> for the custom Finam markets.
    /// </summary>
    /// <remarks>
    /// Approximate MOEX equity sessions in Moscow time: main 10:00–18:50 and evening 19:05–23:50,
    /// Monday–Friday. The morning session (≈07:00–09:50), the closing-auction nuances, holidays and
    /// the separate FORTS (futures/options) schedule are TODOs — refine from the Finam
    /// <c>/v1/assets/{symbol}/schedule</c> endpoint.
    /// </remarks>
    public static class FinamMarketHours
    {
        public static SecurityExchangeHours MoexEquity()
        {
            var main = new MarketHoursSegment(MarketHoursState.Market, new TimeSpan(10, 0, 0), new TimeSpan(18, 50, 0));
            var evening = new MarketHoursSegment(MarketHoursState.Market, new TimeSpan(19, 5, 0), new TimeSpan(23, 50, 0));

            LocalMarketHours TradingDay(DayOfWeek day) => new(day, main, evening);
            LocalMarketHours Closed(DayOfWeek day) => new(day); // no segments => closed all day

            var byDay = new Dictionary<DayOfWeek, LocalMarketHours>
            {
                { DayOfWeek.Sunday, Closed(DayOfWeek.Sunday) },
                { DayOfWeek.Monday, TradingDay(DayOfWeek.Monday) },
                { DayOfWeek.Tuesday, TradingDay(DayOfWeek.Tuesday) },
                { DayOfWeek.Wednesday, TradingDay(DayOfWeek.Wednesday) },
                { DayOfWeek.Thursday, TradingDay(DayOfWeek.Thursday) },
                { DayOfWeek.Friday, TradingDay(DayOfWeek.Friday) },
                { DayOfWeek.Saturday, Closed(DayOfWeek.Saturday) },
            };

            return new SecurityExchangeHours(
                TimeZones.Moscow,
                Enumerable.Empty<DateTime>(),
                byDay,
                new Dictionary<DateTime, TimeSpan>(),
                new Dictionary<DateTime, TimeSpan>());
        }
    }
}
