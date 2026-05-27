/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Licensed under the Apache License, Version 2.0.
*/

using System;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using QuantConnect.Brokerages.Finam.Api;
using QuantConnect.Configuration;
using QuantConnect.Logging;

namespace QuantConnect.Brokerages.Finam.Tests
{
    /// <summary>
    /// Read-only live smoke test against the real Finam Trade API. Marked <see cref="ExplicitAttribute"/>
    /// so it never runs in the normal suite. Requires real credentials supplied via config / environment:
    ///   QC_FINAM_SECRET_TOKEN  -> finam-secret-token
    ///   QC_FINAM_ACCOUNT_ID    -> finam-account-id   (optional; auto-detected from the token if omitted)
    ///   QC_FINAM_API_URL       -> finam-api-url      (optional; defaults to https://api.finam.ru)
    ///
    /// Run with:
    ///   QC_FINAM_SECRET_TOKEN=... QC_FINAM_ACCOUNT_ID=... \
    ///     dotnet test --filter "FullyQualifiedName~FinamBrokerageSmokeTests" -c Release
    ///
    /// Performs NO trading — only auth and read endpoints.
    /// </summary>
    [TestFixture, Explicit("Live test: needs a real Finam API token; hits the production API.")]
    public class FinamBrokerageSmokeTests
    {
        private const string TestSymbol = "SBER@MISX";

        private static FinamApiClient CreateClient(out string secret)
        {
            secret = Config.Get(FinamConstants.ConfigSecretToken);
            if (string.IsNullOrEmpty(secret))
            {
                Assert.Ignore("No finam-secret-token configured (set QC_FINAM_SECRET_TOKEN). Skipping live smoke test.");
            }
            var apiUrl = Config.Get(FinamConstants.ConfigApiUrl, FinamConstants.DefaultRestEndpoint);
            return new FinamApiClient(apiUrl, secret);
        }

        [Test]
        public async Task Authenticates()
        {
            using var api = CreateClient(out _);
            var jwt = await api.AuthenticateAsync();
            Assert.That(jwt, Is.Not.Null.And.Not.Empty, "Auth should return a non-empty JWT");
            Log.Trace($"Smoke.Authenticates: got JWT of length {jwt.Length}");
        }

        [Test]
        public async Task ReadsAccount()
        {
            using var api = CreateClient(out _);
            var accountId = Config.Get(FinamConstants.ConfigAccountId);
            if (string.IsNullOrEmpty(accountId))
            {
                // Discover the first account from the token details.
                var jwt = await api.AuthenticateAsync();
                var details = await api.GetTokenDetailsAsync(jwt);
                accountId = details?.AccountIds?.FirstOrDefault();
                Log.Trace($"Smoke.ReadsAccount: token exposes accounts [{string.Join(", ", details?.AccountIds ?? new())}], readonly={details?.ReadOnly}");
            }

            Assert.That(accountId, Is.Not.Null.And.Not.Empty, "Need an account id (set QC_FINAM_ACCOUNT_ID or use a token that lists accounts)");

            var account = await api.GetAccountAsync(accountId);
            Assert.That(account, Is.Not.Null);
            Log.Trace($"Smoke.ReadsAccount: account {account.AccountId} type={account.Type} equity={account.Equity?.AsDecimal()} " +
                      $"positions={account.Positions?.Count ?? 0} cash={string.Join(",", (account.Cash ?? new()).Select(c => $"{c.AsDecimal()} {c.CurrencyCode}"))}");
        }

        [Test]
        public async Task ReadsQuoteAndBars()
        {
            using var api = CreateClient(out _);

            var asset = await api.GetAssetAsync(TestSymbol);
            Log.Trace($"Smoke.ReadsQuoteAndBars: asset {TestSymbol} -> ticker={asset?.Ticker} mic={asset?.Mic} decimals={asset?.Decimals} lot={asset?.LotSize?.AsDecimal()}");

            var quote = await api.GetLatestQuoteAsync(TestSymbol);
            Assert.That(quote?.Quote, Is.Not.Null, "Expected a latest quote");
            Log.Trace($"Smoke.ReadsQuoteAndBars: quote bid={quote.Quote.Bid?.AsDecimal()} ask={quote.Quote.Ask?.AsDecimal()} last={quote.Quote.Last?.AsDecimal()}");

            var end = DateTime.UtcNow;
            var bars = await api.GetBarsAsync(TestSymbol, "TIME_FRAME_M1", end.AddHours(-3), end);
            Assert.That(bars?.Bars, Is.Not.Null, "Expected a bars response");
            Log.Trace($"Smoke.ReadsQuoteAndBars: received {bars.Bars.Count} M1 bars; last close={bars.Bars.LastOrDefault()?.Close?.AsDecimal()}");
            Assert.That(bars.Bars.Count, Is.GreaterThan(0), "Expected at least one M1 bar in the last 3 hours (during/after a trading session)");
        }
    }
}
