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
        /// Default base URL for the Finam Trade API REST gateway (gRPC-Gateway endpoint).
        /// </summary>
        public const string DefaultRestEndpoint = "https://api.finam.ru";

        /// <summary>
        /// Default base URL for the Finam Trade API gRPC endpoint.
        /// </summary>
        public const string DefaultGrpcEndpoint = "https://api.finam.ru:443";

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
        /// Configuration key used by <c>config.json</c> for the account category (cash/margin).
        /// </summary>
        public const string ConfigAccountType = "finam-account-type";

        /// <summary>
        /// Symbol separator used by Finam: <c>TICKER@MIC</c> (e.g. <c>SBER@MISX</c>).
        /// </summary>
        public const char SymbolSeparator = '@';

        /// <summary>
        /// Default <c>QuantConnect.Market</c> identifier under which Finam instruments are registered.
        /// </summary>
        public const string Market = "finam";

        /// <summary>
        /// Default Finam quote level when <see cref="QuoteRequestMaxRetries"/> is reached.
        /// </summary>
        public const int QuoteRequestMaxRetries = 3;
    }
}
