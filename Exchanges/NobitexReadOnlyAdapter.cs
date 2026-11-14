using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CryptoAnalyzer.Exchanges;

/// <summary>
/// Read-only Nobitex adapter. It only sends GET requests to profile and wallet endpoints.
/// No order, transfer, cancel, login or withdrawal endpoint is called by this adapter.
/// </summary>
public sealed class NobitexReadOnlyAdapter : IExchangeAdapter
{
    private readonly HttpClient _httpClient;
    private static readonly Uri DefaultBaseAddress = new("https://apiv2.nobitex.ir/");

    public string ExchangeCode => "NOBITEX";
    public bool CanPlaceOrders => false;
    public ExchangeCapabilities Capabilities { get; } =
        new("NOBITEX", SupportsSpot: true, SupportsMargin: true, SupportsFutures: false,
            SupportsMarketOrders: true, SupportsLimitOrders: true);

    public NobitexReadOnlyAdapter(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _httpClient.BaseAddress ??= DefaultBaseAddress;
        if (_httpClient.BaseAddress.Scheme != Uri.UriSchemeHttps ||
            !_httpClient.BaseAddress.Host.Equals("apiv2.nobitex.ir", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Nobitex credentials may only be sent to the official HTTPS API host.", nameof(httpClient));
    }

    public async Task<bool> ValidateCredentialsAsync(ExchangeCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        var result = await ValidateCredentialsDetailedAsync(credentials, cancellationToken);
        return result.IsValid;
    }

    public async Task<NobitexCredentialValidationResult> ValidateCredentialsDetailedAsync(
        ExchangeCredentials credentials, CancellationToken cancellationToken = default)
    {
        try
        {
            using var document = await GetAuthenticatedJsonAsync("users/profile", credentials, cancellationToken);
            if (HasOkStatus(document.RootElement))
                return new(true, "Nobitex accepted the API key and signature.");

            return new(false, GetApiFailureMessage(document.RootElement));
        }
        catch (HttpRequestException ex)
        {
            return new(false, ex.Message);
        }
        catch (JsonException ex)
        {
            return new(false, $"Nobitex returned invalid JSON: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return new(false, ex.Message);
        }
        catch (TaskCanceledException)
        {
            return new(false, "Connection to Nobitex timed out. Check network access and IP restrictions.");
        }
    }

    public async Task<ExchangeAccountSnapshot> GetAccountSnapshotAsync(ExchangeCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        using var profile = await GetAuthenticatedJsonAsync("users/profile", credentials, cancellationToken);
        if (!HasOkStatus(profile.RootElement))
            throw new UnauthorizedAccessException("Nobitex rejected the stored API key or Ed25519 signature.");

        using var wallets = await GetAuthenticatedJsonAsync("users/wallets/list", credentials, cancellationToken);
        if (!HasOkStatus(wallets.RootElement) ||
            !wallets.RootElement.TryGetProperty("wallets", out var walletArray) ||
            walletArray.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Nobitex returned an invalid wallet response.");

        var balances = new List<ExchangeBalance>();
        foreach (var wallet in walletArray.EnumerateArray())
        {
            string asset = GetString(wallet, "currency");
            if (string.IsNullOrWhiteSpace(asset)) continue;

            decimal total = GetDecimal(wallet, "balance");
            decimal locked = GetDecimal(wallet, "blockedBalance");
            decimal available = wallet.TryGetProperty("activeBalance", out _)
                ? GetDecimal(wallet, "activeBalance")
                : Math.Max(0m, total - locked);

            balances.Add(new ExchangeBalance(asset.ToUpperInvariant(), total, available, locked));
        }

        return new ExchangeAccountSnapshot(ExchangeCode, true, DateTime.UtcNow, balances);
    }

    public Task<ExchangeOrderResult> PlaceOrderAsync(ExchangeCredentials credentials,
        ExchangeOrderRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(ExchangeOrderResult.Rejected(
            "Nobitex order placement is not implemented in this read-only adapter. No request was sent."));

    private async Task<JsonDocument> GetAuthenticatedJsonAsync(string relativePath,
        ExchangeCredentials credentials, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (string.IsNullOrWhiteSpace(credentials.ApiKey) || string.IsNullOrWhiteSpace(credentials.ApiSecret))
            throw new UnauthorizedAccessException("Nobitex API public key and private Ed25519 signing key are both required.");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/" + relativePath.TrimStart('/'));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("CryptoAnalyzer/1.0");
        NobitexApiKeyAuthentication.Apply(request, credentials, Array.Empty<byte>());

        using var response = await _httpClient.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string body = responseBody.Length > 400 ? responseBody[..400] : responseBody;
            throw new HttpRequestException(
                $"Nobitex HTTP {(int)response.StatusCode} ({response.ReasonPhrase}). Response: {body}",
                null, response.StatusCode);
        }

        return JsonDocument.Parse(responseBody);
    }

    private static string GetApiFailureMessage(JsonElement root)
    {
        string code = GetString(root, "code");
        string message = GetString(root, "message");
        string detail = GetString(root, "detail");
        var parts = new[] { code, message, detail }.Where(x => !string.IsNullOrWhiteSpace(x));
        string reason = string.Join(" | ", parts);
        return string.IsNullOrWhiteSpace(reason)
            ? "Nobitex returned a response without status=ok."
            : $"Nobitex rejected the request: {reason}";
    }

    private static bool HasOkStatus(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty("status", out var status) &&
        status.ValueKind == JsonValueKind.String &&
        string.Equals(status.GetString(), "ok", StringComparison.OrdinalIgnoreCase);

    private static string GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return "";
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            _ => ""
        };
    }

    private static decimal GetDecimal(JsonElement element, string name)
    {
        string value = GetString(element, name);
        return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0m;
    }
}

public sealed record NobitexCredentialValidationResult(bool IsValid, string Message);
