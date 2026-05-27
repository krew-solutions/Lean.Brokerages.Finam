/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Licensed under the Apache License, Version 2.0.
*/

using System;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace QuantConnect.Brokerages.Finam.Tests
{
    [TestFixture]
    public class FinamMarketHoursTests
    {
        // A weekday (2026-05-27 is a Wednesday) and the following Saturday, all in Moscow local time.
        private static DateTime Weekday(int h, int m) => new(2026, 5, 27, h, m, 0);
        private static DateTime Saturday(int h, int m) => new(2026, 5, 30, h, m, 0);

        [TestCase(12, 0, true,  TestName = "main session midday open")]
        [TestCase(21, 0, true,  TestName = "evening session open")]
        [TestCase(18, 55, false, TestName = "between main and evening closed")]
        [TestCase(3, 0, false,  TestName = "overnight closed")]
        [TestCase(9, 30, false, TestName = "before open closed")]
        public void MoexEquityWeekdaySessions(int hour, int minute, bool expectedOpen)
        {
            var hours = FinamMarketHours.MoexEquity();
            Assert.AreEqual(expectedOpen, hours.IsOpen(Weekday(hour, minute), extendedMarketHours: false));
        }

        [Test]
        public void MoexEquityClosedOnWeekend()
        {
            var hours = FinamMarketHours.MoexEquity();
            Assert.IsFalse(hours.IsOpen(Saturday(12, 0), extendedMarketHours: false));
        }

        [Test]
        public void MoexEquityUsesMoscowTimeZone()
        {
            Assert.AreEqual(TimeZones.Moscow, FinamMarketHours.MoexEquity().TimeZone);
        }
    }
}
