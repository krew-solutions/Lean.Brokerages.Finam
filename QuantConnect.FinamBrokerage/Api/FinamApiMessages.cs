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
    /// REST DTOs mirroring the Finam Trade API gRPC-Gateway JSON contract.
    /// The mapping follows <c>grpc/tradeapi/v1/*</c> protos via the protoc-gen-openapiv2 gateway.
    /// </summary>
    public sealed class FinamDecimal
    {
        [JsonProperty("value")]
        public string Value { get; set; }

        public decimal AsDecimal() => string.IsNullOrEmpty(Value) ? 0m : decimal.Parse(Value, System.Globalization.CultureInfo.InvariantCulture);

        public static FinamDecimal From(decimal value) => new() { Value = value.ToString(System.Globalization.CultureInfo.InvariantCulture) };
    }

    public sealed class FinamMoney
    {
        [JsonProperty("currencyCode")] public string CurrencyCode { get; set; }
        [JsonProperty("units")] public string Units { get; set; }
        [JsonProperty("nanos")] public int Nanos { get; set; }

        public decimal AsDecimal()
        {
            var u = string.IsNullOrEmpty(Units) ? 0m : decimal.Parse(Units, System.Globalization.CultureInfo.InvariantCulture);
            return u + Nanos / 1_000_000_000m;
        }
    }

    public sealed class AuthRequest
    {
        [JsonProperty("secret")] public string Secret { get; set; }
    }

    public sealed class AuthResponse
    {
        [JsonProperty("token")] public string Token { get; set; }
    }

    public sealed class TokenDetailsRequest
    {
        [JsonProperty("token")] public string Token { get; set; }
    }

    public sealed class TokenDetailsResponse
    {
        [JsonProperty("createdAt")] public DateTime CreatedAt { get; set; }
        [JsonProperty("expiresAt")] public DateTime ExpiresAt { get; set; }
        [JsonProperty("accountIds")] public List<string> AccountIds { get; set; }
        [JsonProperty("readonly")] public bool ReadOnly { get; set; }
    }

    public sealed class FinamPosition
    {
        [JsonProperty("symbol")] public string Symbol { get; set; }
        [JsonProperty("quantity")] public FinamDecimal Quantity { get; set; }
        [JsonProperty("averagePrice")] public FinamDecimal AveragePrice { get; set; }
        [JsonProperty("currentPrice")] public FinamDecimal CurrentPrice { get; set; }
        [JsonProperty("unrealizedPnl")] public FinamDecimal UnrealizedPnL { get; set; }
        [JsonProperty("dailyPnl")] public FinamDecimal DailyPnL { get; set; }
    }

    public sealed class GetAccountResponse
    {
        [JsonProperty("accountId")] public string AccountId { get; set; }
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("status")] public string Status { get; set; }
        [JsonProperty("equity")] public FinamDecimal Equity { get; set; }
        [JsonProperty("unrealizedProfit")] public FinamDecimal UnrealizedProfit { get; set; }
        [JsonProperty("positions")] public List<FinamPosition> Positions { get; set; }
        [JsonProperty("cash")] public List<FinamMoney> Cash { get; set; }
    }

    public sealed class FinamOrder
    {
        [JsonProperty("accountId")] public string AccountId { get; set; }
        [JsonProperty("symbol")] public string Symbol { get; set; }
        [JsonProperty("quantity")] public FinamDecimal Quantity { get; set; }
        [JsonProperty("side")] public string Side { get; set; }
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("timeInForce")] public string TimeInForce { get; set; }
        [JsonProperty("limitPrice")] public FinamDecimal LimitPrice { get; set; }
        [JsonProperty("stopPrice")] public FinamDecimal StopPrice { get; set; }
        [JsonProperty("stopCondition")] public string StopCondition { get; set; }
        [JsonProperty("clientOrderId")] public string ClientOrderId { get; set; }
        [JsonProperty("validBefore")] public string ValidBefore { get; set; }
        [JsonProperty("comment")] public string Comment { get; set; }
    }

    public sealed class OrderState
    {
        [JsonProperty("orderId")] public string OrderId { get; set; }
        [JsonProperty("execId")] public string ExecId { get; set; }
        [JsonProperty("status")] public string Status { get; set; }
        [JsonProperty("order")] public FinamOrder Order { get; set; }
        [JsonProperty("transactAt")] public DateTime? TransactAt { get; set; }
        [JsonProperty("acceptAt")] public DateTime? AcceptAt { get; set; }
        [JsonProperty("withdrawAt")] public DateTime? WithdrawAt { get; set; }
        [JsonProperty("initialQuantity")] public FinamDecimal InitialQuantity { get; set; }
        [JsonProperty("executedQuantity")] public FinamDecimal ExecutedQuantity { get; set; }
        [JsonProperty("remainingQuantity")] public FinamDecimal RemainingQuantity { get; set; }
    }

    public sealed class OrdersResponse
    {
        [JsonProperty("orders")] public List<OrderState> Orders { get; set; }
    }

    public sealed class BarsResponse
    {
        [JsonProperty("symbol")] public string Symbol { get; set; }
        [JsonProperty("bars")] public List<FinamBar> Bars { get; set; }
    }

    public sealed class FinamBar
    {
        [JsonProperty("timestamp")] public DateTime Timestamp { get; set; }
        [JsonProperty("open")] public FinamDecimal Open { get; set; }
        [JsonProperty("high")] public FinamDecimal High { get; set; }
        [JsonProperty("low")] public FinamDecimal Low { get; set; }
        [JsonProperty("close")] public FinamDecimal Close { get; set; }
        [JsonProperty("volume")] public FinamDecimal Volume { get; set; }
    }

    public sealed class QuoteResponse
    {
        [JsonProperty("symbol")] public string Symbol { get; set; }
        [JsonProperty("quote")] public FinamQuote Quote { get; set; }
    }

    public sealed class FinamQuote
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

    public sealed class GetAssetResponse
    {
        [JsonProperty("board")] public string Board { get; set; }
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("ticker")] public string Ticker { get; set; }
        [JsonProperty("mic")] public string Mic { get; set; }
        [JsonProperty("isin")] public string Isin { get; set; }
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("decimals")] public int Decimals { get; set; }
        [JsonProperty("minStep")] public long MinStep { get; set; }
        [JsonProperty("lotSize")] public FinamDecimal LotSize { get; set; }
        [JsonProperty("quoteCurrency")] public string QuoteCurrency { get; set; }
    }

    /// <summary>
    /// gRPC-Gateway error envelope: <c>{ "code": int, "message": string, "details": [...] }</c>.
    /// </summary>
    public sealed class FinamError
    {
        [JsonProperty("code")] public int Code { get; set; }
        [JsonProperty("message")] public string Message { get; set; }
    }
}
