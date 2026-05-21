/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Licensed under the Apache License, Version 2.0.
*/

using System;
using QuantConnect;
using QuantConnect.Brokerages.Finam;
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.ToolBox.FinamDownloader;

namespace QuantConnect.ToolBox.FinamDownloader
{
    /// <summary>
    /// CLI entry point that downloads historical Finam bars and serialises them in LEAN's CSV format.
    /// Usage:
    ///   QuantConnect.ToolBox.FinamDownloader.exe --tickers=SBER,GAZP --resolution=minute --from=2025-01-01 --to=2025-02-01
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            var tickers = GetArg(args, "--tickers")?.Split(',', StringSplitOptions.RemoveEmptyEntries);
            var resolutionStr = GetArg(args, "--resolution") ?? "daily";
            var fromStr = GetArg(args, "--from") ?? DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");
            var toStr = GetArg(args, "--to") ?? DateTime.UtcNow.ToString("yyyy-MM-dd");

            if (tickers == null || tickers.Length == 0)
            {
                Console.Error.WriteLine("usage: --tickers=SBER,GAZP [--resolution=minute|hour|daily] [--from=YYYY-MM-DD] [--to=YYYY-MM-DD]");
                return 1;
            }

            var resolution = Enum.Parse<Resolution>(resolutionStr, ignoreCase: true);
            var from = DateTime.SpecifyKind(DateTime.Parse(fromStr), DateTimeKind.Utc);
            var to   = DateTime.SpecifyKind(DateTime.Parse(toStr),   DateTimeKind.Utc);

            var secret = Config.Get(FinamConstants.ConfigSecretToken);
            var apiUrl = Config.Get(FinamConstants.ConfigApiUrl, FinamConstants.DefaultRestEndpoint);

            using var downloader = new FinamDataDownloader(apiUrl, secret);

            foreach (var ticker in tickers)
            {
                var symbol = Symbol.Create(ticker.Trim(), SecurityType.Equity, FinamConstants.Market);
                var parameters = new DataDownloaderGetParameters(symbol, resolution, from, to);
                var count = 0;
                foreach (var bar in downloader.Get(parameters))
                {
                    Console.WriteLine($"{bar.Time:o},{bar.Symbol.Value},{bar.Price}");
                    count++;
                }
                Console.Error.WriteLine($"Downloaded {count} bars for {ticker}");
            }
            return 0;
        }

        private static string GetArg(string[] args, string name)
        {
            foreach (var arg in args)
            {
                if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                    return arg.Substring(name.Length + 1);
            }
            return null;
        }
    }
}
