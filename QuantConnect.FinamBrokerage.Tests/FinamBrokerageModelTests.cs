/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Licensed under the Apache License, Version 2.0.
*/

using System;
using NUnit.Framework;
using QuantConnect.Orders;
using QuantConnect.Securities;
using QuantConnect.Securities.Equity;
using QuantConnect.Data;

namespace QuantConnect.Brokerages.Finam.Tests
{
    [TestFixture]
    public class FinamBrokerageModelTests
    {
        private static Security CreateEquity()
        {
            var symbol = Symbol.Create("SBER", SecurityType.Equity, FinamConstants.Market);
            return new Equity(
                SecurityExchangeHours.AlwaysOpen(TimeZones.Moscow),
                new SubscriptionDataConfig(typeof(QuantConnect.Data.Market.TradeBar), symbol, Resolution.Minute, TimeZones.Moscow, TimeZones.Moscow, false, false, false),
                new Cash("RUB", 0, 1m),
                SymbolProperties.GetDefault("RUB"),
                ErrorCurrencyConverter.Instance,
                RegisteredSecurityDataTypesProvider.Null,
                new SecurityCache());
        }

        [Test]
        public void AcceptsLimitEquityOrder()
        {
            var model = new FinamBrokerageModel();
            var order = new LimitOrder(Symbol.Create("SBER", SecurityType.Equity, FinamConstants.Market), 10, 200m, DateTime.UtcNow);

            Assert.IsTrue(model.CanSubmitOrder(CreateEquity(), order, out var _));
        }

        [Test]
        public void RejectsTrailingStopOrder()
        {
            var model = new FinamBrokerageModel();
            var order = new TrailingStopOrder(
                Symbol.Create("SBER", SecurityType.Equity, FinamConstants.Market), 10, 200m, 1m, true, DateTime.UtcNow);

            Assert.IsFalse(model.CanSubmitOrder(CreateEquity(), order, out var msg));
            Assert.IsNotNull(msg);
        }

        [Test]
        public void RejectsAllOrderUpdates()
        {
            var model = new FinamBrokerageModel();
            var order = new LimitOrder(Symbol.Create("SBER", SecurityType.Equity, FinamConstants.Market), 10, 200m, DateTime.UtcNow);
            var update = new UpdateOrderRequest(DateTime.UtcNow, order.Id, new UpdateOrderFields { LimitPrice = 201m });

            Assert.IsFalse(model.CanUpdateOrder(CreateEquity(), order, update, out var msg));
            Assert.IsNotNull(msg);
        }
    }
}
