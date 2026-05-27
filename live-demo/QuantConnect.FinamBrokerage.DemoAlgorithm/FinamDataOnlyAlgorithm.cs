/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Licensed under the Apache License, Version 2.0.
*/

using QuantConnect.Algorithm;
using QuantConnect.Data;

namespace QuantConnect.Algorithm.CSharp
{
    /// <summary>
    /// Data-only live smoke algorithm for the Finam brokerage: subscribes to a couple of MOEX
    /// equities and logs incoming bars/ticks and account state. It places NO orders — it exists to
    /// validate the full live wiring (factory discovery, portfolio setup from the brokerage, and the
    /// live data feed) end to end without trading.
    /// </summary>
    public class FinamDataOnlyAlgorithm : QCAlgorithm
    {
        private bool _loggedHoldings;

        public override void Initialize()
        {
            SetTimeZone(TimeZones.Moscow);

            // In live mode cash/holdings are synced from the brokerage; this is just a fallback.
            SetCash(0);

            // Avoid the default SPY (USD) benchmark, which would need a USD/RUB conversion on a RUB account.
            SetBenchmark(_ => 0m);

            AddEquity("SBER", Resolution.Minute, market: "finam");
            AddEquity("GAZP", Resolution.Minute, market: "finam");

            Debug("FinamDataOnlyAlgorithm initialized (data-only, no trading).");
        }

        public override void OnData(Slice slice)
        {
            if (!_loggedHoldings)
            {
                _loggedHoldings = true;
                Log($"Portfolio: cash={Portfolio.Cash} {Portfolio.CashBook.AccountCurrency}, totalValue={Portfolio.TotalPortfolioValue}");
                foreach (var holding in Portfolio.Values)
                {
                    Log($"Holding: {holding.Symbol.Value} qty={holding.Quantity} avg={holding.AveragePrice} price={holding.Price}");
                }
            }

            foreach (var bar in slice.Bars.Values)
            {
                Debug($"BAR {bar.EndTime:HH:mm:ss} {bar.Symbol.Value} O={bar.Open} H={bar.High} L={bar.Low} C={bar.Close} V={bar.Volume}");
            }
        }
    }
}
