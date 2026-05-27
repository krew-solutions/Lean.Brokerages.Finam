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
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Interfaces;
using QuantConnect.Packets;
using QuantConnect.Securities;
using QuantConnect.Util;

namespace QuantConnect.Brokerages.Finam
{
    /// <summary>
    /// Factory that wires Finam brokerage configuration coming from <c>config.json</c> /
    /// <see cref="LiveNodePacket.BrokerageData"/> into a runtime <see cref="FinamBrokerage"/> instance.
    /// </summary>
    public class FinamBrokerageFactory : BrokerageFactory
    {
        public FinamBrokerageFactory() : base(typeof(FinamBrokerage))
        {
        }

        /// <inheritdoc />
        public override Dictionary<string, string> BrokerageData => new()
        {
            { FinamConstants.ConfigSecretToken, Config.Get(FinamConstants.ConfigSecretToken) },
            { FinamConstants.ConfigAccountId,   Config.Get(FinamConstants.ConfigAccountId) },
            { FinamConstants.ConfigApiUrl,      Config.Get(FinamConstants.ConfigApiUrl, FinamConstants.DefaultRestEndpoint) },
            { FinamConstants.ConfigWsUrl,       Config.Get(FinamConstants.ConfigWsUrl, FinamConstants.DefaultWsEndpoint) },
            { FinamConstants.ConfigAccountType, Config.Get(FinamConstants.ConfigAccountType, "margin") }
        };

        /// <inheritdoc />
        public override IBrokerageModel GetBrokerageModel(IOrderProvider orderProvider)
            => new FinamBrokerageModel();

        /// <inheritdoc />
        public override IBrokerage CreateBrokerage(LiveNodePacket job, IAlgorithm algorithm)
        {
            var errors = new List<string>();

            var secret = Read(job, FinamConstants.ConfigSecretToken, errors);
            var accountId = Read(job, FinamConstants.ConfigAccountId, errors);
            var apiUrl = job.BrokerageData.TryGetValue(FinamConstants.ConfigApiUrl, out var url) && !string.IsNullOrEmpty(url)
                ? url
                : FinamConstants.DefaultRestEndpoint;
            var wsUrl = job.BrokerageData.TryGetValue(FinamConstants.ConfigWsUrl, out var ws) && !string.IsNullOrEmpty(ws)
                ? ws
                : FinamConstants.DefaultWsEndpoint;
            var accountTypeValue = job.BrokerageData.TryGetValue(FinamConstants.ConfigAccountType, out var atype) ? atype : "margin";
            var accountType = string.Equals(accountTypeValue, "cash", System.StringComparison.OrdinalIgnoreCase)
                ? AccountType.Cash
                : AccountType.Margin;

            if (errors.Count != 0)
            {
                throw new System.Exception("FinamBrokerageFactory.CreateBrokerage: missing configuration: " + string.Join(", ", errors));
            }

            var brokerage = new FinamBrokerage(
                apiUrl,
                wsUrl,
                secret,
                accountId,
                accountType,
                algorithm,
                algorithm?.Portfolio?.Transactions,
                Composer.Instance.GetPart<IDataAggregator>());

            Composer.Instance.AddPart<IDataQueueHandler>(brokerage);
            return brokerage;
        }

        /// <inheritdoc />
        public override void Dispose()
        {
        }

        private static string Read(LiveNodePacket job, string key, ICollection<string> errors)
        {
            if (job.BrokerageData.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value))
            {
                return value;
            }
            errors.Add(key);
            return null;
        }
    }
}
