/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Licensed under the Apache License, Version 2.0.
*/

using NUnit.Framework;

namespace QuantConnect.Brokerages.Finam.Tests
{
    [TestFixture]
    public class FinamSymbolMapperTests
    {
        [Test]
        public void MapsRussianEquityToTickerAtMisx()
        {
            var mapper = new FinamSymbolMapper();
            var symbol = Symbol.Create("SBER", SecurityType.Equity, FinamConstants.Market);

            Assert.AreEqual("SBER@MISX", mapper.GetBrokerageSymbol(symbol));
        }

        [Test]
        public void MapsUsEquityToTickerAtXnas()
        {
            var mapper = new FinamSymbolMapper();
            var symbol = Symbol.Create("AAPL", SecurityType.Equity, Market.USA);

            Assert.AreEqual("AAPL@XNAS", mapper.GetBrokerageSymbol(symbol));
        }

        [Test]
        public void RoundTripsTickerAtMicSymbol()
        {
            var mapper = new FinamSymbolMapper();
            var lean = mapper.GetLeanSymbol("GAZP@MISX", SecurityType.Equity, FinamConstants.Market);
            var back = mapper.GetBrokerageSymbol(lean);

            Assert.AreEqual("GAZP", lean.Value);
            Assert.AreEqual("GAZP@MISX", back);
        }

        [Test]
        public void PreservesAlreadyQualifiedSymbol()
        {
            var mapper = new FinamSymbolMapper();
            var symbol = Symbol.Create("LKOH@MISX", SecurityType.Equity, FinamConstants.Market);

            Assert.AreEqual("LKOH@MISX", mapper.GetBrokerageSymbol(symbol));
        }
    }
}
