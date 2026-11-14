namespace CryptoAnalyzer.Exchanges;

public enum ExchangeTradeMode { Spot, Margin, Futures }
public enum ExchangeOrderSide { Buy, Sell }
public enum ExchangeOrderType { Market, Limit }

public sealed record ExchangeCapabilities(
    string ExchangeCode,
    bool SupportsSpot,
    bool SupportsMargin,
    bool SupportsFutures,
    bool SupportsMarketOrders,
    bool SupportsLimitOrders);

public sealed record ExchangeCredentials(string ApiKey, string ApiSecret, string? Passphrase = null);

public sealed record ExchangeBalance(string Asset, decimal Total, decimal Available, decimal Locked);

public sealed record ExchangeAccountSnapshot(
    string ExchangeCode, bool Authenticated, DateTime RetrievedAtUtc,
    IReadOnlyList<ExchangeBalance> Balances);

public sealed record ExchangeOrderRequest(
    string Symbol,
    ExchangeTradeMode Mode,
    ExchangeOrderSide Side,
    ExchangeOrderType Type,
    decimal Quantity,
    decimal? LimitPrice = null,
    decimal Leverage = 1,
    string? ClientOrderId = null,
    decimal? PriceGuard = null);

public sealed record ExchangeOrderResult(bool Accepted, string? ExchangeOrderId, string Message,
    bool OutcomeUnknown = false, string? ClientOrderId = null)
{
    public static ExchangeOrderResult Rejected(string message) => new(false, null, message);
    public static ExchangeOrderResult Unknown(string message, string? clientOrderId = null) => new(false, null, message, true, clientOrderId);
}

public interface IExchangeAdapter
{
    string ExchangeCode { get; }
    ExchangeCapabilities Capabilities { get; }
    bool CanPlaceOrders { get; }
    Task<bool> ValidateCredentialsAsync(ExchangeCredentials credentials, CancellationToken cancellationToken = default);
    Task<ExchangeAccountSnapshot> GetAccountSnapshotAsync(ExchangeCredentials credentials,
        CancellationToken cancellationToken = default);
    Task<ExchangeOrderResult> PlaceOrderAsync(ExchangeCredentials credentials,
        ExchangeOrderRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Registry of explicitly registered exchange adapters; it never guesses an exchange.</summary>
public sealed class ExchangeAdapterRegistry
{
    private readonly IReadOnlyDictionary<string, IExchangeAdapter> _adapters;

    public ExchangeAdapterRegistry(IEnumerable<IExchangeAdapter> adapters)
    {
        var items = adapters.ToList();
        if (items.Any(x => string.IsNullOrWhiteSpace(x.ExchangeCode)))
            throw new ArgumentException("Every exchange adapter must have an exchange code.", nameof(adapters));
        var duplicate = items.GroupBy(x => x.ExchangeCode, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(x => x.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"Duplicate exchange adapter: {duplicate.Key}", nameof(adapters));
        _adapters = items.ToDictionary(x => x.ExchangeCode, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<ExchangeCapabilities> GetSupportedExchanges() =>
        _adapters.Values.Select(x => x.Capabilities).ToArray();

    public IExchangeAdapter GetRequired(string exchangeCode) =>
        _adapters.TryGetValue(exchangeCode, out var adapter)
            ? adapter
            : throw new KeyNotFoundException($"Exchange adapter '{exchangeCode}' is not configured.");
}

/// <summary>Safe placeholder until an exchange-specific adapter is implemented and tested.</summary>
public sealed class DisabledExchangeAdapter : IExchangeAdapter
{
    public string ExchangeCode { get; }
    public ExchangeCapabilities Capabilities { get; }
    public bool CanPlaceOrders => false;

    public DisabledExchangeAdapter(ExchangeCapabilities capabilities)
    {
        Capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        ExchangeCode = capabilities.ExchangeCode;
    }

    public Task<bool> ValidateCredentialsAsync(ExchangeCredentials credentials,
        CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<ExchangeAccountSnapshot> GetAccountSnapshotAsync(ExchangeCredentials credentials,
        CancellationToken cancellationToken = default) =>
        Task.FromException<ExchangeAccountSnapshot>(new NotSupportedException($"{ExchangeCode} account read is not implemented."));

    public Task<ExchangeOrderResult> PlaceOrderAsync(ExchangeCredentials credentials,
        ExchangeOrderRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(ExchangeOrderResult.Rejected(
            $"{ExchangeCode} order execution is not implemented and is disabled."));
}

public static class OrderExecutionPolicy
{
    // Deliberately hard-disabled until the complete order lifecycle and risk checks pass.
    public static bool IsLiveExecutionEnabled => false;

    public static void EnsureLiveExecutionDisabled()
    {
        if (!IsLiveExecutionEnabled)
            throw new InvalidOperationException("Live exchange order execution is disabled.");
    }
}
