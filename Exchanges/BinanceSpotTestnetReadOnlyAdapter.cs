using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CryptoAnalyzer.Exchanges;

/// <summary>
/// Read-only Binance Spot Testnet adapter. Its base URL is permanently pinned to
/// testnet.binance.vision and it never submits, cancels or modifies an order.
/// </summary>
public sealed class BinanceSpotTestnetReadOnlyAdapter : IExchangeAdapter
{
    private readonly HttpClient _httpClient;
    private static readonly Uri TestnetBaseAddress = new("https://testnet.binance.vision/api/");

    public string ExchangeCode => "BINANCE_TESTNET";
    public bool CanPlaceOrders => false;
    public ExchangeCapabilities Capabilities { get; } =
        new("BINANCE_TESTNET", SupportsSpot: true, SupportsMargin: false, SupportsFutures: false,
            SupportsMarketOrders: true, SupportsLimitOrders: true);

    public BinanceSpotTestnetReadOnlyAdapter(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = TestnetBaseAddress;
        else if (!string.Equals(_httpClient.BaseAddress.AbsoluteUri.TrimEnd('/'),
                     TestnetBaseAddress.AbsoluteUri.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Binance adapter is pinned to https://testnet.binance.vision/api/ only.", nameof(httpClient));
    }

    public async Task<bool> ValidateCredentialsAsync(ExchangeCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _ = await GetAccountSnapshotAsync(credentials, cancellationToken);
            return true;
        }
        catch (HttpRequestException) { return false; }
        catch (JsonException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (CryptographicException) { return false; }
        catch (InvalidDataException) { return false; }
    }

    public async Task<ExchangeAccountSnapshot> GetAccountSnapshotAsync(ExchangeCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (string.IsNullOrWhiteSpace(credentials.ApiKey) || string.IsNullOrWhiteSpace(credentials.ApiSecret))
            throw new UnauthorizedAccessException("Binance Testnet API key and secret are required.");

        using var timeResponse = await _httpClient.GetAsync("v3/time", cancellationToken);
        timeResponse.EnsureSuccessStatusCode();
        using var timeStream = await timeResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var timeDocument = await JsonDocument.ParseAsync(timeStream, cancellationToken: cancellationToken);
        if (!timeDocument.RootElement.TryGetProperty("serverTime", out var serverTimeElement) ||
            !serverTimeElement.TryGetInt64(out long serverTime))
            throw new InvalidDataException("Binance Testnet time response is invalid.");

        string query = $"timestamp={serverTime.ToString(CultureInfo.InvariantCulture)}&recvWindow=5000";
        string signature = Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(credentials.ApiSecret), Encoding.UTF8.GetBytes(query))).ToLowerInvariant();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"v3/account?{query}&signature={signature}");
        request.Headers.Add("X-MBX-APIKEY", credentials.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _httpClient.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException("Binance Testnet rejected the API credentials or permissions.");
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("balances", out var balanceArray) ||
            balanceArray.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Binance Testnet account response is invalid.");

        var balances = new List<ExchangeBalance>();
        foreach (var balance in balanceArray.EnumerateArray())
        {
            string asset = GetString(balance, "asset");
            if (string.IsNullOrWhiteSpace(asset)) continue;
            decimal free = GetDecimal(balance, "free");
            decimal locked = GetDecimal(balance, "locked");
            balances.Add(new ExchangeBalance(asset.ToUpperInvariant(), free + locked, free, locked));
        }

        return new ExchangeAccountSnapshot(ExchangeCode, true, DateTime.UtcNow, balances);
    }

    public Task<ExchangeOrderResult> PlaceOrderAsync(ExchangeCredentials credentials,
        ExchangeOrderRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(ExchangeOrderResult.Rejected(
            "Binance Testnet order placement is not implemented in this read-only adapter. No request was sent."));

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    private static decimal GetDecimal(JsonElement element, string name)
    {
        string value = GetString(element, name);
        return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var result)
            ? result : 0m;
    }
}
