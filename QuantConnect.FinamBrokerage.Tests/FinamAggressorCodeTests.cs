/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Licensed under the Apache License, Version 2.0.
*/

using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace QuantConnect.Brokerages.Finam.Tests
{
    /// <summary>
    /// Verifies <see cref="FinamBrokerage.ToAggressorCode"/> maps Finam's trade aggressor side
    /// (<c>Trade.side</c>) to the one-character tick SaleCondition flag used to compute a real
    /// Cumulative Volume Delta: "B" = buyer-initiated, "S" = seller-initiated, "" = unknown.
    /// </summary>
    [TestFixture]
    public class FinamAggressorCodeTests
    {
        [TestCase("SIDE_BUY")]
        [TestCase("buy")]
        [TestCase("BUY")]
        [TestCase("1")]
        [TestCase(" side_buy ")]
        public void MapsBuySideToB(string side)
        {
            Assert.AreEqual("B", FinamBrokerage.ToAggressorCode(side));
        }

        [TestCase("SIDE_SELL")]
        [TestCase("sell")]
        [TestCase("SELL")]
        [TestCase("2")]
        [TestCase(" side_sell ")]
        public void MapsSellSideToS(string side)
        {
            Assert.AreEqual("S", FinamBrokerage.ToAggressorCode(side));
        }

        [TestCase("")]
        [TestCase(null)]
        [TestCase("SIDE_UNSPECIFIED")]
        [TestCase("0")]
        [TestCase("garbage")]
        public void MapsUnknownSideToEmpty(string side)
        {
            Assert.AreEqual(string.Empty, FinamBrokerage.ToAggressorCode(side));
        }
    }
}
