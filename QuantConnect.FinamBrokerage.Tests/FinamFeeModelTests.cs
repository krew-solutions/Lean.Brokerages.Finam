/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Licensed under the Apache License, Version 2.0.
*/

using System;
using NUnit.Framework;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;
using QuantConnect.Securities.Equity;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace QuantConnect.Brokerages.Finam.Tests
{
    [TestFixture]
    public class FinamFeeModelTests
    {
        private static Equity CreateEquity(string ticker, string market)
        {
            var symbol = Symbol.Create(ticker, SecurityType.Equity, market);
            var equity = new Equity(
                SecurityExchangeHours.AlwaysOpen(TimeZones.Moscow),
                new SubscriptionDataConfig(typeof(TradeBar), symbol, Resolution.Minute, TimeZones.Moscow, TimeZones.Moscow, false, false, false),
                new Cash("RUB", 0, 1m),
                SymbolProperties.GetDefault("RUB"),
                ErrorCurrencyConverter.Instance,
                RegisteredSecurityDataTypesProvider.Null);
            equity.SetMarketPrice(new Tick(DateTime.UtcNow, symbol, 250m, 250m));
            return equity;
        }

        [Test]
        public void ChargesMinFeeOnSmallMoexOrder()
        {
            var model = new FinamFeeModel();
            var equity = CreateEquity("SBER", FinamConstants.Market);
            var order = new MarketOrder(equity.Symbol, 1, DateTime.UtcNow);

            var fee = model.GetOrderFee(new OrderFeeParameters(equity, order));

            Assert.AreEqual("RUB", fee.Value.Currency);
            Assert.GreaterOrEqual(fee.Value.Amount, 35m);
        }

        [Test]
        public void UsesPercentFeeForLargeMoexOrder()
        {
            var model = new FinamFeeModel();
            var equity = CreateEquity("SBER", FinamConstants.Market);
            var order = new MarketOrder(equity.Symbol, 10000, DateTime.UtcNow);

            var fee = model.GetOrderFee(new OrderFeeParameters(equity, order));

            // 10_000 * 250 * 0.000354 ≈ 885
            Assert.AreEqual(885d, (double)fee.Value.Amount, 1d);
        }
    }
}
