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
using System.Collections.Concurrent;
using QuantConnect.Brokerages.Finam.Api;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;

namespace QuantConnect.Brokerages.Finam
{
    /// <summary>
    /// Push-based order lifecycle: fills come from the account <c>TRADES</c> stream (with the real
    /// execution price), non-fill status transitions from the account <c>ORDERS</c> stream.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Fill events are sourced <em>only</em> from <see cref="WsAccountTrade"/> — it carries
    /// <c>order_id</c> (attribution) and <c>price</c> (execution price). The REST place-order
    /// response is never treated as a fill; <see cref="PlaceOrder"/> emits at most a
    /// <see cref="OrderStatus.Submitted"/>.
    /// </para>
    /// <para>
    /// Commissions are not present on <see cref="WsAccountTrade"/> (Finam reports them as separate
    /// COMMISSION transactions), so fill events carry <see cref="OrderFee.Zero"/> and broker fees
    /// are reconciled through the periodic cash sync.
    /// </para>
    /// </remarks>
    public partial class FinamBrokerage
    {
        private readonly ConcurrentDictionary<string, decimal> _cumulativeFillByBrokerageId = new();
        private readonly ConcurrentDictionary<int, OrderStatus> _lastEmittedStatus = new();

        private void OnAccountTrades(WsAccountTradesPayload payload)
        {
            if (payload?.Trades == null) return;

            foreach (var trade in payload.Trades)
            {
                if (string.IsNullOrEmpty(trade.OrderId) ||
                    !_ordersByBrokerageId.TryGetValue(trade.OrderId, out var order))
                {
                    // An execution we can't attribute to a tracked LEAN order (e.g. placed in another
                    // session). Nothing to report into this algorithm.
                    continue;
                }

                var fillPrice = WsParse.Dec(trade.Price);
                var absSize = Math.Abs(WsParse.Dec(trade.Size));
                if (fillPrice <= 0m || absSize <= 0m) continue;

                var isSell = string.Equals(trade.Side, "SIDE_SELL", StringComparison.OrdinalIgnoreCase);
                var fillQuantity = isSell ? -absSize : absSize;

                var cumulative = _cumulativeFillByBrokerageId.AddOrUpdate(trade.OrderId, absSize, (_, prev) => prev + absSize);
                var status = cumulative >= Math.Abs(order.Quantity) ? OrderStatus.Filled : OrderStatus.PartiallyFilled;

                var time = trade.Timestamp == default ? DateTime.UtcNow : trade.Timestamp;
                OnOrderEvent(new OrderEvent(order, time, OrderFee.Zero, "Finam fill")
                {
                    Status = status,
                    FillPrice = fillPrice,
                    FillQuantity = fillQuantity
                });

                if (status == OrderStatus.Filled)
                {
                    _lastEmittedStatus[order.Id] = OrderStatus.Filled;
                }
            }
        }

        private void OnOrderStates(WsOrdersPayload payload)
        {
            if (payload?.Orders == null) return;

            foreach (var state in payload.Orders)
            {
                if (string.IsNullOrEmpty(state.OrderId) ||
                    !_ordersByBrokerageId.TryGetValue(state.OrderId, out var order))
                {
                    continue;
                }

                var status = FinamOrderMapping.ToLeanStatus(state.Status);

                // Fills (and unknown statuses) are driven by the TRADES stream; skip them here to
                // avoid double-reporting an execution without a price.
                if (status is OrderStatus.Filled or OrderStatus.PartiallyFilled or OrderStatus.None)
                {
                    continue;
                }

                EmitStatusOnce(order, status);
            }
        }

        /// <summary>
        /// Emits a status-only <see cref="OrderEvent"/> at most once per (order, status) so the
        /// ORDERS stream and the optimistic <see cref="PlaceOrder"/>/<see cref="CancelOrder"/>
        /// emits don't produce duplicates.
        /// </summary>
        private void EmitStatusOnce(Order order, OrderStatus status)
        {
            if (status == OrderStatus.None) return;

            if (_lastEmittedStatus.TryGetValue(order.Id, out var previous))
            {
                if (previous == status) return;

                // Never downgrade out of a terminal state (e.g. a late Submitted after a fill won the race).
                var isTerminal = previous is OrderStatus.Filled or OrderStatus.Canceled or OrderStatus.Invalid;
                var isNonTerminalUpdate = status is OrderStatus.Submitted or OrderStatus.CancelPending or OrderStatus.New;
                if (isTerminal && isNonTerminalUpdate) return;
            }
            _lastEmittedStatus[order.Id] = status;

            OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero)
            {
                Status = status
            });
        }
    }
}
