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
using Newtonsoft.Json;

namespace QuantConnect.Brokerages.Finam.Api
{
    /// <summary>
    /// WebSocket DTOs for the Finam Trade API <c>tradingInfo</c> channel.
    /// </summary>
    /// <remarks>
    /// Shapes verified against the live stream (the published AsyncAPI was misleading):
    /// <list type="bullet">
    ///   <item>the envelope <c>payload</c> is a <em>JSON string</em> (double-encoded), parsed via
    ///         <see cref="WsEnvelope.PayloadAs{T}"/>;</item>
    ///   <item>inner field names are camelCase (e.g. <c>askSize</c>);</item>
    ///   <item>decimals are the <c>{ "value": "..." }</c> wrapper (<see cref="FinamDecimal"/>), same as REST;</item>
    ///   <item><c>timestamp</c> is a fractional unix epoch (double).</item>
    /// </list>
    /// </remarks>

    /// <summary>Subscription type discriminator (AsyncAPI <c>SubscriptionType</c>).</summary>
    public static class WsSubscriptionType
    {
        public const string Bars = "BARS";
        public const string Quotes = "QUOTES";
        public const string OrderBook = "ORDER_BOOK";
        public const string InstrumentTrades = "INSTRUMENT_TRADES";
        public const string Orders = "ORDERS";
        public const string Trades = "TRADES";
        public const string Account = "ACCOUNT";
    }

    public static class WsAction
    {
        public const string Subscribe = "SUBSCRIBE";
        public const string Unsubscribe = "UNSUBSCRIBE";
        public const string UnsubscribeAll = "UNSUBSCRIBE_ALL";
    }

    /// <summary>Client -> server subscription request.</summary>
    public sealed class WsRequest
    {
        [JsonProperty("action")] public string Action { get; set; }
        [JsonProperty("type", NullValueHandling = NullValueHandling.Ignore)] public string Type { get; set; }
        [JsonProperty("data", NullValueHandling = NullValueHandling.Ignore)] public Dictionary<string, object> Data { get; set; }
        [JsonProperty("token")] public string Token { get; set; }
    }

    /// <summary>
    /// Server -> client envelope. <see cref="Payload"/> is a JSON string; decode it with
    /// <see cref="PayloadAs{T}"/> based on <see cref="SubscriptionType"/>.
    /// </summary>
    public sealed class WsEnvelope
    {
        [JsonProperty("type")] public string Type { get; set; }                       // DATA | ERROR | EVENT
        [JsonProperty("subscription_key")] public string SubscriptionKey { get; set; }
        [JsonProperty("subscription_type")] public string SubscriptionType { get; set; }
        [JsonProperty("timestamp")] public double Timestamp { get; set; }
        [JsonProperty("payload")] public string Payload { get; set; }
        [JsonProperty("error_info")] public WsError ErrorInfo { get; set; }
        [JsonProperty("event_info")] public WsEvent EventInfo { get; set; }

        public bool IsData => string.Equals(Type, "DATA", StringComparison.OrdinalIgnoreCase);
        public bool IsError => string.Equals(Type, "ERROR", StringComparison.OrdinalIgnoreCase);
        public bool IsEvent => string.Equals(Type, "EVENT", StringComparison.OrdinalIgnoreCase);

        /// <summary>Deserializes the (string) <see cref="Payload"/> into the typed payload.</summary>
        public T PayloadAs<T>() => string.IsNullOrEmpty(Payload) ? default : JsonConvert.DeserializeObject<T>(Payload);
    }

    public sealed class WsError
    {
        [JsonProperty("code")] public int Code { get; set; }
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("message")] public string Message { get; set; }
    }

    public sealed class WsEvent
    {
        [JsonProperty("event")] public string Event { get; set; }
        [JsonProperty("code")] public int Code { get; set; }
        [JsonProperty("reason")] public string Reason { get; set; }
    }

    public sealed class WsQuotePayload
    {
        [JsonProperty("quote")] public List<WsQuote> Quote { get; set; }
        [JsonProperty("error")] public WsError Error { get; set; }
    }

    public sealed class WsQuote
    {
        [JsonProperty("symbol")] public string Symbol { get; set; }
        [JsonProperty("timestamp")] public DateTime Timestamp { get; set; }
        [JsonProperty("ask")] public FinamDecimal Ask { get; set; }
        [JsonProperty("askSize")] public FinamDecimal AskSize { get; set; }
        [JsonProperty("bid")] public FinamDecimal Bid { get; set; }
        [JsonProperty("bidSize")] public FinamDecimal BidSize { get; set; }
        [JsonProperty("last")] public FinamDecimal Last { get; set; }
        [JsonProperty("lastSize")] public FinamDecimal LastSize { get; set; }
        [JsonProperty("volume")] public FinamDecimal Volume { get; set; }
    }

    public sealed class WsTradesPayload
    {
        [JsonProperty("symbol")] public string Symbol { get; set; }
        [JsonProperty("trades")] public List<WsTrade> Trades { get; set; }
    }

    public sealed class WsTrade
    {
        [JsonProperty("tradeId")] public string TradeId { get; set; }
        [JsonProperty("mpid")] public string Mpid { get; set; }
        [JsonProperty("timestamp")] public DateTime Timestamp { get; set; }
        [JsonProperty("price")] public FinamDecimal Price { get; set; }
        [JsonProperty("size")] public FinamDecimal Size { get; set; }
        [JsonProperty("side")] public string Side { get; set; }
    }

    // ----------------------------------------------------------- account streams (by account_id)

    /// <summary>Payload of the account <c>TRADES</c> subscription (own executions).</summary>
    public sealed class WsAccountTradesPayload
    {
        [JsonProperty("trades")] public List<WsAccountTrade> Trades { get; set; }
    }

    /// <summary>
    /// One own execution from the account TRADES stream. Carries <see cref="OrderId"/> and
    /// <see cref="AccountId"/>, so a fill is attributable to a LEAN order; <see cref="Price"/> is the
    /// actual execution price.
    /// </summary>
    public sealed class WsAccountTrade
    {
        [JsonProperty("tradeId")] public string TradeId { get; set; }
        [JsonProperty("symbol")] public string Symbol { get; set; }
        [JsonProperty("price")] public FinamDecimal Price { get; set; }
        [JsonProperty("size")] public FinamDecimal Size { get; set; }
        [JsonProperty("side")] public string Side { get; set; }
        [JsonProperty("timestamp")] public DateTime Timestamp { get; set; }
        [JsonProperty("orderId")] public string OrderId { get; set; }
        [JsonProperty("accountId")] public string AccountId { get; set; }
    }

    /// <summary>Payload of the account <c>ORDERS</c> subscription (own order state changes).</summary>
    public sealed class WsOrdersPayload
    {
        [JsonProperty("orders")] public List<WsOrderState> Orders { get; set; }
    }

    /// <summary>Minimal order-state projection: enough to drive non-fill status transitions.</summary>
    public sealed class WsOrderState
    {
        [JsonProperty("orderId")] public string OrderId { get; set; }
        [JsonProperty("execId")] public string ExecId { get; set; }
        [JsonProperty("status")] public string Status { get; set; }
        [JsonProperty("transactAt")] public DateTime? TransactAt { get; set; }
    }
}
