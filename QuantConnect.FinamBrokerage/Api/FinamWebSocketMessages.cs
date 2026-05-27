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
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace QuantConnect.Brokerages.Finam.Api
{
    /// <summary>
    /// WebSocket DTOs mirroring the Finam Trade API AsyncAPI 1.0 contract (<c>tradingInfo</c> channel).
    /// </summary>
    /// <remarks>
    /// Unlike the REST gRPC-Gateway, the WebSocket frames encode <c>google.type.Decimal</c> as a
    /// <em>bare JSON string</em> (e.g. <c>"250.5"</c>) rather than the <c>{ "value": "250.5" }</c>
    /// wrapper. Hence decimal fields here are plain <see cref="string"/> parsed via
    /// <see cref="WsParse.Dec"/>.
    /// </remarks>
    public static class WsParse
    {
        public static decimal Dec(string s) =>
            string.IsNullOrEmpty(s) ? 0m : decimal.Parse(s, NumberStyles.Any, CultureInfo.InvariantCulture);
    }

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

    /// <summary>Server -> client envelope. <see cref="Payload"/> is parsed lazily by <see cref="SubscriptionType"/>.</summary>
    public sealed class WsEnvelope
    {
        [JsonProperty("type")] public string Type { get; set; }                       // DATA | ERROR | EVENT
        [JsonProperty("subscription_key")] public string SubscriptionKey { get; set; }
        [JsonProperty("subscription_type")] public string SubscriptionType { get; set; }
        [JsonProperty("timestamp")] public long Timestamp { get; set; }
        [JsonProperty("payload")] public JToken Payload { get; set; }
        [JsonProperty("error_info")] public WsError ErrorInfo { get; set; }
        [JsonProperty("event_info")] public WsEvent EventInfo { get; set; }

        public bool IsData => string.Equals(Type, "DATA", StringComparison.OrdinalIgnoreCase);
        public bool IsError => string.Equals(Type, "ERROR", StringComparison.OrdinalIgnoreCase);
        public bool IsEvent => string.Equals(Type, "EVENT", StringComparison.OrdinalIgnoreCase);
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
        [JsonProperty("ask")] public string Ask { get; set; }
        [JsonProperty("ask_size")] public string AskSize { get; set; }
        [JsonProperty("bid")] public string Bid { get; set; }
        [JsonProperty("bid_size")] public string BidSize { get; set; }
        [JsonProperty("last")] public string Last { get; set; }
        [JsonProperty("last_size")] public string LastSize { get; set; }
        [JsonProperty("volume")] public string Volume { get; set; }
    }

    public sealed class WsTradesPayload
    {
        [JsonProperty("symbol")] public string Symbol { get; set; }
        [JsonProperty("trades")] public List<WsTrade> Trades { get; set; }
    }

    public sealed class WsTrade
    {
        [JsonProperty("trade_id")] public string TradeId { get; set; }
        [JsonProperty("mpid")] public string Mpid { get; set; }
        [JsonProperty("timestamp")] public DateTime Timestamp { get; set; }
        [JsonProperty("price")] public string Price { get; set; }
        [JsonProperty("size")] public string Size { get; set; }
        [JsonProperty("side")] public string Side { get; set; }
    }

    // ----------------------------------------------------------- account streams (by account_id)

    /// <summary>Payload of the account <c>TRADES</c> subscription (own executions).</summary>
    public sealed class WsAccountTradesPayload
    {
        [JsonProperty("trades")] public List<WsAccountTrade> Trades { get; set; }
    }

    /// <summary>
    /// One own execution. Unlike the market <see cref="WsTrade"/> tape, this carries
    /// <see cref="OrderId"/> and <see cref="AccountId"/>, so a fill is attributable to a LEAN order;
    /// <see cref="Price"/> is the actual execution price.
    /// </summary>
    public sealed class WsAccountTrade
    {
        [JsonProperty("trade_id")] public string TradeId { get; set; }
        [JsonProperty("symbol")] public string Symbol { get; set; }
        [JsonProperty("price")] public string Price { get; set; }
        [JsonProperty("size")] public string Size { get; set; }
        [JsonProperty("side")] public string Side { get; set; }
        [JsonProperty("timestamp")] public DateTime Timestamp { get; set; }
        [JsonProperty("order_id")] public string OrderId { get; set; }
        [JsonProperty("account_id")] public string AccountId { get; set; }
    }

    /// <summary>Payload of the account <c>ORDERS</c> subscription (own order state changes).</summary>
    public sealed class WsOrdersPayload
    {
        [JsonProperty("orders")] public List<WsOrderState> Orders { get; set; }
    }

    /// <summary>Minimal order-state projection: enough to drive non-fill status transitions.</summary>
    public sealed class WsOrderState
    {
        [JsonProperty("order_id")] public string OrderId { get; set; }
        [JsonProperty("exec_id")] public string ExecId { get; set; }
        [JsonProperty("status")] public string Status { get; set; }
        [JsonProperty("transact_at")] public DateTime? TransactAt { get; set; }
    }
}
