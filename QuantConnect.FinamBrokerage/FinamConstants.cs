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
    using System;
    using QuantConnect.Logging;
    using QuantConnect.Securities;

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
        /// Registers the custom <c>finam</c> market and its market hours so the engine can resolve
        /// <c>Symbol.Create(..., Market)</c> and <c>MarketHoursDatabase</c> lookups at runtime.
        /// Triggered on first access to <see cref="Market"/> (a static field, unlike a const, runs the
        /// type initializer); the factory touches it before the algorithm initializes.
        /// </summary>
        static FinamConstants()
        {
            QuantConnect.Market.Add(Market, FinamMarketIdentifier);
            RegisterMarketHours();
        }

        /// <summary>
        /// Registers always-open Moscow-time market hours for the custom market, best-effort.
        /// TODO: replace with real MOEX sessions (main 10:00–18:45 plus morning/evening) once
        /// the schedule is wired from the Finam <c>/v1/assets/{symbol}/schedule</c> endpoint.
        /// </summary>
        private static void RegisterMarketHours()
        {
            try
            {
                var mhdb = MarketHoursDatabase.FromDataFolder();
                var moex = FinamMarketHours.MoexEquity();
                mhdb.SetEntry(Market, null, SecurityType.Equity, moex, TimeZones.Moscow);
                mhdb.SetEntry(Market, null, SecurityType.Index, moex, TimeZones.Moscow);

                // FORTS (futures/options) trades on a different schedule than equities; until that is
                // modelled, keep them always-open so subscriptions/fills are not gated incorrectly.
                mhdb.SetEntryAlwaysOpen(Market, null, SecurityType.Future, TimeZones.Moscow);
                mhdb.SetEntryAlwaysOpen(Market, null, SecurityType.Option, TimeZones.Moscow);
            }
            catch (Exception ex)
            {
                // Needs a configured data folder; harmless to skip in contexts that don't trade (e.g. pure REST).
                Log.Trace($"FinamConstants: could not register market hours ({ex.Message}).");
            }
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
        /// REST fallback poll takes over for that symbol. Default for
        /// <see cref="ConfigMarketDataStalenessSeconds"/>.
        /// </summary>
        public static readonly System.TimeSpan WebSocketStaleness = System.TimeSpan.FromSeconds(10);

        /// <summary>
        /// Live market-data source selector (<c>config.json</c> key <c>finam-marketdata-source</c>):
        /// <list type="bullet">
        ///   <item><c>auto</c> (default) — WS primary, automatically falling back to REST polling per
        ///         symbol when the WS feed delivers no <em>fresh</em> data for
        ///         <see cref="ConfigMarketDataStalenessSeconds"/> seconds;</item>
        ///   <item><c>rest</c> — always poll REST (the WS market-data feed is known snapshot-only);</item>
        ///   <item><c>ws</c> — WS only, no REST fallback (legacy).</item>
        /// </list>
        /// </summary>
        public const string ConfigMarketDataSource = "finam-marketdata-source";
        public const string MarketDataSourceAuto = "auto";
        public const string MarketDataSourceRest = "rest";
        public const string MarketDataSourceWs = "ws";

        /// <summary>REST market-data poll cadence in milliseconds (<c>finam-marketdata-poll-interval</c>).</summary>
        public const string ConfigMarketDataPollInterval = "finam-marketdata-poll-interval";
        public const int DefaultMarketDataPollIntervalMs = 1000;

        /// <summary>
        /// In <c>auto</c> mode, how many seconds without fresh WS data before a symbol falls back to REST
        /// polling (<c>finam-marketdata-staleness-seconds</c>; defaults to <see cref="WebSocketStaleness"/>).
        /// </summary>
        public const string ConfigMarketDataStalenessSeconds = "finam-marketdata-staleness-seconds";

        /// <summary>
        /// Configuration key used by <c>config.json</c> for the account category (cash/margin).
        /// </summary>
        public const string ConfigAccountType = "finam-account-type";

        /// <summary>
        /// Config keys for the <c>ContrastsSpotVsFuturesInstrumentTrades</c> diagnostic smoke test:
        /// the spot (MOEX/<c>MISX</c>) and futures (FORTS/<c>RTSX</c>) symbols whose live
        /// <c>INSTRUMENT_TRADES</c> streams are compared. Override the futures default with a live
        /// near-month contract via <c>QC_FINAM_SMOKE_FUTURES_SYMBOL</c>.
        /// </summary>
        public const string ConfigSmokeSpotSymbol = "finam-smoke-spot-symbol";
        public const string ConfigSmokeFuturesSymbol = "finam-smoke-futures-symbol";

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
