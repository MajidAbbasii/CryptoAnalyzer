using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CryptoAnalyzer.Exchanges;

public sealed record NobitexOcoCloseRequest(
    string Symbol, long PositionId, bool IsLong, decimal Amount,
    decimal CurrentMarketPrice, decimal TakeProfitPrice,
    decimal StopLossTriggerPrice, decimal StopLossLimitPrice);

public sealed record NobitexOcoResult(
    bool Accepted, IReadOnlyList<string> ExchangeOrderIds, string Message,
    bool OutcomeUnknown = false);

public sealed record NobitexSpotOcoExitRequest(
    string Symbol, decimal Amount, decimal CurrentMarketPrice,
    decimal TakeProfitPrice, decimal StopLossTriggerPrice,
    decimal StopLossLimitPrice, string? ClientOrderId = null);

/// <summary>
/// Dedicated Nobitex order adapter. Production submission remains fail-closed through
/// OrderExecutionPolicy; the injectable gate exists only for isolated HTTP-mock tests.
/// It never retries a POST because a timeout may mean the exchange accepted the order.
/// </summary>
public sealed class NobitexTradingAdapter : IExchangeAdapter
{
    private static readonly Uri OfficialBaseAddress = new("https://apiv2.nobitex.ir/");
    private readonly HttpClient _http;
    private readonly IExchangeAdapter _readOnly;
    private readonly Func<bool> _submissionEnabled;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string ExchangeCode => "NOBITEX";
    public bool CanPlaceOrders => true;
    public ExchangeCapabilities Capabilities { get; } = new(
        "NOBITEX", SupportsSpot: true, SupportsMargin: true, SupportsFutures: false,
        SupportsMarketOrders: true, SupportsLimitOrders: true);

    public NobitexTradingAdapter(HttpClient httpClient, IExchangeAdapter readOnlyAdapter,
        Func<bool>? submissionEnabled = null)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _readOnly = readOnlyAdapter ?? throw new ArgumentNullException(nameof(readOnlyAdapter));
        _submissionEnabled = submissionEnabled ?? (() => OrderExecutionPolicy.IsLiveExecutionEnabled);
        _http.BaseAddress ??= OfficialBaseAddress;
        if (_http.BaseAddress.Scheme != Uri.UriSchemeHttps ||
            !_http.BaseAddress.Host.Equals(OfficialBaseAddress.Host, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Nobitex credentials may only be sent to the official HTTPS API host.", nameof(httpClient));
    }

    public Task<bool> ValidateCredentialsAsync(ExchangeCredentials credentials, CancellationToken cancellationToken = default) =>
        _readOnly.ValidateCredentialsAsync(credentials, cancellationToken);

    public Task<ExchangeAccountSnapshot> GetAccountSnapshotAsync(ExchangeCredentials credentials, CancellationToken cancellationToken = default) =>
        _readOnly.GetAccountSnapshotAsync(credentials, cancellationToken);

    public async Task<ExchangeOrderResult> PlaceOrderAsync(ExchangeCredentials credentials,
        ExchangeOrderRequest request, CancellationToken cancellationToken = default)
    {
        if (!_submissionEnabled()) return ExchangeOrderResult.Rejected("Live execution is disabled; no request was sent.");
        if (credentials is null || string.IsNullOrWhiteSpace(credentials.ApiKey) || string.IsNullOrWhiteSpace(credentials.ApiSecret))
            return ExchangeOrderResult.Rejected("Nobitex API public key and private Ed25519 signing key are both required.");
        if (request is null) return ExchangeOrderResult.Rejected("Order request is missing.");
        if (request.Mode == ExchangeTradeMode.Futures)
            return ExchangeOrderResult.Rejected("Nobitex Futures is not supported by this adapter.");
        if (!TryParseSymbol(request.Symbol, out var src, out var dst))
            return ExchangeOrderResult.Rejected("Invalid Nobitex market symbol. Use a pair such as BTCUSDT or BTCIRT.");
        if (request.Quantity <= 0) return ExchangeOrderResult.Rejected("Order quantity must be positive.");
        if (request.ClientOrderId?.Length > 32) return ExchangeOrderResult.Rejected("clientOrderId cannot exceed 32 characters.");
        string clientOrderId = request.ClientOrderId ?? "ca" + Guid.NewGuid().ToString("N")[..30];

        decimal price = request.Type == ExchangeOrderType.Limit ? request.LimitPrice ?? 0 : request.PriceGuard ?? 0;
        if (price <= 0) return ExchangeOrderResult.Rejected(
            request.Type == ExchangeOrderType.Market
                ? "Market orders require a positive PriceGuard to limit price slippage."
                : "Limit orders require a positive limit price.");

        if (request.Mode == ExchangeTradeMode.Margin)
        {
            if (request.Leverage < 1 || request.Leverage > 20 || request.Leverage * 2m % 1m != 0)
                return ExchangeOrderResult.Rejected("Margin leverage must be >=1, <=20 and use 0.5 increments.");
            var market = await GetMarginMarketAsync(src, dst, credentials, cancellationToken);
            if (market.Error is not null) return ExchangeOrderResult.Rejected(market.Error);
            bool enabled = request.Side == ExchangeOrderSide.Buy ? market.BuyEnabled : market.SellEnabled;
            if (!enabled) return ExchangeOrderResult.Rejected("This side is currently disabled for the selected Nobitex margin market.");
            if (request.Leverage > market.MaxLeverage)
                return ExchangeOrderResult.Rejected($"Requested leverage exceeds this account/market maximum ({market.MaxLeverage}).");
        }

        var body = new Dictionary<string, string>
        {
            ["srcCurrency"] = src,
            ["dstCurrency"] = dst,
            ["type"] = request.Side == ExchangeOrderSide.Buy ? "buy" : "sell",
            ["execution"] = request.Type == ExchangeOrderType.Market ? "market" : "limit",
            ["amount"] = F(request.Quantity),
            ["price"] = F(price)
        };
        body["clientOrderId"] = clientOrderId;
        string path = request.Mode == ExchangeTradeMode.Spot ? "market/orders/add" : "margin/orders/add";
        if (request.Mode == ExchangeTradeMode.Margin) body["leverage"] = F(request.Leverage);

        try
        {
            using var document = await SendJsonAsync(HttpMethod.Post, path, credentials, body, cancellationToken);
            if (!IsOk(document.RootElement)) return ExchangeOrderResult.Rejected(ApiError(document.RootElement));
            if (!document.RootElement.TryGetProperty("order", out var order) || !order.TryGetProperty("id", out var id))
                return ExchangeOrderResult.Unknown("Nobitex response did not contain an order id; reconcile by clientOrderId before any retry.", clientOrderId);
            return new ExchangeOrderResult(true, id.ToString(), "Order accepted by Nobitex; acceptance does not mean it has filled.", ClientOrderId: clientOrderId);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ExchangeOrderResult.Unknown("Order submission timed out. Its outcome is unknown; check Nobitex order history/clientOrderId and do not retry blindly.", clientOrderId);
        }
        catch (HttpRequestException ex)
        {
            return ExchangeOrderResult.Unknown($"Network outcome is unknown ({ex.GetType().Name}). Reconcile with Nobitex before retrying.", clientOrderId);
        }
        catch (JsonException)
        {
            return ExchangeOrderResult.Unknown("Nobitex returned an unreadable response. Reconcile the order before retrying.", clientOrderId);
        }
        catch (UnauthorizedAccessException ex)
        {
            return ExchangeOrderResult.Rejected(ex.Message);
        }
    }

    /// <summary>Places a two-leg OCO close against an already-open margin position.</summary>
    public async Task<NobitexOcoResult> PlaceMarginPositionOcoCloseAsync(
        ExchangeCredentials credentials, NobitexOcoCloseRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!_submissionEnabled()) return new(false, Array.Empty<string>(), "Live execution is disabled; no request was sent.");
        if (credentials is null || string.IsNullOrWhiteSpace(credentials.ApiKey) || string.IsNullOrWhiteSpace(credentials.ApiSecret))
            return new(false, Array.Empty<string>(), "Nobitex API public key and private Ed25519 signing key are both required.");
        if (request.PositionId <= 0 || request.Amount <= 0 || request.CurrentMarketPrice <= 0 ||
            request.TakeProfitPrice <= 0 || request.StopLossTriggerPrice <= 0 || request.StopLossLimitPrice <= 0)
            return new(false, Array.Empty<string>(), "Position id, amount and all OCO prices must be positive.");
        if (!TryParseSymbol(request.Symbol, out _, out _))
            return new(false, Array.Empty<string>(), "Invalid Nobitex market symbol.");

        TryParseSymbol(request.Symbol, out var expectedSrc, out var expectedDst);

        // Nobitex documents: closing a short (buy) => TP < market < SL;
        // closing a long (sell) => TP > market > SL.
        bool directionValid = request.IsLong
            ? request.TakeProfitPrice > request.CurrentMarketPrice && request.CurrentMarketPrice > request.StopLossTriggerPrice
              && request.StopLossLimitPrice <= request.StopLossTriggerPrice
            : request.TakeProfitPrice < request.CurrentMarketPrice && request.CurrentMarketPrice < request.StopLossTriggerPrice
              && request.StopLossLimitPrice >= request.StopLossTriggerPrice;
        if (!directionValid)
            return new(false, Array.Empty<string>(), "OCO prices violate Nobitex position-close price conditions for this position side.");

        try
        {
            using var positionDocument = await SendJsonAsync(HttpMethod.Get,
                $"positions/{request.PositionId}/status", credentials, null, cancellationToken);
            if (!IsOk(positionDocument.RootElement) || !positionDocument.RootElement.TryGetProperty("position", out var position))
                return new(false, Array.Empty<string>(), ApiError(positionDocument.RootElement));
            string actualSrc = position.TryGetProperty("srcCurrency", out var srcValue) ? srcValue.ToString() : "";
            string actualDst = position.TryGetProperty("dstCurrency", out var dstValue) ? dstValue.ToString() : "";
            string actualSide = position.TryGetProperty("side", out var sideValue) ? sideValue.ToString() : "";
            string actualStatus = position.TryGetProperty("status", out var statusValue) ? statusValue.ToString() : "";
            if (!actualSrc.Equals(expectedSrc, StringComparison.OrdinalIgnoreCase) ||
                !(actualDst.Equals(expectedDst, StringComparison.OrdinalIgnoreCase) ||
                  (expectedDst == "rls" && actualDst.Equals("irt", StringComparison.OrdinalIgnoreCase))))
                return new(false, Array.Empty<string>(), "Position market does not match the requested OCO symbol.");
            if (!actualStatus.Equals("Open", StringComparison.OrdinalIgnoreCase))
                return new(false, Array.Empty<string>(), "OCO can only be attached to an open margin position.");
            if (!(request.IsLong ? actualSide.Equals("buy", StringComparison.OrdinalIgnoreCase) : actualSide.Equals("sell", StringComparison.OrdinalIgnoreCase)))
                return new(false, Array.Empty<string>(), "Position side does not match the requested long/short OCO direction.");
            decimal availableLiability = DecimalProperty(position, "liability") - DecimalProperty(position, "liabilityInOrder");
            if (availableLiability <= 0 || request.Amount > availableLiability)
                return new(false, Array.Empty<string>(), "OCO amount exceeds the position liability available for closing.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, Array.Empty<string>(), "Position verification timed out; OCO was not submitted.", true);
        }
        catch (HttpRequestException ex)
        {
            return new(false, Array.Empty<string>(), $"Could not verify the position ({ex.GetType().Name}); OCO was not submitted.");
        }
        catch (JsonException)
        {
            return new(false, Array.Empty<string>(), "Could not parse the position response; OCO was not submitted.");
        }
        catch (UnauthorizedAccessException ex)
        {
            return new(false, Array.Empty<string>(), ex.Message);
        }

        var body = new Dictionary<string, string>
        {
            ["mode"] = "oco",
            ["amount"] = F(request.Amount),
            ["price"] = F(request.TakeProfitPrice),
            ["stopPrice"] = F(request.StopLossTriggerPrice),
            ["stopLimitPrice"] = F(request.StopLossLimitPrice)
        };
        try
        {
            using var document = await SendJsonAsync(HttpMethod.Post,
                $"positions/{request.PositionId}/close", credentials, body, cancellationToken);
            if (!IsOk(document.RootElement))
                return new(false, Array.Empty<string>(), ApiError(document.RootElement));
            if (!document.RootElement.TryGetProperty("orders", out var orders) || orders.ValueKind != JsonValueKind.Array)
                return new(false, Array.Empty<string>(), "Nobitex OCO response did not contain its two orders; reconcile position orders.", true);
            var ids = orders.EnumerateArray()
                .Where(x => x.TryGetProperty("id", out _))
                .Select(x => x.GetProperty("id").ToString()).ToArray();
            if (ids.Length != 2)
                return new(false, ids, "OCO response did not confirm both legs; reconcile before further action.", true);
            return new(true, ids, "OCO close accepted by Nobitex. One leg should cancel when the other executes.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, Array.Empty<string>(), "OCO submission timed out; outcome unknown. Reconcile position orders and do not resubmit.", true);
        }
        catch (HttpRequestException ex)
        {
            return new(false, Array.Empty<string>(), $"OCO network outcome unknown ({ex.GetType().Name}); reconcile before retrying.", true);
        }
        catch (JsonException)
        {
            return new(false, Array.Empty<string>(), "Unreadable OCO response; reconcile before retrying.", true);
        }
    }

    /// <summary>
    /// Places a Spot sell OCO to protect an already-owned asset. The free wallet balance
    /// is checked immediately before submission. A timeout is reported as unknown and
    /// must be reconciled; this method never retries a POST.
    /// </summary>
    public async Task<NobitexOcoResult> PlaceSpotOcoExitAsync(
        ExchangeCredentials credentials, NobitexSpotOcoExitRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!_submissionEnabled())
            return new(false, Array.Empty<string>(), "Live execution is disabled; no request was sent.");
        if (credentials is null || string.IsNullOrWhiteSpace(credentials.ApiKey) || string.IsNullOrWhiteSpace(credentials.ApiSecret))
            return new(false, Array.Empty<string>(), "Nobitex API public key and private Ed25519 signing key are both required.");
        if (request.Amount <= 0 || request.CurrentMarketPrice <= 0 || request.TakeProfitPrice <= 0 ||
            request.StopLossTriggerPrice <= 0 || request.StopLossLimitPrice <= 0)
            return new(false, Array.Empty<string>(), "Amount, current price and all OCO prices must be positive.");
        if (request.ClientOrderId?.Length > 32)
            return new(false, Array.Empty<string>(), "clientOrderId cannot exceed 32 characters.");
        if (!TryParseSymbol(request.Symbol, out var src, out var dst))
            return new(false, Array.Empty<string>(), "Invalid Nobitex market symbol.");

        // For a Spot sell OCO: take-profit is above market; stop trigger is below.
        // The stop-limit sell price must not be above its trigger.
        if (!(request.TakeProfitPrice > request.CurrentMarketPrice &&
              request.CurrentMarketPrice > request.StopLossTriggerPrice &&
              request.StopLossLimitPrice <= request.StopLossTriggerPrice))
            return new(false, Array.Empty<string>(),
                "Spot sell OCO requires TP > current market > stop trigger and stop-limit <= stop trigger.");

        try
        {
            var snapshot = await _readOnly.GetAccountSnapshotAsync(credentials, cancellationToken);
            var asset = snapshot.Balances.FirstOrDefault(x =>
                x.Asset.Equals(src, StringComparison.OrdinalIgnoreCase));
            if (!snapshot.Authenticated || asset is null || asset.Available < request.Amount)
                return new(false, Array.Empty<string>(),
                    $"Insufficient available {src.ToUpperInvariant()} balance for the OCO amount.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, Array.Empty<string>(), "Wallet verification timed out; OCO was not submitted.", true);
        }
        catch (Exception ex) when (ex is HttpRequestException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return new(false, Array.Empty<string>(),
                $"Could not verify the Spot wallet ({ex.GetType().Name}); OCO was not submitted.");
        }

        string clientOrderId = request.ClientOrderId ?? "oco" + Guid.NewGuid().ToString("N")[..29];
        var body = new Dictionary<string, string>
        {
            ["type"] = "sell",
            ["srcCurrency"] = src,
            ["dstCurrency"] = dst,
            ["amount"] = F(request.Amount),
            ["mode"] = "oco",
            ["price"] = F(request.TakeProfitPrice),
            ["stopPrice"] = F(request.StopLossTriggerPrice),
            ["stopLimitPrice"] = F(request.StopLossLimitPrice),
            ["clientOrderId"] = clientOrderId
        };

        try
        {
            using var document = await SendJsonAsync(HttpMethod.Post,
                "market/orders/add", credentials, body, cancellationToken);
            if (!IsOk(document.RootElement))
                return new(false, Array.Empty<string>(), ApiError(document.RootElement));
            if (!document.RootElement.TryGetProperty("orders", out var orders) || orders.ValueKind != JsonValueKind.Array)
                return new(false, Array.Empty<string>(),
                    "Nobitex OCO response did not contain both orders; reconcile order history before retrying.", true);
            var ids = orders.EnumerateArray()
                .Where(x => x.TryGetProperty("id", out var id) && id.ValueKind is JsonValueKind.Number or JsonValueKind.String)
                .Select(x => x.GetProperty("id").ToString()).ToArray();
            if (ids.Length != 2)
                return new(false, ids,
                    "Nobitex did not confirm both OCO legs; reconcile order history before taking further action.", true);
            return new(true, ids, "Spot OCO exit accepted by Nobitex; the exchange links the take-profit and stop-loss legs.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, Array.Empty<string>(),
                "Spot OCO submission timed out; outcome unknown. Check Nobitex order history and do not resubmit blindly.", true);
        }
        catch (HttpRequestException ex)
        {
            return new(false, Array.Empty<string>(),
                $"Spot OCO network outcome unknown ({ex.GetType().Name}); reconcile before retrying.", true);
        }
        catch (JsonException)
        {
            return new(false, Array.Empty<string>(),
                "Unreadable Spot OCO response; reconcile order history before retrying.", true);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new(false, Array.Empty<string>(), ex.Message);
        }
    }

    private async Task<(decimal MaxLeverage, bool BuyEnabled, bool SellEnabled, string? Error)> GetMarginMarketAsync(
        string src, string dst, ExchangeCredentials credentials, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await SendJsonAsync(HttpMethod.Get, "margin/markets/list", credentials, null, cancellationToken);
            if (!IsOk(document.RootElement) || !document.RootElement.TryGetProperty("markets", out var markets))
                return (0, false, false, ApiError(document.RootElement));
            string key = src.ToUpperInvariant() + (dst == "rls" ? "IRT" : dst.ToUpperInvariant());
            if (!markets.TryGetProperty(key, out var market)) return (0, false, false, "This pair is not listed in Nobitex margin markets.");
            decimal max = DecimalProperty(market, "maxLeverage");
            bool buy = BoolProperty(market, "buyEnabled");
            bool sell = BoolProperty(market, "sellEnabled");
            return max > 0 ? (max, buy, sell, null) : (0, false, false, "Nobitex returned an invalid maximum leverage.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            return (0, false, false, "Could not verify live margin market limits; order was not submitted.");
        }
    }

    private async Task<JsonDocument> SendJsonAsync(HttpMethod method, string path,
        ExchangeCredentials credentials, Dictionary<string, string>? body, CancellationToken cancellationToken)
    {
        byte[] rawBody = body is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body, JsonOptions));
        using var request = new HttpRequestMessage(method, "/" + path.TrimStart('/'));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
            request.Content = new ByteArrayContent(rawBody);
        if (request.Content is not null)
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        NobitexApiKeyAuthentication.Apply(request, credentials, rawBody);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException("Nobitex rejected the API key or Ed25519 signature.");
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static bool TryParseSymbol(string symbol, out string src, out string dst)
    {
        src = dst = "";
        if (string.IsNullOrWhiteSpace(symbol)) return false;
        string value = symbol.Trim().Replace("/", "").Replace("-", "").ToUpperInvariant();
        string suffix = value.EndsWith("USDT", StringComparison.Ordinal) ? "USDT" :
            value.EndsWith("IRT", StringComparison.Ordinal) ? "IRT" :
            value.EndsWith("RLS", StringComparison.Ordinal) ? "RLS" : "";
        if (suffix.Length == 0 || value.Length <= suffix.Length) return false;
        src = value[..^suffix.Length].ToLowerInvariant();
        dst = suffix == "IRT" ? "rls" : suffix.ToLowerInvariant();
        return src.All(char.IsAsciiLetter) && src.Length <= 12;
    }

    private static bool IsOk(JsonElement root) => root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty("status", out var status) && status.GetString() == "ok";
    private static string ApiError(JsonElement root) =>
        $"Nobitex rejected the request: {(root.TryGetProperty("code", out var code) ? code.ToString() : "UnknownError")} - {(root.TryGetProperty("message", out var message) ? message.ToString() : "No details")}";
    private static decimal DecimalProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && decimal.TryParse(value.ToString(), NumberStyles.Number,
            CultureInfo.InvariantCulture, out var parsed) ? parsed : 0m;
    private static bool BoolProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private static string F(decimal value) => value.ToString(CultureInfo.InvariantCulture);
}
