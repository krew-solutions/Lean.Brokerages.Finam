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
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using QuantConnect.Brokerages.Finam.Api;
using QuantConnect.Data;
using QuantConnect.Interfaces;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Packets;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.Finam
{
    /// <summary>
    /// LEAN brokerage implementation for the Finam Trade API.
    /// </summary>
    /// <remarks>
    /// The implementation talks to Finam's gRPC-Gateway REST endpoint for order management,
    /// account holdings and historical candles. Live streaming (quotes, trades, order updates)
    /// is exposed through <see cref="IDataQueueHandler"/> via the matching <c>Subscribe*</c>
    /// stream RPCs. JWT lifecycle is owned by <see cref="FinamApiClient"/>.
    /// </remarks>
    public partial class FinamBrokerage : Brokerage, IDataQueueHandler
    {
        private readonly string _accountId;
        private readonly string _wsUrl;
        private readonly AccountType _accountType;
        private readonly IAlgorithm _algorithm;
        private readonly IOrderProvider _orderProvider;
        private readonly FinamApiClient _api;
        private readonly FinamSymbolMapper _symbolMapper;

        private readonly ConcurrentDictionary<string, Order> _ordersByBrokerageId = new();
        private readonly ConcurrentDictionary<int, string> _leanToBrokerageOrderId = new();

        private CancellationTokenSource _cts;
        private volatile bool _isConnected;

        /// <summary>
        /// Convenience constructor exposed for unit tests; production code reaches
        /// <see cref="FinamBrokerage"/> through <see cref="FinamBrokerageFactory"/>.
        /// </summary>
        public FinamBrokerage(string apiUrl, string wsUrl, string secret, string accountId, AccountType accountType,
            IAlgorithm algorithm, IOrderProvider orderProvider, IDataAggregator aggregator)
            : base("Finam Brokerage")
        {
            if (string.IsNullOrEmpty(accountId)) throw new ArgumentException("Finam account id required", nameof(accountId));

            _accountId = accountId;
            _wsUrl = string.IsNullOrEmpty(wsUrl) ? FinamConstants.DefaultWsEndpoint : wsUrl;
            _accountType = accountType;
            _algorithm = algorithm;
            _orderProvider = orderProvider;
            _symbolMapper = new FinamSymbolMapper();
            _api = new FinamApiClient(apiUrl ?? FinamConstants.DefaultRestEndpoint, secret);
            _aggregator = aggregator;
        }

        /// <inheritdoc />
        public override bool IsConnected => _isConnected;

        /// <inheritdoc />
        public override void Connect()
        {
            if (_isConnected) return;

            try
            {
                _api.AuthenticateAsync().GetAwaiter().GetResult();
                _cts = new CancellationTokenSource();
                _isConnected = true;
                StartStreaming(_cts.Token);
                Log.Trace($"FinamBrokerage.Connect: connected to account {_accountId}");
            }
            catch (Exception ex)
            {
                _isConnected = false;
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, "ConnectFailed", ex.Message));
                throw;
            }
        }

        /// <inheritdoc />
        public override void Disconnect()
        {
            if (!_isConnected) return;
            _cts?.Cancel();
            _isConnected = false;
            Log.Trace("FinamBrokerage.Disconnect");
        }

        /// <inheritdoc />
        public override List<Order> GetOpenOrders()
        {
            var response = _api.GetOrdersAsync(_accountId).GetAwaiter().GetResult();
            if (response?.Orders == null) return new List<Order>();

            var open = new List<Order>();
            foreach (var state in response.Orders)
            {
                var status = FinamOrderMapping.ToLeanStatus(state.Status);
                if (status is OrderStatus.Filled or OrderStatus.Canceled or OrderStatus.Invalid)
                    continue;

                var leanOrder = ConvertBrokerageOrder(state);
                if (leanOrder != null)
                {
                    open.Add(leanOrder);
                    _ordersByBrokerageId[state.OrderId] = leanOrder;
                }
            }

            return open;
        }

        /// <inheritdoc />
        public override List<Holding> GetAccountHoldings()
        {
            var account = _api.GetAccountAsync(_accountId).GetAwaiter().GetResult();
            if (account?.Positions == null) return new List<Holding>();

            var holdings = new List<Holding>(account.Positions.Count);
            foreach (var p in account.Positions)
            {
                var quantity = p.Quantity?.AsDecimal() ?? 0m;
                if (quantity == 0m) continue;

                var symbol = TryParseSymbol(p.Symbol);
                if (symbol == null) continue;

                holdings.Add(new Holding
                {
                    Symbol = symbol,
                    Quantity = quantity,
                    AveragePrice = p.AveragePrice?.AsDecimal() ?? 0m,
                    MarketPrice = p.CurrentPrice?.AsDecimal() ?? 0m,
                    UnrealizedPnL = p.UnrealizedPnL?.AsDecimal() ?? 0m
                });
            }
            return holdings;
        }

        /// <inheritdoc />
        public override List<CashAmount> GetCashBalance()
        {
            var account = _api.GetAccountAsync(_accountId).GetAwaiter().GetResult();
            if (account?.Cash == null) return new List<CashAmount>();

            return account.Cash
                .Where(m => !string.IsNullOrEmpty(m.CurrencyCode))
                .Select(m => new CashAmount(m.AsDecimal(), m.CurrencyCode))
                .ToList();
        }

        /// <inheritdoc />
        public override bool PlaceOrder(Order order)
        {
            if (!_isConnected)
            {
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, "NotConnected",
                    "Cannot place order: brokerage is not connected"));
                return false;
            }

            try
            {
                var brokerageSymbol = _symbolMapper.GetBrokerageSymbol(order.Symbol);
                var request = FinamOrderMapping.ToFinam(order, brokerageSymbol, _accountId);
                var state = _api.PlaceOrderAsync(_accountId, request).GetAwaiter().GetResult();

                if (state == null || string.IsNullOrEmpty(state.OrderId))
                {
                    OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, "Finam returned empty response")
                    {
                        Status = OrderStatus.Invalid
                    });
                    return false;
                }

                order.BrokerId.Add(state.OrderId);
                _ordersByBrokerageId[state.OrderId] = order;
                _leanToBrokerageOrderId[order.Id] = state.OrderId;

                // Report acceptance only. Never treat the REST place response as a fill: actual
                // executions (with price) arrive on the account TRADES stream; final status on ORDERS.
                EmitStatusOnce(order, OrderStatus.Submitted);
                return true;
            }
            catch (Exception ex)
            {
                OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, ex.Message) { Status = OrderStatus.Invalid });
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, "PlaceOrderFailed", ex.Message));
                return false;
            }
        }

        /// <inheritdoc />
        public override bool UpdateOrder(Order order)
        {
            // Finam Trade API does not expose an order modification RPC; updates must be
            // performed as cancel + new order from the algorithm side.
            OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "UpdateNotSupported",
                "Finam brokerage does not support order updates; cancel and resubmit instead."));
            return false;
        }

        /// <inheritdoc />
        public override bool CancelOrder(Order order)
        {
            if (order.BrokerId == null || order.BrokerId.Count == 0)
            {
                Log.Error($"FinamBrokerage.CancelOrder: order {order.Id} has no brokerage id");
                return false;
            }

            var brokerageId = order.BrokerId[0];
            try
            {
                var state = _api.CancelOrderAsync(_accountId, brokerageId).GetAwaiter().GetResult();
                // Optimistic: mark cancel as pending; the final Canceled is confirmed by the ORDERS stream.
                var status = state == null ? OrderStatus.CancelPending : FinamOrderMapping.ToLeanStatus(state.Status);
                EmitStatusOnce(order, status == OrderStatus.None ? OrderStatus.CancelPending : status);
                return true;
            }
            catch (Exception ex)
            {
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "CancelFailed", ex.Message));
                return false;
            }
        }

        public override void Dispose()
        {
            try { Disconnect(); } catch { /* swallow */ }
            _webSocket?.Dispose();
            _aggregator?.Dispose();
            _cts?.Dispose();
            _api.Dispose();
            base.Dispose();
        }

        private Order ConvertBrokerageOrder(OrderState state)
        {
            if (state?.Order == null) return null;

            var symbol = TryParseSymbol(state.Order.Symbol);
            if (symbol == null) return null;

            var quantity = state.Order.Quantity?.AsDecimal() ?? 0m;
            if (string.Equals(state.Order.Side, "SIDE_SELL", StringComparison.OrdinalIgnoreCase))
                quantity = -quantity;

            var time = state.TransactAt ?? DateTime.UtcNow;

            Order order = state.Order.Type switch
            {
                "ORDER_TYPE_MARKET" => new MarketOrder(symbol, quantity, time),
                "ORDER_TYPE_LIMIT" => new LimitOrder(symbol, quantity, state.Order.LimitPrice?.AsDecimal() ?? 0m, time),
                "ORDER_TYPE_STOP" => new StopMarketOrder(symbol, quantity, state.Order.StopPrice?.AsDecimal() ?? 0m, time),
                "ORDER_TYPE_STOP_LIMIT" => new StopLimitOrder(symbol, quantity, state.Order.StopPrice?.AsDecimal() ?? 0m,
                    state.Order.LimitPrice?.AsDecimal() ?? 0m, time),
                _ => null
            };

            if (order != null)
            {
                order.BrokerId.Add(state.OrderId);
                order.Status = FinamOrderMapping.ToLeanStatus(state.Status);
            }
            return order;
        }

        private Symbol TryParseSymbol(string brokerageSymbol)
        {
            if (string.IsNullOrEmpty(brokerageSymbol)) return null;
            try
            {
                return _symbolMapper.GetLeanSymbol(brokerageSymbol, SecurityType.Equity, FinamConstants.Market);
            }
            catch (Exception ex)
            {
                Log.Error($"FinamBrokerage: failed to parse symbol '{brokerageSymbol}': {ex.Message}");
                return null;
            }
        }
    }
}
