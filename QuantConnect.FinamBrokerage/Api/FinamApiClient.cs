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
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using QuantConnect.Logging;

namespace QuantConnect.Brokerages.Finam.Api
{
    /// <summary>
    /// Thin async HTTP wrapper around the Finam Trade API gRPC-Gateway REST endpoint.
    /// Handles JWT session lifecycle (acquire/refresh) and applies the <c>Authorization</c> header
    /// to every authenticated call.
    /// </summary>
    /// <remarks>
    /// JWT lifetime is short (typically 15 min). To avoid an explicit refresh loop the client
    /// re-authenticates lazily when within <see cref="TokenRefreshSkew"/> of expiration. Errors
    /// returned by the gateway follow the <see cref="FinamError"/> envelope and are surfaced
    /// as <see cref="FinamApiException"/>.
    /// </remarks>
    public sealed class FinamApiClient : IDisposable
    {
        private static readonly TimeSpan TokenRefreshSkew = TimeSpan.FromMinutes(2);
        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            NullValueHandling = NullValueHandling.Ignore,
            DateFormatHandling = DateFormatHandling.IsoDateFormat
        };

        private readonly HttpClient _http;
        private readonly string _secret;
        private readonly SemaphoreSlim _authLock = new(1, 1);

        private string _jwt;
        private DateTime _jwtExpiresAt = DateTime.MinValue;

        public FinamApiClient(string baseUrl, string secret)
        {
            if (string.IsNullOrEmpty(baseUrl)) throw new ArgumentException("baseUrl required", nameof(baseUrl));
            if (string.IsNullOrEmpty(secret)) throw new ArgumentException("secret required", nameof(secret));

            _secret = secret;
            _http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        public string CurrentJwt => _jwt;

        public async Task<string> AuthenticateAsync(CancellationToken ct = default)
        {
            await _authLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var response = await PostAsync<AuthResponse>("v1/sessions", new AuthRequest { Secret = _secret }, authenticated: false, ct).ConfigureAwait(false);
                _jwt = response.Token;
                var details = await PostAsync<TokenDetailsResponse>("v1/sessions/details", new TokenDetailsRequest { Token = _jwt }, authenticated: false, ct).ConfigureAwait(false);
                _jwtExpiresAt = details?.ExpiresAt ?? DateTime.UtcNow.AddMinutes(10);
                Log.Trace($"FinamApiClient: authenticated, jwt expires at {_jwtExpiresAt:O}");
                return _jwt;
            }
            finally
            {
                _authLock.Release();
            }
        }

        private async Task EnsureAuthenticatedAsync(CancellationToken ct)
        {
            if (string.IsNullOrEmpty(_jwt) || DateTime.UtcNow >= _jwtExpiresAt - TokenRefreshSkew)
            {
                await AuthenticateAsync(ct).ConfigureAwait(false);
            }
        }

        public Task<GetAccountResponse> GetAccountAsync(string accountId, CancellationToken ct = default)
            => GetAsync<GetAccountResponse>($"v1/accounts/{Uri.EscapeDataString(accountId)}", ct);

        public Task<OrdersResponse> GetOrdersAsync(string accountId, CancellationToken ct = default)
            => GetAsync<OrdersResponse>($"v1/accounts/{Uri.EscapeDataString(accountId)}/orders", ct);

        public Task<OrderState> GetOrderAsync(string accountId, string orderId, CancellationToken ct = default)
            => GetAsync<OrderState>($"v1/accounts/{Uri.EscapeDataString(accountId)}/orders/{Uri.EscapeDataString(orderId)}", ct);

        public Task<OrderState> PlaceOrderAsync(string accountId, FinamOrder order, CancellationToken ct = default)
            => PostAsync<OrderState>($"v1/accounts/{Uri.EscapeDataString(accountId)}/orders", order, authenticated: true, ct);

        public Task<OrderState> CancelOrderAsync(string accountId, string orderId, CancellationToken ct = default)
            => DeleteAsync<OrderState>($"v1/accounts/{Uri.EscapeDataString(accountId)}/orders/{Uri.EscapeDataString(orderId)}", ct);

        public Task<GetAssetResponse> GetAssetAsync(string symbol, string accountId = null, CancellationToken ct = default)
        {
            var url = $"v1/assets/{Uri.EscapeDataString(symbol)}";
            if (!string.IsNullOrEmpty(accountId)) url += "?account_id=" + Uri.EscapeDataString(accountId);
            return GetAsync<GetAssetResponse>(url, ct);
        }

        public Task<QuoteResponse> GetLatestQuoteAsync(string symbol, CancellationToken ct = default)
            => GetAsync<QuoteResponse>($"v1/instruments/{Uri.EscapeDataString(symbol)}/quotes/latest", ct);

        public Task<BarsResponse> GetBarsAsync(string symbol, string timeframe, DateTime startUtc, DateTime endUtc, CancellationToken ct = default)
        {
            var url = $"v1/instruments/{Uri.EscapeDataString(symbol)}/bars" +
                      $"?timeframe={timeframe}" +
                      $"&interval.start_time={Uri.EscapeDataString(startUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture))}" +
                      $"&interval.end_time={Uri.EscapeDataString(endUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture))}";
            return GetAsync<BarsResponse>(url, ct);
        }

        private async Task<T> GetAsync<T>(string path, CancellationToken ct)
        {
            await EnsureAuthenticatedAsync(ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.TryAddWithoutValidation("Authorization", _jwt);
            return await SendAsync<T>(request, ct).ConfigureAwait(false);
        }

        private async Task<T> PostAsync<T>(string path, object body, bool authenticated, CancellationToken ct)
        {
            if (authenticated) await EnsureAuthenticatedAsync(ct).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent(JsonConvert.SerializeObject(body, JsonSettings), Encoding.UTF8, "application/json")
            };
            if (authenticated) request.Headers.TryAddWithoutValidation("Authorization", _jwt);
            return await SendAsync<T>(request, ct).ConfigureAwait(false);
        }

        private async Task<T> DeleteAsync<T>(string path, CancellationToken ct)
        {
            await EnsureAuthenticatedAsync(ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Delete, path);
            request.Headers.TryAddWithoutValidation("Authorization", _jwt);
            return await SendAsync<T>(request, ct).ConfigureAwait(false);
        }

        private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken ct)
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                FinamError err = null;
                try { err = JsonConvert.DeserializeObject<FinamError>(content); } catch { /* swallow */ }
                throw new FinamApiException((int)response.StatusCode, err?.Message ?? content);
            }
            return string.IsNullOrEmpty(content)
                ? default
                : JsonConvert.DeserializeObject<T>(content, JsonSettings);
        }

        public void Dispose()
        {
            _http.Dispose();
            _authLock.Dispose();
        }
    }

    public sealed class FinamApiException : Exception
    {
        public int StatusCode { get; }
        public FinamApiException(int statusCode, string message) : base($"Finam API error {statusCode}: {message}")
        {
            StatusCode = statusCode;
        }
    }
}
