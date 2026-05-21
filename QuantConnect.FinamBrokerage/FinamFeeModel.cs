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
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.Finam
{
    /// <summary>
    /// Approximate Finam retail commission model.
    /// </summary>
    /// <remarks>
    /// Finam уровня "Стандартный" — 0.0354% от оборота для акций MOEX, минимум 35 ₽,
    /// для срочного рынка фиксированная ставка за контракт.
    /// Эти значения нужно подтверждать тарифом конкретного клиента.
    /// </remarks>
    public class FinamFeeModel : FeeModel
    {
        private const decimal MoexEquityRate = 0.000354m;
        private const decimal MoexEquityMinFee = 35m;
        private const decimal FortsContractFee = 0.45m;
        private const decimal UsEquityPerShare = 0.02m;
        private const decimal UsEquityMinFee = 1m;

        public override OrderFee GetOrderFee(OrderFeeParameters parameters)
        {
            var security = parameters.Security;
            var order = parameters.Order;

            var orderValue = Math.Abs(order.GetValue(security));
            var quantity = Math.Abs(order.Quantity);

            decimal fee;
            string currency = "RUB";

            switch (security.Type)
            {
                case SecurityType.Future:
                case SecurityType.Option when string.Equals(security.Symbol.ID.Market, FinamConstants.Market, StringComparison.OrdinalIgnoreCase):
                    fee = quantity * FortsContractFee;
                    break;

                case SecurityType.Equity when string.Equals(security.Symbol.ID.Market, Market.USA, StringComparison.OrdinalIgnoreCase):
                    fee = Math.Max(quantity * UsEquityPerShare, UsEquityMinFee);
                    currency = "USD";
                    break;

                default:
                    fee = Math.Max(orderValue * MoexEquityRate, MoexEquityMinFee);
                    break;
            }

            return new OrderFee(new CashAmount(fee, currency));
        }
    }
}
