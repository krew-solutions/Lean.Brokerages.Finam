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
using QuantConnect.Brokerages.Finam.Api;
using QuantConnect.Orders;
using LeanOrderType = QuantConnect.Orders.OrderType;

namespace QuantConnect.Brokerages.Finam
{
    /// <summary>
    /// Translation primitives between LEAN <see cref="Order"/> instances and Finam <see cref="FinamOrder"/>.
    /// </summary>
    /// <remarks>
    /// Side / type / time-in-force enums are encoded as the gRPC-Gateway JSON string form,
    /// i.e. <c>SIDE_BUY</c>, <c>ORDER_TYPE_LIMIT</c>, etc.
    /// </remarks>
    internal static class FinamOrderMapping
    {
        public static FinamOrder ToFinam(Order leanOrder, string brokerageSymbol, string accountId)
        {
            var finamOrder = new FinamOrder
            {
                AccountId = accountId,
                Symbol = brokerageSymbol,
                Quantity = FinamDecimal.From(Math.Abs(leanOrder.Quantity)),
                Side = leanOrder.Direction == OrderDirection.Buy ? "SIDE_BUY" : "SIDE_SELL",
                Type = MapOrderType(leanOrder.Type),
                TimeInForce = MapTimeInForce(leanOrder.TimeInForce),
                ClientOrderId = TruncateClientId(leanOrder.Id.ToStringInvariant()),
                Comment = string.IsNullOrEmpty(leanOrder.Tag) ? null : Truncate(leanOrder.Tag, 128)
            };

            switch (leanOrder)
            {
                case LimitOrder limit:
                    finamOrder.LimitPrice = FinamDecimal.From(limit.LimitPrice);
                    break;
                case StopMarketOrder stop:
                    finamOrder.StopPrice = FinamDecimal.From(stop.StopPrice);
                    finamOrder.StopCondition = stop.Direction == OrderDirection.Buy ? "STOP_CONDITION_LAST_UP" : "STOP_CONDITION_LAST_DOWN";
                    break;
                case StopLimitOrder stopLimit:
                    finamOrder.LimitPrice = FinamDecimal.From(stopLimit.LimitPrice);
                    finamOrder.StopPrice = FinamDecimal.From(stopLimit.StopPrice);
                    finamOrder.StopCondition = stopLimit.Direction == OrderDirection.Buy ? "STOP_CONDITION_LAST_UP" : "STOP_CONDITION_LAST_DOWN";
                    break;
            }

            return finamOrder;
        }

        public static OrderStatus ToLeanStatus(string finamStatus) => finamStatus switch
        {
            "ORDER_STATUS_NEW" or "ORDER_STATUS_PENDING_NEW" or "ORDER_STATUS_FORWARDING" or "ORDER_STATUS_WAIT" or "ORDER_STATUS_WATCHING" => OrderStatus.Submitted,
            "ORDER_STATUS_PARTIALLY_FILLED" => OrderStatus.PartiallyFilled,
            "ORDER_STATUS_FILLED" or "ORDER_STATUS_EXECUTED" or "ORDER_STATUS_SL_EXECUTED" or "ORDER_STATUS_TP_EXECUTED" => OrderStatus.Filled,
            "ORDER_STATUS_CANCELED" or "ORDER_STATUS_PENDING_CANCEL" or "ORDER_STATUS_EXPIRED" or "ORDER_STATUS_DONE_FOR_DAY" => OrderStatus.Canceled,
            "ORDER_STATUS_REJECTED" or "ORDER_STATUS_DENIED_BY_BROKER" or "ORDER_STATUS_REJECTED_BY_EXCHANGE" or "ORDER_STATUS_FAILED" or "ORDER_STATUS_DISABLED" => OrderStatus.Invalid,
            _ => OrderStatus.None
        };

        private static string MapOrderType(LeanOrderType type) => type switch
        {
            LeanOrderType.Market => "ORDER_TYPE_MARKET",
            LeanOrderType.Limit => "ORDER_TYPE_LIMIT",
            LeanOrderType.StopMarket => "ORDER_TYPE_STOP",
            LeanOrderType.StopLimit => "ORDER_TYPE_STOP_LIMIT",
            _ => throw new NotSupportedException($"Order type {type} is not supported by Finam brokerage")
        };

        private static string MapTimeInForce(TimeInForce tif)
        {
            if (tif == TimeInForce.Day) return "TIME_IN_FORCE_DAY";
            if (tif == TimeInForce.GoodTilCanceled) return "TIME_IN_FORCE_GOOD_TILL_CANCEL";
            return "TIME_IN_FORCE_DAY";
        }

        private static string TruncateClientId(string id) => Truncate(id, 20);

        private static string Truncate(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Length <= maxLen ? s : s.Substring(0, maxLen);
        }
    }
}
