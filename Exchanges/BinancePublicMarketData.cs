using System.Globalization;
using System.Text.Json;
using Skender.Stock.Indicators;

namespace CryptoAnalyzer.Exchanges;

/// <summary>Unauthenticated Binance Spot market-data client. It never submits orders.</summary>
public sealed class BinancePublicMarketData
{
    private readonly HttpClient _http;
    private static readonly HashSet<string> StablePairs = new(StringComparer.OrdinalIgnoreCase)
    {
        "USDCUSDT", "TUSDUSDT", "BUSDUSDT", "DAIUSDT", "FDUSDUSDT",
        "USDPUSDT", "USDDUSDT", "USD1USDT", "EURUSDT", "AEURUSDT", "RLUSDUSDT"
    };

    public BinancePublicMarketData(HttpClient http) => _http = http ?? throw new ArgumentNullException(nameof(http));

    public async Task<List<Quote>?> GetKlinesAsync(string symbol, string interval, int limit = 100,
        DateTime? nowUtc = null, CancellationToken cancellationToken = default)
    {
        if (!IsValidUsdtSymbol(symbol) || interval is not ("15m" or "1h") || limit is < 50 or > 1000)
            return null;

        try
        {
            using var response = await _http.GetAsync(
                $"/api/v3/klines?symbol={Uri.EscapeDataString(symbol.ToUpperInvariant())}&interval={interval}&limit={limit}",
                cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;

            var duration = interval == "15m" ? TimeSpan.FromMinutes(15) : TimeSpan.FromHours(1);
            DateTime utcNow = (nowUtc ?? DateTime.UtcNow).ToUniversalTime();
            var quotes = new List<Quote>();
            foreach (var row in document.RootElement.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 6) return null;
                long openTime = row[0].GetInt64();
                if (!TryDecimal(row[1], out var open) || !TryDecimal(row[2], out var high) ||
                    !TryDecimal(row[3], out var low) || !TryDecimal(row[4], out var close) ||
                    !TryDecimal(row[5], out var volume) || open <= 0 || high <= 0 || low <= 0 || close <= 0 || volume < 0)
                    return null;

                var openUtc = DateTimeOffset.FromUnixTimeMilliseconds(openTime).UtcDateTime;
                if (!CryptoAnalyzer.Analysis.StrategyRules.IsCandleClosed(openUtc, duration, utcNow)) continue;
                quotes.Add(new Quote { Date = openUtc, Open = open, High = high, Low = low, Close = close, Volume = volume });
            }

            return quotes.OrderBy(x => x.Date).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or
                                   FormatException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            Console.WriteLine($"Binance klines failed for {symbol} {interval}: {ex.Message}");
            return null;
        }
    }

    public async Task<decimal?> GetLastPriceAsync(string symbol, CancellationToken cancellationToken = default)
    {
        if (!IsValidUsdtSymbol(symbol)) return null;
        try
        {
            using var response = await _http.GetAsync(
                $"/api/v3/ticker/price?symbol={Uri.EscapeDataString(symbol.ToUpperInvariant())}", cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return document.RootElement.TryGetProperty("price", out var value) &&
                   TryDecimal(value, out var price) && price > 0 ? price : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or
                                   FormatException or InvalidOperationException)
        {
            Console.WriteLine($"Binance ticker failed for {symbol}: {ex.Message}");
            return null;
        }
    }

    public async Task<List<string>> GetTopUsdtSymbolsAsync(int count = 20,
        CancellationToken cancellationToken = default)
    {
        if (count is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(count));
        try
        {
            using var response = await _http.GetAsync("/api/v3/ticker/24hr", cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return document.RootElement.EnumerateArray()
                .Where(x => x.TryGetProperty("symbol", out var symbol) && symbol.ValueKind == JsonValueKind.String)
                .Select(x => new
                {
                    Symbol = x.GetProperty("symbol").GetString()!,
                    QuoteVolume = x.TryGetProperty("quoteVolume", out var volume) && TryDecimal(volume, out var parsed) ? parsed : 0m
                })
                .Where(x => IsValidUsdtSymbol(x.Symbol) && !StablePairs.Contains(x.Symbol) &&
                            !HasLeveragedTokenSuffix(x.Symbol))
                .OrderByDescending(x => x.QuoteVolume)
                .Take(count)
                .Select(x => x.Symbol.ToUpperInvariant())
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            Console.WriteLine($"Binance 24h ticker list failed: {ex.Message}");
            return [];
        }
    }

    public static bool IsValidUsdtSymbol(string? symbol) =>
        !string.IsNullOrWhiteSpace(symbol) && symbol.Length is >= 6 and <= 24 &&
        symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase) &&
        symbol[..^4].All(char.IsAsciiLetterOrDigit);

    private static bool HasLeveragedTokenSuffix(string symbol)
    {
        string baseAsset = symbol[..^4];
        return new[] { "UP", "DOWN", "BULL", "BEAR" }
            .Any(suffix => baseAsset.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryDecimal(JsonElement element, out decimal value)
    {
        string? text = element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }
}
