/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Licensed under the Apache License, Version 2.0.
*/

using System;
using NUnit.Framework;
using QuantConnect.Orders;

namespace QuantConnect.Brokerages.Finam.Tests
{
    [TestFixture]
    public class FinamOrderMappingTests
    {
        [Test]
        public void MapsLimitBuyOrder()
        {
            var symbol = Symbol.Create("SBER", SecurityType.Equity, FinamConstants.Market);
            var order = new LimitOrder(symbol, 10, 250.5m, DateTime.UtcNow);

            var finam = FinamOrderMapping.ToFinam(order, "SBER@MISX", "A12345");

            Assert.AreEqual("A12345", finam.AccountId);
            Assert.AreEqual("SBER@MISX", finam.Symbol);
            Assert.AreEqual("SIDE_BUY", finam.Side);
            Assert.AreEqual("ORDER_TYPE_LIMIT", finam.Type);
            Assert.AreEqual("10", finam.Quantity.Value);
            Assert.AreEqual("250.5", finam.LimitPrice.Value);
        }

        [Test]
        public void MapsMarketSellOrder()
        {
            var symbol = Symbol.Create("GAZP", SecurityType.Equity, FinamConstants.Market);
            var order = new MarketOrder(symbol, -5, DateTime.UtcNow);

            var finam = FinamOrderMapping.ToFinam(order, "GAZP@MISX", "A1");

            Assert.AreEqual("SIDE_SELL", finam.Side);
            Assert.AreEqual("ORDER_TYPE_MARKET", finam.Type);
            Assert.AreEqual("5", finam.Quantity.Value);
            Assert.IsNull(finam.LimitPrice);
        }

        [Test]
        public void MapsStopLimitWithCondition()
        {
            var symbol = Symbol.Create("SBER", SecurityType.Equity, FinamConstants.Market);
            var order = new StopLimitOrder(symbol, 10, 260m, 261m, DateTime.UtcNow);

            var finam = FinamOrderMapping.ToFinam(order, "SBER@MISX", "A1");

            Assert.AreEqual("ORDER_TYPE_STOP_LIMIT", finam.Type);
            Assert.AreEqual("261", finam.LimitPrice.Value);
            Assert.AreEqual("260", finam.StopPrice.Value);
            Assert.AreEqual("STOP_CONDITION_LAST_UP", finam.StopCondition);
        }

        [TestCase("ORDER_STATUS_NEW", OrderStatus.Submitted)]
        [TestCase("ORDER_STATUS_PARTIALLY_FILLED", OrderStatus.PartiallyFilled)]
        [TestCase("ORDER_STATUS_FILLED", OrderStatus.Filled)]
        [TestCase("ORDER_STATUS_CANCELED", OrderStatus.Canceled)]
        [TestCase("ORDER_STATUS_REJECTED", OrderStatus.Invalid)]
        public void MapsBrokerageStatusToLean(string finamStatus, OrderStatus expected)
        {
            Assert.AreEqual(expected, FinamOrderMapping.ToLeanStatus(finamStatus));
        }
    }
}
