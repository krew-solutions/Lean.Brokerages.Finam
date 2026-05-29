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
    /// MOEX equity sessions in Moscow time, Monday–Friday, taken from the Finam
    /// <c>/v1/assets/{symbol}/schedule</c> endpoint (EARLY/CORE/LATE_TRADING with auctions between):
    /// morning 07:00–09:50, main 10:00–18:55, evening 19:00–23:50. All three are continuous trading
    /// sessions in T+ mode — ordinary market orders execute throughout — so each is modelled as a
    /// regular <see cref="MarketHoursState.Market"/> segment. (They are NOT US-style pre/post-market:
    /// classifying them as such would make LEAN convert market orders into MarketOnOpen and the
    /// Finam model would reject them.) Strategies that want the main session only should gate on the
    /// bar time themselves rather than rely on extended-hours subscription semantics. The brief
    /// opening/closing auction windows (09:50–10:00, 18:55–19:00) are left as gaps; no quarter-hour
    /// bar starts inside them. Holidays and the separate FORTS (futures/options) schedule are TODOs.
    /// </remarks>
    public static class FinamMarketHours
    {
        public static SecurityExchangeHours MoexEquity()
        {
            var morning = new MarketHoursSegment(MarketHoursState.Market, new TimeSpan(7, 0, 0), new TimeSpan(9, 50, 0));
            var main = new MarketHoursSegment(MarketHoursState.Market, new TimeSpan(10, 0, 0), new TimeSpan(18, 55, 0));
            var evening = new MarketHoursSegment(MarketHoursState.Market, new TimeSpan(19, 0, 0), new TimeSpan(23, 50, 0));

            LocalMarketHours TradingDay(DayOfWeek day) => new(day, morning, main, evening);
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
