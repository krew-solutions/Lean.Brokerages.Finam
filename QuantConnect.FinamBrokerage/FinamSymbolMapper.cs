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

namespace QuantConnect.Brokerages.Finam
{
    /// <summary>
    /// Maps between LEAN <see cref="Symbol"/> instances and Finam <c>ticker@mic</c> identifiers.
    /// </summary>
    /// <remarks>
    /// Finam exposes symbols in a <c>TICKER@MIC</c> form (e.g. <c>SBER@MISX</c>, <c>AAPL@XNAS</c>).
    /// LEAN's native form is the bare ticker plus a <see cref="QuantConnect.Market"/> identifier.
    /// We cache the round-trip mapping per <see cref="SecurityType"/> to avoid repeatedly
    /// re-parsing strings on hot order paths.
    /// </remarks>
    public class FinamSymbolMapper : ISymbolMapper
    {
        private readonly ConcurrentDictionary<Symbol, string> _leanToBrokerage = new();
        private readonly ConcurrentDictionary<string, Symbol> _brokerageToLean = new();

        /// <inheritdoc />
        public string GetBrokerageSymbol(Symbol symbol)
        {
            if (symbol == null) throw new ArgumentNullException(nameof(symbol));

            return _leanToBrokerage.GetOrAdd(symbol, static s =>
            {
                var ticker = s.Value;
                if (ticker.IndexOf(FinamConstants.SymbolSeparator) >= 0)
                {
                    return ticker.ToUpperInvariant();
                }

                var mic = MapMarketToMic(s.ID.Market, s.SecurityType);
                return $"{ticker.ToUpperInvariant()}{FinamConstants.SymbolSeparator}{mic}";
            });
        }

        /// <inheritdoc />
        public Symbol GetLeanSymbol(string brokerageSymbol, SecurityType securityType, string market,
            DateTime expirationDate = default, decimal strike = 0, OptionRight optionRight = 0)
        {
            if (string.IsNullOrWhiteSpace(brokerageSymbol))
                throw new ArgumentException("brokerageSymbol required", nameof(brokerageSymbol));

            return _brokerageToLean.GetOrAdd(brokerageSymbol, s =>
            {
                var parts = s.Split(FinamConstants.SymbolSeparator);
                var ticker = parts[0];
                var resolvedMarket = string.IsNullOrEmpty(market)
                    ? (parts.Length > 1 ? MapMicToMarket(parts[1], securityType) : FinamConstants.Market)
                    : market;

                return securityType switch
                {
                    SecurityType.Option when expirationDate != default
                        => Symbol.CreateOption(ticker, resolvedMarket, OptionStyle.European, optionRight, strike, expirationDate),
                    SecurityType.Future when expirationDate != default
                        => Symbol.CreateFuture(ticker, resolvedMarket, expirationDate),
                    _ => Symbol.Create(ticker, securityType, resolvedMarket)
                };
            });
        }

        /// <summary>
        /// Translates the LEAN <see cref="QuantConnect.Market"/> string into a Finam MIC code.
        /// Default mapping covers MOEX boards (MISX, RTSX) and US exchanges (XNAS, XNYS, XCBO).
        /// </summary>
        public static string MapMarketToMic(string market, SecurityType securityType)
        {
            if (string.IsNullOrEmpty(market)) return "MISX";

            if (string.Equals(market, FinamConstants.Market, StringComparison.OrdinalIgnoreCase))
            {
                return securityType == SecurityType.Future ? "RTSX" : "MISX";
            }

            return market.ToUpperInvariant() switch
            {
                "USA" or "NASDAQ" => "XNAS",
                "NYSE" => "XNYS",
                "CBOE" => "XCBO",
                "CME" => "XCME",
                "MOEX" or "RUSSIA" => "MISX",
                "FORTS" or "FUT" => "RTSX",
                _ => market.ToUpperInvariant()
            };
        }

        /// <summary>
        /// Inverse of <see cref="MapMarketToMic"/>.
        /// </summary>
        public static string MapMicToMarket(string mic, SecurityType securityType)
        {
            if (string.IsNullOrEmpty(mic)) return FinamConstants.Market;
            return mic.ToUpperInvariant() switch
            {
                "MISX" => FinamConstants.Market,
                "RTSX" => FinamConstants.Market,
                "XNAS" => Market.USA,
                "XNYS" => Market.USA,
                "XCBO" => Market.USA,
                "XCME" => Market.CME,
                _ => FinamConstants.Market
            };
        }
    }
}
