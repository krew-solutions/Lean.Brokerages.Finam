/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Licensed under the Apache License, Version 2.0.
*/

using System;
using NUnit.Framework;

namespace QuantConnect.Brokerages.Finam.Tests
{
    /// <summary>
    /// Verifies <see cref="FinamBrokerage.IsNewerTrade(string, DateTime, string, DateTime)"/> — the dedup
    /// decision shared by the WS <c>INSTRUMENT_TRADES</c> path and the REST <c>LatestTrades</c> poll. The
    /// REST tape returns the last ~1000 trades each poll (heavily overlapping at ~140 trades/s), so dedup
    /// must key on the monotonic numeric trade id rather than the (too-coarse) millisecond timestamp.
    /// </summary>
    [TestFixture]
    public class FinamTradeDedupTests
    {
        private static readonly DateTime T0 = new(2026, 6, 3, 7, 23, 0, DateTimeKind.Utc);

        [Test]
        public void NumericTradeId_AdvancesMonotonically_RegardlessOfTimestamp()
        {
            // Newer id wins even when the timestamp is identical (same-millisecond high-frequency prints).
            Assert.That(FinamBrokerage.IsNewerTrade("16636024012", T0, "16636041164", T0), Is.True);
            // Same and lower ids are not re-emitted (overlapping REST batches).
            Assert.That(FinamBrokerage.IsNewerTrade("16636041164", T0, "16636041164", T0.AddSeconds(1)), Is.False);
            Assert.That(FinamBrokerage.IsNewerTrade("16636041164", T0, "16636024012", T0.AddSeconds(1)), Is.False);
        }

        [Test]
        public void NumericTradeId_HandlesLargeFortsIds()
        {
            // FORTS ids are large but fit in a long.
            Assert.That(FinamBrokerage.IsNewerTrade("1925039900101449780", T0, "1925039900101449979", T0), Is.True);
            Assert.That(FinamBrokerage.IsNewerTrade("1925039900101449979", T0, "1925039900101449780", T0), Is.False);
        }

        [Test]
        public void NonNumericTradeId_FallsBackToStrictlyNewerByTimeThenId()
        {
            // Same time, different id -> treated as newer (can't order by id, so don't drop a distinct print).
            Assert.That(FinamBrokerage.IsNewerTrade("A", T0, "B", T0), Is.True);
            // Exact same (time, id) -> duplicate, dropped.
            Assert.That(FinamBrokerage.IsNewerTrade("A", T0, "A", T0), Is.False);
            // Older timestamp -> dropped.
            Assert.That(FinamBrokerage.IsNewerTrade("A", T0, "B", T0.AddSeconds(-1)), Is.False);
            // Newer timestamp -> kept.
            Assert.That(FinamBrokerage.IsNewerTrade("A", T0, "B", T0.AddSeconds(1)), Is.True);
        }
    }
}
