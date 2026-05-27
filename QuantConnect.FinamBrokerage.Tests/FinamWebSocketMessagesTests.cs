/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Licensed under the Apache License, Version 2.0.
*/

using NUnit.Framework;
using Newtonsoft.Json;
using QuantConnect.Brokerages.Finam.Api;
using QuantConnect.Orders;

namespace QuantConnect.Brokerages.Finam.Tests
{
    [TestFixture]
    public class FinamWebSocketMessagesTests
    {
        [Test]
        public void ParsesQuoteDataEnvelope()
        {
            const string json = @"{
                ""type"": ""DATA"",
                ""subscription_type"": ""QUOTES"",
                ""timestamp"": 1700000000,
                ""payload"": { ""quote"": [ { ""symbol"": ""GAZP@MISX"", ""bid"": ""130.5"", ""ask"": ""130.7"", ""bid_size"": ""10"", ""ask_size"": ""12"", ""last"": ""130.6"" } ] }
            }";

            var envelope = JsonConvert.DeserializeObject<WsEnvelope>(json);
            var payload = envelope.Payload.ToObject<WsQuotePayload>();

            Assert.AreEqual(1, payload.Quote.Count);
            Assert.AreEqual(130.5m, WsParse.Dec(payload.Quote[0].Bid));
            Assert.AreEqual(130.7m, WsParse.Dec(payload.Quote[0].Ask));
        }

        [Test]
        public void RecognizesEventAndErrorEnvelopes()
        {
            var ev = JsonConvert.DeserializeObject<WsEnvelope>(
                @"{ ""type"": ""EVENT"", ""timestamp"": 1, ""event_info"": { ""event"": ""HANDSHAKE_SUCCESS"", ""code"": 0, ""reason"": ""ok"" } }");
            Assert.IsTrue(ev.IsEvent);
            Assert.AreEqual("HANDSHAKE_SUCCESS", ev.EventInfo.Event);

            var err = JsonConvert.DeserializeObject<WsEnvelope>(
                @"{ ""type"": ""ERROR"", ""timestamp"": 1, ""error_info"": { ""code"": 401, ""type"": ""UNAUTHENTICATED"", ""message"": ""expired"" } }");
            Assert.IsTrue(err.IsError);
            Assert.AreEqual("UNAUTHENTICATED", err.ErrorInfo.Type);
        }

        [Test]
        public void ParsesAccountTradesEnvelopeWithOrderId()
        {
            const string json = @"{
                ""type"": ""DATA"",
                ""subscription_type"": ""TRADES"",
                ""timestamp"": 1700000000,
                ""payload"": { ""trades"": [
                    { ""trade_id"": ""T1"", ""symbol"": ""SBER@MISX"", ""price"": ""250.4"", ""size"": ""10"", ""side"": ""SIDE_BUY"", ""order_id"": ""ORD42"", ""account_id"": ""A1"" }
                ] }
            }";

            var envelope = JsonConvert.DeserializeObject<WsEnvelope>(json);
            Assert.AreEqual(WsSubscriptionType.Trades, envelope.SubscriptionType);

            var payload = envelope.Payload.ToObject<WsAccountTradesPayload>();
            var trade = payload.Trades[0];

            Assert.AreEqual("ORD42", trade.OrderId);
            Assert.AreEqual("SIDE_BUY", trade.Side);
            Assert.AreEqual(250.4m, WsParse.Dec(trade.Price));
            Assert.AreEqual(10m, WsParse.Dec(trade.Size));
        }

        [Test]
        public void ParsesAccountOrdersEnvelope()
        {
            const string json = @"{
                ""type"": ""DATA"",
                ""subscription_type"": ""ORDERS"",
                ""timestamp"": 1700000000,
                ""payload"": { ""orders"": [ { ""order_id"": ""ORD42"", ""status"": ""ORDER_STATUS_CANCELED"" } ] }
            }";

            var envelope = JsonConvert.DeserializeObject<WsEnvelope>(json);
            var payload = envelope.Payload.ToObject<WsOrdersPayload>();

            Assert.AreEqual("ORD42", payload.Orders[0].OrderId);
            Assert.AreEqual(OrderStatus.Canceled, FinamOrderMapping.ToLeanStatus(payload.Orders[0].Status));
        }

        [Test]
        public void SerializesSubscribeRequest()
        {
            var request = new WsRequest
            {
                Action = WsAction.Subscribe,
                Type = WsSubscriptionType.Bars,
                Data = new() { ["symbol"] = "SBER@MISX", ["timeframe"] = "TIME_FRAME_M1" },
                Token = "jwt"
            };

            var json = JsonConvert.SerializeObject(request);

            StringAssert.Contains("\"action\":\"SUBSCRIBE\"", json);
            StringAssert.Contains("\"type\":\"BARS\"", json);
            StringAssert.Contains("\"symbol\":\"SBER@MISX\"", json);
            StringAssert.Contains("\"token\":\"jwt\"", json);
        }
    }
}
