/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Licensed under the Apache License, Version 2.0.
*/

using NUnit.Framework;
using NUnit.Framework.Legacy;
using Newtonsoft.Json;
using QuantConnect.Brokerages.Finam.Api;
using QuantConnect.Orders;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace QuantConnect.Brokerages.Finam.Tests
{
    /// <summary>
    /// Verifies parsing of the real Finam WebSocket wire format (confirmed against the live stream):
    /// the envelope <c>payload</c> is a JSON <em>string</em>, inner fields are camelCase, and decimals
    /// use the <c>{ "value": "..." }</c> wrapper. <see cref="WrapEnvelope"/> double-encodes the payload
    /// exactly as the server does.
    /// </summary>
    [TestFixture]
    public class FinamWebSocketMessagesTests
    {
        private static string WrapEnvelope(string subscriptionType, string innerPayloadJson)
            => "{\"type\":\"DATA\",\"subscription_type\":\"" + subscriptionType +
               "\",\"subscription_key\":\"SBER@MISX\",\"timestamp\":1779901583.237934639,\"payload\":" +
               JsonConvert.SerializeObject(innerPayloadJson) + "}";

        [Test]
        public void ParsesQuoteDataEnvelope()
        {
            // Decimals are {value} objects; field names camelCase; volume uses scientific notation.
            var inner = @"{""quote"":[{""symbol"":""GAZP@MISX"",""timestamp"":""2026-05-27T17:06:23Z"",
                ""ask"":{""value"":""130.7""},""askSize"":{""value"":""12.0""},
                ""bid"":{""value"":""130.5""},""bidSize"":{""value"":""10.0""},
                ""last"":{""value"":""130.6""},""volume"":{""value"":""2.0094773E7""}}]}";
            var json = WrapEnvelope(WsSubscriptionType.Quotes, inner);

            var envelope = JsonConvert.DeserializeObject<WsEnvelope>(json);
            Assert.IsTrue(envelope.IsData);
            Assert.AreEqual(WsSubscriptionType.Quotes, envelope.SubscriptionType);

            var payload = envelope.PayloadAs<WsQuotePayload>();
            Assert.AreEqual(1, payload.Quote.Count);
            var q = payload.Quote[0];
            Assert.AreEqual("GAZP@MISX", q.Symbol);
            Assert.AreEqual(130.5m, q.Bid.AsDecimal());
            Assert.AreEqual(130.7m, q.Ask.AsDecimal());
            Assert.AreEqual(12m, q.AskSize.AsDecimal());
            Assert.AreEqual(20094773m, q.Volume.AsDecimal()); // 2.0094773E7 parsed via NumberStyles.Float
        }

        [Test]
        public void RecognizesEventAndErrorEnvelopes()
        {
            var ev = JsonConvert.DeserializeObject<WsEnvelope>(
                @"{ ""type"": ""EVENT"", ""timestamp"": 1779901583.23, ""event_info"": { ""event"": ""HANDSHAKE_SUCCESS"", ""code"": 1000, ""reason"": ""Authentication successful"" } }");
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
            var inner = @"{""trades"":[{""tradeId"":""T1"",""symbol"":""SBER@MISX"",
                ""price"":{""value"":""250.4""},""size"":{""value"":""10.0""},""side"":""SIDE_BUY"",
                ""orderId"":""ORD42"",""accountId"":""A1""}]}";
            var json = WrapEnvelope(WsSubscriptionType.Trades, inner);

            var envelope = JsonConvert.DeserializeObject<WsEnvelope>(json);
            Assert.AreEqual(WsSubscriptionType.Trades, envelope.SubscriptionType);

            var trade = envelope.PayloadAs<WsAccountTradesPayload>().Trades[0];
            Assert.AreEqual("ORD42", trade.OrderId);
            Assert.AreEqual("SIDE_BUY", trade.Side);
            Assert.AreEqual(250.4m, trade.Price.AsDecimal());
            Assert.AreEqual(10m, trade.Size.AsDecimal());
        }

        [Test]
        public void ParsesAccountOrdersEnvelope()
        {
            var inner = @"{""orders"":[{""orderId"":""ORD42"",""status"":""ORDER_STATUS_CANCELED""}]}";
            var json = WrapEnvelope(WsSubscriptionType.Orders, inner);

            var envelope = JsonConvert.DeserializeObject<WsEnvelope>(json);
            var order = envelope.PayloadAs<WsOrdersPayload>().Orders[0];

            Assert.AreEqual("ORD42", order.OrderId);
            Assert.AreEqual(OrderStatus.Canceled, FinamOrderMapping.ToLeanStatus(order.Status));
        }

        [Test]
        public void SerializesSubscribeRequest()
        {
            var request = new WsRequest
            {
                Action = WsAction.Subscribe,
                Type = WsSubscriptionType.Quotes,
                Data = new() { ["symbols"] = new[] { "SBER@MISX" } },
                Token = "jwt"
            };

            var json = JsonConvert.SerializeObject(request);

            StringAssert.Contains("\"action\":\"SUBSCRIBE\"", json);
            StringAssert.Contains("\"type\":\"QUOTES\"", json);
            StringAssert.Contains("\"symbols\":[\"SBER@MISX\"]", json);
            StringAssert.Contains("\"token\":\"jwt\"", json);
        }
    }
}
