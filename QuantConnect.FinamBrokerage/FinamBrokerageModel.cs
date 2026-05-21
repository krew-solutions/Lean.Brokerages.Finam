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

using System.Collections.Generic;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.Finam
{
    /// <summary>
    /// Brokerage model that mirrors Finam Trade API capabilities to LEAN's reality-modeling layer.
    /// </summary>
    /// <remarks>
    /// The proto contract advertises Market, Limit, Stop and StopLimit order types
    /// across Equity / Option / Future security types — that is the supported set declared here.
    /// </remarks>
    public class FinamBrokerageModel : DefaultBrokerageModel
    {
        private static readonly Dictionary<SecurityType, HashSet<OrderType>> SupportedOrderTypes = new()
        {
            [SecurityType.Equity] = new HashSet<OrderType>
            {
                OrderType.Market, OrderType.Limit, OrderType.StopMarket, OrderType.StopLimit
            },
            [SecurityType.Option] = new HashSet<OrderType>
            {
                OrderType.Market, OrderType.Limit
            },
            [SecurityType.Future] = new HashSet<OrderType>
            {
                OrderType.Market, OrderType.Limit, OrderType.StopMarket, OrderType.StopLimit
            },
            [SecurityType.Index] = new HashSet<OrderType>
            {
                OrderType.Market, OrderType.Limit
            }
        };

        public FinamBrokerageModel(AccountType accountType = AccountType.Margin) : base(accountType)
        {
        }

        public override bool CanSubmitOrder(Security security, Order order, out BrokerageMessageEvent message)
        {
            if (!SupportedOrderTypes.TryGetValue(security.Type, out var supported))
            {
                message = new BrokerageMessageEvent(BrokerageMessageType.Warning, "NotSupported",
                    Messages.DefaultBrokerageModel.UnsupportedSecurityType(this, security));
                return false;
            }

            if (!supported.Contains(order.Type))
            {
                message = new BrokerageMessageEvent(BrokerageMessageType.Warning, "NotSupported",
                    Messages.DefaultBrokerageModel.UnsupportedOrderType(this, order, supported));
                return false;
            }

            message = null;
            return true;
        }

        public override bool CanUpdateOrder(Security security, Order order, UpdateOrderRequest request, out BrokerageMessageEvent message)
        {
            // Finam Trade API gRPC contract does not currently expose an UpdateOrder RPC;
            // updates are emulated by cancel + replace at the algorithm level.
            message = new BrokerageMessageEvent(BrokerageMessageType.Warning, "NotSupported",
                "Finam brokerage does not support modifying live orders; cancel and resubmit instead.");
            return false;
        }

        public override IFeeModel GetFeeModel(Security security) => new FinamFeeModel();
    }
}
