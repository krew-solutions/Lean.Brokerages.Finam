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

namespace QuantConnect.Brokerages.Finam
{
    /// <summary>
    /// Compile-time constants shared by the Finam brokerage implementation.
    /// </summary>
    public static class FinamConstants
    {
        /// <summary>
        /// Numeric market identifier registered with <see cref="QuantConnect.Market"/>.
        /// Built-in markets occupy 0..42; 43..999 are free for custom markets.
        /// </summary>
        private const int FinamMarketIdentifier = 100;

        /// <summary>
        /// Registers the custom <c>finam</c> market so <c>Symbol.Create(..., Market)</c> resolves at
        /// runtime. Triggered on first access to <see cref="Market"/> (a static field, unlike a const,
        /// runs the type initializer).
        /// </summary>
        static FinamConstants()
        {
            QuantConnect.Market.Add(Market, FinamMarketIdentifier);
        }

        /// <summary>
        /// Default base URL for the Finam Trade API REST gateway (gRPC-Gateway endpoint).
        /// </summary>
        public const string DefaultRestEndpoint = "https://api.finam.ru";

        /// <summary>
        /// Default base URL for the Finam Trade API gRPC endpoint.
        /// </summary>
        public const string DefaultGrpcEndpoint = "https://api.finam.ru:443";

        /// <summary>
        /// Default Finam Trade API WebSocket endpoint (AsyncAPI <c>tradingInfo</c> channel, path <c>/ws</c>).
        /// </summary>
        public const string DefaultWsEndpoint = "wss://api.finam.ru/ws";

        /// <summary>
        /// Configuration key used by <c>config.json</c> for the API secret token.
        /// </summary>
        public const string ConfigSecretToken = "finam-secret-token";

        /// <summary>
        /// Configuration key used by <c>config.json</c> for the active account id.
        /// </summary>
        public const string ConfigAccountId = "finam-account-id";

        /// <summary>
        /// Configuration key used by <c>config.json</c> for the API base URL override.
        /// </summary>
        public const string ConfigApiUrl = "finam-api-url";

        /// <summary>
        /// Configuration key used by <c>config.json</c> for the WebSocket URL override.
        /// </summary>
        public const string ConfigWsUrl = "finam-ws-url";

        /// <summary>
        /// How long live WebSocket data for a symbol is considered fresh before the
        /// REST <c>LastQuote</c> fallback poll takes over for that symbol.
        /// </summary>
        public static readonly System.TimeSpan WebSocketStaleness = System.TimeSpan.FromSeconds(10);

        /// <summary>
        /// Configuration key used by <c>config.json</c> for the account category (cash/margin).
        /// </summary>
        public const string ConfigAccountType = "finam-account-type";

        /// <summary>
        /// Symbol separator used by Finam: <c>TICKER@MIC</c> (e.g. <c>SBER@MISX</c>).
        /// </summary>
        public const char SymbolSeparator = '@';

        /// <summary>
        /// Custom <c>QuantConnect.Market</c> name under which Finam instruments are registered.
        /// A static field (not a const) so that reading it runs the type initializer, which calls
        /// <see cref="QuantConnect.Market.Add"/>.
        /// </summary>
        public static readonly string Market = "finam";

        /// <summary>
        /// Default Finam quote level when <see cref="QuoteRequestMaxRetries"/> is reached.
        /// </summary>
        public const int QuoteRequestMaxRetries = 3;
    }
}
