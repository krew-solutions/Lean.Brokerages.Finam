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
using QuantConnect.Brokerages.Finam;
using QuantConnect.Brokerages.Finam.Api;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Interfaces;

namespace QuantConnect.ToolBox.FinamDownloader
{
    /// <summary>
    /// IDataDownloader implementation that yields LEAN-formatted bars from the Finam REST API.
    /// </summary>
    public class FinamDataDownloader : IDataDownloader, IDisposable
    {
        private readonly FinamApiClient _api;
        private readonly FinamSymbolMapper _symbolMapper;

        public FinamDataDownloader(string apiUrl, string secret)
        {
            _api = new FinamApiClient(apiUrl ?? FinamConstants.DefaultRestEndpoint, secret);
            _symbolMapper = new FinamSymbolMapper();
        }

        public IEnumerable<BaseData> Get(DataDownloaderGetParameters parameters)
        {
            if (parameters == null) yield break;

            var timeframe = MapResolution(parameters.Resolution);
            if (timeframe == null) yield break;

            var brokerageSymbol = _symbolMapper.GetBrokerageSymbol(parameters.Symbol);
            var response = _api.GetBarsAsync(brokerageSymbol, timeframe, parameters.StartUtc, parameters.EndUtc).GetAwaiter().GetResult();
            if (response?.Bars == null) yield break;

            var period = parameters.Resolution.ToTimeSpan();
            foreach (var bar in response.Bars)
            {
                yield return new TradeBar(
                    bar.Timestamp,
                    parameters.Symbol,
                    bar.Open?.AsDecimal() ?? 0m,
                    bar.High?.AsDecimal() ?? 0m,
                    bar.Low?.AsDecimal() ?? 0m,
                    bar.Close?.AsDecimal() ?? 0m,
                    bar.Volume?.AsDecimal() ?? 0m,
                    period);
            }
        }

        private static string MapResolution(Resolution resolution) => resolution switch
        {
            Resolution.Minute => "TIME_FRAME_M1",
            Resolution.Hour => "TIME_FRAME_H1",
            Resolution.Daily => "TIME_FRAME_D",
            _ => null
        };

        public void Dispose()
        {
            _api?.Dispose();
        }
    }
}
