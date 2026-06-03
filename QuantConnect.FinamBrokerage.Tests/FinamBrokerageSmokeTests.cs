/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Licensed under the Apache License, Version 2.0.
*/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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

            // /v1/assets/{symbol} requires the account_id query parameter (the live API 400s without it).
            var accountId = Config.Get(FinamConstants.ConfigAccountId);
            var asset = await api.GetAssetAsync(TestSymbol, accountId);
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

        [Test]
        public async Task StreamsQuotesOverWebSocket()
        {
            using var api = CreateClient(out _);
            var wsUrl = Config.Get(FinamConstants.ConfigWsUrl, FinamConstants.DefaultWsEndpoint);

            var envelopes = new ConcurrentQueue<WsEnvelope>();
            var dataReceived = new ManualResetEventSlim(false);

            using var ws = new FinamWebSocketClient(wsUrl, api.GetValidJwtAsync);
            ws.ConnectionError += msg => Log.Trace($"Smoke.WS: connection error: {msg}");
            ws.EnvelopeReceived += envelope =>
            {
                envelopes.Enqueue(envelope);
                if (envelope.IsEvent) Log.Trace($"Smoke.WS: EVENT {envelope.EventInfo?.Event} {envelope.EventInfo?.Reason}");
                if (envelope.IsError) Log.Trace($"Smoke.WS: ERROR {envelope.ErrorInfo?.Type} {envelope.ErrorInfo?.Message}");
                if (envelope.IsData) dataReceived.Set();
            };

            using var cts = new CancellationTokenSource();
            ws.Start(cts.Token);
            await ws.SubscribeQuotesAsync(TestSymbol);
            await ws.SubscribeInstrumentTradesAsync(TestSymbol);

            // Wait for the first DATA frame (quotes/trades), up to 25s.
            var gotData = dataReceived.Wait(TimeSpan.FromSeconds(25));
            cts.Cancel();

            Log.Trace($"Smoke.WS: received {envelopes.Count} envelopes; IsOpen(before cancel)={ws.IsOpen}");
            Assert.That(envelopes.Count, Is.GreaterThan(0), "Expected at least the handshake/event frames — connection or auth failed");

            // Inspect a quotes DATA frame if present and verify the bare-string decimals parse.
            var quoteEnvelope = envelopes.FirstOrDefault(e => e.IsData && e.SubscriptionType == WsSubscriptionType.Quotes);
            if (quoteEnvelope != null)
            {
                var payload = quoteEnvelope.PayloadAs<WsQuotePayload>();
                var quote = payload?.Quote?.FirstOrDefault();
                Log.Trace($"Smoke.WS: quote {quote?.Symbol} bid={quote?.Bid?.AsDecimal()} ask={quote?.Ask?.AsDecimal()} last={quote?.Last?.AsDecimal()}");
                Assert.That(quote, Is.Not.Null, "QUOTES DATA frame should contain at least one quote");
                Assert.That(quote.Ask?.AsDecimal(), Is.GreaterThan(0m), "Quote ask should parse to a positive decimal");
            }

            Assert.That(gotData, Is.True,
                "No DATA frame within 25s. Connection/auth worked (events received), but no market data — likely outside MOEX trading hours.");
        }

        /// <summary>
        /// Live order lifecycle: place a far-below-market BUY limit (won't execute), confirm it is open,
        /// then cancel it. Validates the REST order path + request-body serialization and reveals the
        /// account ORDERS stream format. Double-gated: requires a trading token AND
        /// QC_FINAM_ALLOW_TRADING=1, so it never trades by accident. Prefer a demo account.
        /// </summary>
        [Test]
        public async Task PlacesAndCancelsLimitOrder()
        {
            if (Config.Get("finam-allow-trading") != "1")
            {
                Assert.Ignore("Live order test disabled. Set QC_FINAM_ALLOW_TRADING=1 (and a trading token) to enable.");
            }

            using var api = CreateClient(out _);
            var accountId = Config.Get(FinamConstants.ConfigAccountId);
            Assert.That(accountId, Is.Not.Null.And.Not.Empty, "Need QC_FINAM_ACCOUNT_ID for the order test");

            // Watch the account ORDERS stream and log the raw payload (confirms the account-stream wire format).
            var wsUrl = Config.Get(FinamConstants.ConfigWsUrl, FinamConstants.DefaultWsEndpoint);
            using var ws = new FinamWebSocketClient(wsUrl, api.GetValidJwtAsync);
            var orderFrames = new ConcurrentQueue<string>();
            ws.EnvelopeReceived += e =>
            {
                if (e.IsData && e.SubscriptionType == WsSubscriptionType.Orders)
                {
                    orderFrames.Enqueue(e.Payload);
                    Log.Trace($"Smoke.Order: ORDERS frame raw payload: {e.Payload}");
                }
            };
            using var cts = new CancellationTokenSource();
            ws.Start(cts.Token);
            await ws.SubscribeOrdersAsync(accountId);

            // BUY limit ~5% below last — inside the exchange price band, but won't fill in the test window.
            var quote = await api.GetLatestQuoteAsync(TestSymbol);
            var last = quote?.Quote?.Last?.AsDecimal() ?? 0m;
            Assert.That(last, Is.GreaterThan(0m), "Need a last price to compute a safe limit");
            var farLimit = decimal.Round(last * 0.95m, 2);

            var request = new FinamOrder
            {
                AccountId = accountId,
                Symbol = TestSymbol,
                Quantity = FinamDecimal.From(1m),
                Side = "SIDE_BUY",
                Type = "ORDER_TYPE_LIMIT",
                TimeInForce = "TIME_IN_FORCE_DAY",
                LimitPrice = FinamDecimal.From(farLimit),
                ClientOrderId = "smk" + DateTime.UtcNow.ToString("HHmmssff")
            };

            OrderState placed = null;
            try
            {
                placed = await api.PlaceOrderAsync(accountId, request);
                Log.Trace($"Smoke.Order: placed orderId={placed?.OrderId} status={placed?.Status} limit={farLimit} (last={last})");
                Assert.That(placed?.OrderId, Is.Not.Null.And.Not.Empty, "PlaceOrder should return an order id");

                var orders = await api.GetOrdersAsync(accountId);
                var found = orders?.Orders?.Any(o => o.OrderId == placed.OrderId) ?? false;
                Log.Trace($"Smoke.Order: open orders count={orders?.Orders?.Count}; contains placed={found}");

                // Give the ORDERS stream a moment to push our new order.
                await Task.Delay(TimeSpan.FromSeconds(4));
                Log.Trace($"Smoke.Order: ORDERS frames seen={orderFrames.Count}");
            }
            finally
            {
                if (!string.IsNullOrEmpty(placed?.OrderId))
                {
                    var cancelled = await api.CancelOrderAsync(accountId, placed.OrderId);
                    Log.Trace($"Smoke.Order: cancelled orderId={placed.OrderId} status={cancelled?.Status}");
                }
                cts.Cancel();
            }
        }

        /// <summary>
        /// Diagnostic: determines whether the live WS <c>INSTRUMENT_TRADES</c> stream actually delivers a
        /// fresh trade tape, or only a stale last-trade snapshot (resent as a heartbeat). For a MOEX spot
        /// symbol (<c>MISX</c>) and a FORTS futures symbol (<c>RTSX</c>) it subscribes to BOTH
        /// <c>QUOTES</c> and <c>INSTRUMENT_TRADES</c>, and per symbol tracks:
        /// <list type="bullet">
        ///   <item>trade frames, distinct <c>tradeId</c>s, frames carrying a <em>fresh</em> trade
        ///         (timestamp within the last 5 min), and the newest trade timestamp seen;</item>
        ///   <item>quote frames + freshness — proving the market is actually live right now;</item>
        /// </list>
        /// It also probes REST <c>LatestTrades</c>/<c>LatestQuote</c> before and after the window, so we
        /// can directly compare what WS streams vs what REST returns. The first few raw INSTRUMENT_TRADES
        /// frames are dumped verbatim.
        ///
        /// Reading the result:
        /// <list type="bullet">
        ///   <item><b>WS tape is live</b> for a symbol iff distinctTradeIds &gt; 1 and tradeFreshFrames &gt; 0.</item>
        ///   <item><b>WS is snapshot-only (broken)</b> iff quotes are fresh (market live) but distinctTradeIds == 1
        ///         and the only trade is older than the 5-min frontier — i.e. the production
        ///         <c>OnInstrumentTrades</c> frontier would drop it and emit nothing.</item>
        /// </list>
        /// MUST run during MOEX trading hours. The futures contrast needs a live contract via
        /// <c>QC_FINAM_SMOKE_FUTURES_SYMBOL</c> (e.g. RIM6@RTSX); a stale contract yields an ERROR.
        /// </summary>
        [Test]
        public async Task ContrastsSpotVsFuturesInstrumentTrades()
        {
            using var api = CreateClient(out _);
            var wsUrl = Config.Get(FinamConstants.ConfigWsUrl, FinamConstants.DefaultWsEndpoint);
            var seconds = Config.GetInt("finam-smoke-seconds", 120);
            var freshWindow = TimeSpan.FromMinutes(5);

            var spot = Config.Get(FinamConstants.ConfigSmokeSpotSymbol, TestSymbol);
            var futures = Config.Get(FinamConstants.ConfigSmokeFuturesSymbol, string.Empty);
            var symbols = string.IsNullOrEmpty(futures) ? new[] { spot } : new[] { spot, futures };
            if (string.IsNullOrEmpty(futures))
            {
                Log.Trace("Smoke.Contrast: NO futures symbol set — futures contrast skipped. " +
                          "Set QC_FINAM_SMOKE_FUTURES_SYMBOL=<live RTSX contract> (e.g. RIM6@RTSX) to compare.");
            }

            static string Ticker(string s) => s.Split(FinamConstants.SymbolSeparator)[0];

            // REST probe: shows what LatestTrades/LatestQuote return (compare WS-stream vs REST tape).
            async Task ProbeRestAsync(string label)
            {
                foreach (var symbol in symbols)
                {
                    try
                    {
                        var q = await api.GetLatestQuoteAsync(symbol);
                        var fq = q?.Quote;
                        Log.Trace($"Smoke.Contrast REST[{label}] {symbol} quote ts={fq?.Timestamp:o} last={fq?.Last?.AsDecimal()} bid={fq?.Bid?.AsDecimal()} ask={fq?.Ask?.AsDecimal()}");
                    }
                    catch (Exception ex) { Log.Trace($"Smoke.Contrast REST[{label}] {symbol} quote error: {ex.Message}"); }
                    try
                    {
                        var t = await api.GetLatestTradesAsync(symbol);
                        var latest = t?.Trades?.OrderByDescending(x => x.Timestamp).FirstOrDefault();
                        Log.Trace($"Smoke.Contrast REST[{label}] {symbol} trades count={t?.Trades?.Count} latestTradeId={latest?.TradeId} ts={latest?.Timestamp:o} price={latest?.Price?.AsDecimal()}");
                    }
                    catch (Exception ex) { Log.Trace($"Smoke.Contrast REST[{label}] {symbol} trades error: {ex.Message}"); }
                }
            }

            var tradeFrames = new ConcurrentDictionary<string, int>();
            var tradeFreshFrames = new ConcurrentDictionary<string, int>();
            var maxTradeTs = new ConcurrentDictionary<string, DateTime>();
            var tradeIdSeen = new ConcurrentDictionary<string, byte>();   // key "ticker|tradeId"
            var quoteFrames = new ConcurrentDictionary<string, int>();
            var quoteFreshFrames = new ConcurrentDictionary<string, int>();
            var maxQuoteTs = new ConcurrentDictionary<string, DateTime>();
            var dumped = new ConcurrentDictionary<string, int>();
            const int dumpLimit = 5;

            using var ws = new FinamWebSocketClient(wsUrl, api.GetValidJwtAsync);
            ws.ConnectionError += msg => Log.Trace($"Smoke.Contrast: connection error: {msg}");
            ws.EnvelopeReceived += envelope =>
            {
                if (envelope.IsError)
                {
                    Log.Trace($"Smoke.Contrast: ERROR {envelope.ErrorInfo?.Type} code={envelope.ErrorInfo?.Code} {envelope.ErrorInfo?.Message}");
                    return;
                }
                if (envelope.IsEvent)
                {
                    Log.Trace($"Smoke.Contrast: EVENT {envelope.EventInfo?.Event} {envelope.EventInfo?.Reason}");
                    return;
                }
                if (!envelope.IsData) return;

                var fresh = DateTime.UtcNow - freshWindow;

                if (envelope.SubscriptionType == WsSubscriptionType.InstrumentTrades)
                {
                    WsTradesPayload payload;
                    try { payload = envelope.PayloadAs<WsTradesPayload>(); }
                    catch (Exception ex) { Log.Trace($"Smoke.Contrast: trades parse error: {ex.Message}; raw={envelope.Payload}"); return; }
                    if (payload?.Trades == null) return;

                    var key = Ticker(payload.Symbol ?? envelope.SubscriptionKey ?? "?");
                    tradeFrames.AddOrUpdate(key, 1, (_, n) => n + 1);

                    var anyFresh = false;
                    foreach (var trade in payload.Trades)
                    {
                        if (!string.IsNullOrEmpty(trade.TradeId)) tradeIdSeen.TryAdd($"{key}|{trade.TradeId}", 0);
                        var ts = trade.Timestamp.ToUniversalTime();
                        maxTradeTs.AddOrUpdate(key, ts, (_, prev) => ts > prev ? ts : prev);
                        if (ts >= fresh) anyFresh = true;
                    }
                    if (anyFresh) tradeFreshFrames.AddOrUpdate(key, 1, (_, n) => n + 1);

                    var shown = dumped.AddOrUpdate(key, 1, (_, n) => n + 1);
                    if (shown <= dumpLimit)
                    {
                        Log.Trace($"Smoke.Contrast: {key} INSTRUMENT_TRADES raw frame #{shown}: {envelope.Payload}");
                    }
                }
                else if (envelope.SubscriptionType == WsSubscriptionType.Quotes)
                {
                    WsQuotePayload payload;
                    try { payload = envelope.PayloadAs<WsQuotePayload>(); }
                    catch { return; }
                    if (payload?.Quote == null) return;

                    foreach (var quote in payload.Quote)
                    {
                        var key = Ticker(quote.Symbol ?? envelope.SubscriptionKey ?? "?");
                        quoteFrames.AddOrUpdate(key, 1, (_, n) => n + 1);
                        var ts = quote.Timestamp.ToUniversalTime();
                        maxQuoteTs.AddOrUpdate(key, ts, (_, prev) => ts > prev ? ts : prev);
                        if (ts >= fresh) quoteFreshFrames.AddOrUpdate(key, 1, (_, n) => n + 1);
                    }
                }
            };

            await ProbeRestAsync("before");

            using var cts = new CancellationTokenSource();
            ws.Start(cts.Token);
            foreach (var symbol in symbols)
            {
                await ws.SubscribeQuotesAsync(symbol);
                await ws.SubscribeInstrumentTradesAsync(symbol);
            }

            Log.Trace($"Smoke.Contrast: subscribed QUOTES+INSTRUMENT_TRADES for {string.Join(", ", symbols)}; observing {seconds}s...");
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            cts.Cancel();

            await ProbeRestAsync("after");

            var totalFrames = 0;
            foreach (var symbol in symbols)
            {
                var key = Ticker(symbol);
                var tf = tradeFrames.GetValueOrDefault(key);
                var tff = tradeFreshFrames.GetValueOrDefault(key);
                var distinct = tradeIdSeen.Keys.Count(k => k.StartsWith(key + "|", StringComparison.OrdinalIgnoreCase));
                var qf = quoteFrames.GetValueOrDefault(key);
                var qff = quoteFreshFrames.GetValueOrDefault(key);
                totalFrames += tf + qf;
                Log.Trace($"Smoke.Contrast SUMMARY {symbol}: tradeFrames={tf} distinctTradeIds={distinct} tradeFreshFrames={tff} " +
                          $"maxTradeTs={(maxTradeTs.TryGetValue(key, out var mt) ? mt.ToString("o") : "-")} | " +
                          $"quoteFrames={qf} quoteFreshFrames={qff} maxQuoteTs={(maxQuoteTs.TryGetValue(key, out var mq) ? mq.ToString("o") : "-")}");
            }
            Log.Trace("Smoke.Contrast: WS tape LIVE iff distinctTradeIds>1 && tradeFreshFrames>0. " +
                      "WS SNAPSHOT-ONLY (broken) iff quoteFreshFrames>0 (market live) but distinctTradeIds==1 && tradeFreshFrames==0.");

            if (totalFrames == 0)
            {
                Assert.Ignore("No QUOTES/INSTRUMENT_TRADES DATA frames within the window — likely outside MOEX trading hours.");
            }
            Assert.That(totalFrames, Is.GreaterThan(0), "Expected at least one DATA frame (channel alive)");
        }
    }
}
