using System.Text;
using Binance.Net.Clients;
using Binance.Net.Enums;
using Microsoft.Data.Sqlite;
using Skender.Stock.Indicators;
using Telegram.Bot;
using Telegram.Bot.Types.ReplyMarkups;
using CryptoAnalyzer.Analysis;
using CryptoAnalyzer.Exchanges;
using CryptoAnalyzer.Persistence;

class Program
{
    // Fail-closed until the complete multi-exchange order lifecycle has passed tests.
    private const bool TradingExecutionEnabled = false;
    private const bool TradingFeaturesEnabled = false;
    const string OllamaModel = "llama3.2:latest";
    const string OllamaUrl = "http://127.0.0.1:11434";
    static readonly string TelegramToken = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN") ?? "";
    static readonly HttpClient ExchangeHttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    static readonly NobitexReadOnlyAdapter NobitexReadOnly = new(ExchangeHttpClient);
    static readonly NobitexTradingAdapter NobitexTrading = new(
        new HttpClient { Timeout = TimeSpan.FromSeconds(15) }, NobitexReadOnly);
    static readonly HttpClient BinanceTestnetHttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    static readonly BinanceSpotTestnetReadOnlyAdapter BinanceTestnetReadOnly = new(BinanceTestnetHttpClient);
    static readonly HttpClient BinancePublicHttpClient = new()
    {
        BaseAddress = new Uri("https://data-api.binance.vision"),
        Timeout = TimeSpan.FromSeconds(15)
    };
    static readonly BinancePublicMarketData BinancePublicMarket = new(BinancePublicHttpClient);
    static readonly string NobitexBaseUrl = "https://apiv2.nobitex.ir";
    private static readonly string DatabaseConnectionString = CreateDatabaseConnectionString();
    static decimal UsdtTomanRate = 276_000m;
    static readonly Dictionary<string, string> SignalPayloads = new();
    static readonly Dictionary<long, PendingOrder> PendingOrders = new();
    static readonly System.Collections.Concurrent.ConcurrentDictionary<long, OrderWizardState> OrderWizards = new();
    static readonly System.Collections.Concurrent.ConcurrentDictionary<long, ManualUiState> ManualUiStates = new();
    static readonly System.Collections.Concurrent.ConcurrentDictionary<long, AccountLinkState> AccountLinkStates = new();

    sealed class AccountLinkState
    {
        public string Step { get; set; } = "label";
        public string Label { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public long? AccountId { get; set; }
    }

    sealed class ManualUiState
    {
        public string Action { get; set; } = "";
        public string Step { get; set; } = "symbol";
        public string Symbol { get; set; } = "";
        public decimal EntryPrice { get; set; }
        public string? ApprovalId { get; set; }
    }

    sealed class OrderWizardState
    {
        public long AccountId { get; set; }
        public string Symbol { get; set; } = "";
        public ExchangeTradeMode Mode { get; set; }
        public ExchangeOrderSide Side { get; set; }
        public ExchangeOrderType Type { get; set; }
        public decimal Quantity { get; set; }
        public decimal? LimitPrice { get; set; }
        public decimal? PriceGuard { get; set; }
        public decimal? StopLoss { get; set; }
        public decimal? TakeProfit { get; set; }
        public decimal Leverage { get; set; } = 1m;
        public string Step { get; set; } = "symbol";
        public bool IsSignalTrade { get; set; }
        public decimal? RequestedBudgetUsdt { get; set; }
        public decimal SignalEntryPrice { get; set; }
    }

    static readonly string NobitexApiKey =
        Environment.GetEnvironmentVariable("API_KEY") ?? "";
    static readonly string NobitexApiSecret =
        Environment.GetEnvironmentVariable("SECRET_KEY") ?? "";

    static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("Crypto Analyzer - Multi User");
        Console.WriteLine("SELL only for open trades | Users must /start");
        Console.WriteLine($"Database: {new SqliteConnectionStringBuilder(DatabaseConnectionString).DataSource}");
        Console.WriteLine("Press Ctrl+C to stop\n");

        InitializeDatabase();

        if (args.Any(x => x.Equals("--account-add", StringComparison.OrdinalIgnoreCase)))
        {
            ProvisionExchangeAccountInteractive();
            return;
        }

        if (string.IsNullOrWhiteSpace(TelegramToken))
            throw new InvalidOperationException("TELEGRAM_BOT_TOKEN environment variable is required.");

        var restClient = new BinanceRestClient();
        var botClient = new TelegramBotClient(TelegramToken);

        // گوش دادن به دکمه و دستورهای تلگرام
        _ = Task.Run(() => ListenTelegramCommands(botClient, restClient));
        _ = Task.Run(MonitorPendingSignalsAsync);

        while (true)
        {
            try
            {
                Console.WriteLine($"\n===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");

                // Generate each market independently. A user's preference only controls
                // delivery; candle/price/SL/TP data are never mixed between exchanges.
                var users = GetActiveChatIds();
                Console.WriteLine($"Active users: {users.Count}");
                if (users.Count == 0)
                    Console.WriteLine("Signal scan skipped: no active users found in the configured database. Send /start to the bot.");
                var preferences = new UserSignalPreferenceRepository(DatabaseConnectionString);
                var sourcesByUser = users.ToDictionary(chatId => chatId, preferences.GetSource);
                var sourceSignals = new Dictionary<string, List<(string Text, InlineKeyboardMarkup? Keyboard)>>();

                if (sourcesByUser.Values.Any(x => x is SignalSourcePreference.Nobitex or SignalSourcePreference.Both))
                {
                    var nobitexSymbols = await GetTopVolumeSymbols(restClient);
                    Console.WriteLine($"Analyzing {nobitexSymbols.Count} Nobitex markets...");
                    var signals = new List<(string Text, InlineKeyboardMarkup? Keyboard)>();
                    foreach (var symbol in nobitexSymbols)
                    {
                        var result = await AnalyzeSymbol(restClient, symbol, SignalSourcePreference.Nobitex);
                        if (result is not null) signals.AddRange(result);
                        await Task.Delay(500);
                    }
                    sourceSignals[SignalSourcePreference.Nobitex] = signals;
                }

                if (sourcesByUser.Values.Any(x => x is SignalSourcePreference.Binance or SignalSourcePreference.Both))
                {
                    var binanceSymbols = await BinancePublicMarket.GetTopUsdtSymbolsAsync(20);
                    Console.WriteLine($"Analyzing {binanceSymbols.Count} Binance markets...");
                    var signals = new List<(string Text, InlineKeyboardMarkup? Keyboard)>();
                    foreach (var symbol in binanceSymbols)
                    {
                        var result = await AnalyzeSymbol(restClient, symbol, SignalSourcePreference.Binance);
                        if (result is not null) signals.AddRange(result);
                        await Task.Delay(500);
                    }
                    sourceSignals[SignalSourcePreference.Binance] = signals;
                }

                Console.WriteLine($"Signals: {sourceSignals.Sum(x => x.Value.Count)}");

                foreach (var chatId in users)
                {
                    var selectedSources = sourcesByUser[chatId] == SignalSourcePreference.Both
                        ? new[] { SignalSourcePreference.Nobitex, SignalSourcePreference.Binance }
                        : new[] { sourcesByUser[chatId] };

                    foreach (var source in selectedSources)
                    {
                        if (!sourceSignals.TryGetValue(source, out var selectedSignals)) continue;
                        foreach (var (text, keyboard) in selectedSignals)
                        {
                            try
                            {
                                await botClient.SendMessage(chatId, text, replyMarkup: MainReplyKeyboard());
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Send {source} signal fail {chatId}: {ex.Message}");
                            }
                            await Task.Delay(50);
                        }
                    }

                }

                if (sourceSignals.Values.Sum(x => x.Count) == 0)
                    Console.WriteLine("No signals this round.");

                Console.WriteLine($"Next run at {DateTime.Now.AddMinutes(3):HH:mm}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Main Error: {ex.Message}");

                if (ex.Message.Contains("rate") ||
                    ex.Message.Contains("429") ||
                    ex.Message.Contains("Too many"))
                {
                    Console.WriteLine("Rate limit → waiting 60 seconds...");
                    await Task.Delay(60000);
                }
            }

            await Task.Delay(TimeSpan.FromMinutes(3));
        }
    }

    static async Task MonitorPendingSignalsAsync()
    {
        Console.WriteLine("Signal outcome monitor started (60-second interval; no order execution).");
        while (true)
        {
            try
            {
                await ResolvePendingSignals();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Signal monitor error: {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromMinutes(1));
        }
    }

    static string CreateDatabaseConnectionString()
    {
        string configuredPath = Environment.GetEnvironmentVariable("CRYPTO_ANALYZER_DB_PATH") ?? "";
        string path = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(AppContext.BaseDirectory, "crypto_analyzer.db")
            : Path.GetFullPath(configuredPath);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        return new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true }.ToString();
    }

    static byte[] FromUrlSafeBase64(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }

    static string ToUrlSafeBase64(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static string SignNobitex(string timestamp, string method, string fullPath, string rawBody)
    {
        // payload = timestamp + METHOD + full_path + raw_body
        string payload = timestamp + method.ToUpperInvariant() + fullPath + (rawBody ?? "");
        byte[] msg = Encoding.UTF8.GetBytes(payload);
        byte[] sk = FromUrlSafeBase64(NobitexApiSecret);

        // NSec Ed25519
        var algorithm = NSec.Cryptography.SignatureAlgorithm.Ed25519;
        var key = NSec.Cryptography.Key.Import(
            algorithm,
            sk,
            NSec.Cryptography.KeyBlobFormat.RawPrivateKey);

        byte[] sig = algorithm.Sign(key, msg);
        return ToUrlSafeBase64(sig);
    }

    static async Task<(int Status, string Body)> NobitexSendAsync(
        HttpMethod method, string fullPath, string? jsonBody)
    {
        if (method != HttpMethod.Get && (!TradingExecutionEnabled || !OrderExecutionPolicy.IsLiveExecutionEnabled))
            return (0, "Trading execution is disabled in the current manual-only phase.");

        string rawBody = jsonBody ?? "";
        string url = NobitexBaseUrl + fullPath;

        using var handler = new HttpClientHandler { UseProxy = false, Proxy = null };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");

        using var req = new HttpRequestMessage(method, url);

        if (!string.IsNullOrWhiteSpace(NobitexApiKey) && !string.IsNullOrWhiteSpace(NobitexApiSecret))
        {
            // حالت API Key (مستند جدید)
            string ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            string sig = SignNobitex(ts, method.Method, fullPath, rawBody);
            req.Headers.TryAddWithoutValidation("Nobitex-Key", NobitexApiKey);
            req.Headers.TryAddWithoutValidation("Nobitex-Timestamp", ts);
            req.Headers.TryAddWithoutValidation("Nobitex-Signature", sig);
        }
        else
        {
            return (0, "NOBITEX_TOKEN یا API_KEY/SECRET تنظیم نشده");
        }

        if (method == HttpMethod.Post || method == HttpMethod.Put)
            req.Content = new StringContent(rawBody, Encoding.UTF8, "application/json");

        var resp = await http.SendAsync(req);
        string body = await resp.Content.ReadAsStringAsync();
        Console.WriteLine($"Nobitex {(int)resp.StatusCode} {fullPath} {body}");
        return ((int)resp.StatusCode, body);
    }

    static string StorePayload(string fullPayload)
    {
        string id = Guid.NewGuid().ToString("N")[..12];
        SignalPayloads[id] = fullPayload;

        if (SignalPayloads.Count > 200)
        {
            foreach (var old in SignalPayloads.Keys.Take(50).ToList())
                SignalPayloads.Remove(old);
        }
        return id;
    }

    static async Task<NobitexOrderResult> PlaceNobitexMarginOrderAsync(
    string symbol,
    string side,
    int leverage,
    decimal amountSrc,
    decimal price,
    decimal stopLoss,
    decimal takeProfit)
    {
        if (!OrderExecutionPolicy.IsLiveExecutionEnabled)
            return new NobitexOrderResult { Ok = false, Error = "Live exchange order execution is disabled." };

        if ((string.IsNullOrWhiteSpace(NobitexApiKey) || string.IsNullOrWhiteSpace(NobitexApiSecret)))
        {
            return new NobitexOrderResult
            {
                Ok = false,
                Error = "NOBITEX_TOKEN یا NOBITEX_API_KEY/SECRET تنظیم نشده"
            };
        }

        var pair = ParseNobitexPair(symbol);
        if (pair is null)
            return new NobitexOrderResult { Ok = false, Error = $"نماد نامعتبر: {symbol}" };

        string orderType = side.Equals("SHORT", StringComparison.OrdinalIgnoreCase) ? "sell" : "buy";
        leverage = Math.Clamp(leverage, 1, 5);

        // اول همان جفت (مثلاً usdt)، اگر MarginClosed بود و usdt بود یک‌بار rls را امتحان نکن
        // مگر قیمت را به ریال تبدیل کرده باشی — پس اینجا فقط همان جفت را می‌زنیم
        return await PlaceNobitexMarginOrderCoreAsync(
            pair.Value.Src, pair.Value.Dst, orderType, leverage, amountSrc, price, stopLoss, takeProfit);
    }

    static async Task<NobitexOrderResult> PlaceNobitexMarginOrderCoreAsync(
        string src,
        string dst,
        string orderType, // buy / sell
        int leverage,
        decimal amountSrc,
        decimal price,
        decimal stopLoss,
        decimal takeProfit)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;

        // JSON فشرده — عین همین باید امضا شود
        string json =
     "{" +
     $"\"srcCurrency\":\"{src}\"," +
     $"\"dstCurrency\":\"{dst}\"," +
     $"\"type\":\"{orderType}\"," +
     "\"mode\":\"oco\"," +
     $"\"leverage\":\"{leverage.ToString(inv)}\"," +
     $"\"amount\":\"{amountSrc.ToString(inv)}\"," +
     $"\"price\":\"{takeProfit.ToString(inv)}\"," +
     $"\"stopPrice\":\"{stopLoss.ToString(inv)}\"," +
     $"\"stopLimitPrice\":\"{stopLoss.ToString(inv)}\"" +
     "}";

        try
        {
            var (status, raw) = await NobitexSendAsync(HttpMethod.Post, "/margin/orders/add", json);

            if (status == 0 || raw.StartsWith("NOBITEX_", StringComparison.Ordinal))
                return new NobitexOrderResult { Ok = false, Error = raw, Raw = raw };

            if (string.IsNullOrWhiteSpace(raw))
                return new NobitexOrderResult { Ok = false, Error = $"HTTP {status} empty body" };

            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            var root = doc.RootElement;

            if (root.TryGetProperty("status", out var st) && st.GetString() == "ok")
            {
                long? id = null;
                decimal? avg = null;

                if (root.TryGetProperty("order", out var order))
                {
                    if (order.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var oid))
                        id = oid;

                    if (order.TryGetProperty("averagePrice", out var ap) &&
                        decimal.TryParse(ap.GetString(),
                            System.Globalization.NumberStyles.Any, inv, out var apv) &&
                        apv > 0)
                    {
                        avg = apv;
                    }
                }

                return new NobitexOrderResult
                {
                    Ok = true,
                    OrderId = id,
                    FillPrice = avg ?? price,
                    Raw = raw
                };
            }

            string code = root.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
            string msg = root.TryGetProperty("message", out var m) ? m.GetString() ?? raw : raw;

            string friendly = code switch
            {
                "MarginClosed" =>
                    "تعهدی این بازار الان روی نوبیتکس بسته است (MarginClosed). " +
                    "در سایت نوبیتکس بخش تعهدی همان نماد را چک کن یا جفت دیگر (مثلاً RLS) را ببین.",
                "InsufficientBalance" =>
                    "موجودی کیف تعهدی کافی نیست. از اسپات به کیف تعهدی منتقل کن.",
                "InvalidMarketPair" =>
                    "این جفت‌ارز در نوبیتکس معتبر نیست.",
                "UnsupportedMarginSrc" =>
                    "این رمزارز برای تعهدی پشتیبانی نمی‌شود.",
                "SmallOrder" =>
                    "حجم سفارش خیلی کوچک است. مبلغ تومان را بیشتر کن.",
                "LeverageTooHigh" =>
                    "اهرم بالاتر از سقف مجاز این بازار است. اهرم را کم کن.",
                "BadPrice" =>
                    "قیمت سفارش از نظر نوبیتکس نامعتبر است.",
                "ExceedDlegationLimit" =>
                    "از سقف وکالت باقی‌مانده بیشتر شده است.",
                "TradeLimitation" =>
                    "سطح احراز یا محدودیت حساب اجازه معامله نمی‌دهد.",
                "AmountUnavailable" =>
                    "در استخر وکالت، مقدار کافی برای فروش تعهدی نیست.",
                _ => msg
            };

            if (msg.Contains("30 second", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("timestamp", StringComparison.OrdinalIgnoreCase))
            {
                friendly += " | ساعت ویندوز را Sync کن (UTC).";
            }

            return new NobitexOrderResult
            {
                Ok = false,
                Error = $"HTTP {status} | {code} | {friendly}",
                Raw = raw
            };
        }
        catch (Exception ex)
        {
            return new NobitexOrderResult { Ok = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// اختیاری: ببین این نماد در بازار تعهدی هست یا نه
    /// </summary>
    static async Task<string> CheckMarginMarketAsync(string symbol)
    {
        var pair = ParseNobitexPair(symbol);
        if (pair is null) return "نماد نامعتبر";

        var (status, raw) = await NobitexSendAsync(HttpMethod.Get, "/margin/markets/list", null);
        if (status != 200)
            return $"لیست بازارها گرفته نشد: HTTP {status}\n{raw}";

        // کلیدها معمولاً شبیه BTCUSDT یا BTCIRT هستند
        string keyUsdt = $"{pair.Value.Src.ToUpperInvariant()}USDT";
        string keyIrt = $"{pair.Value.Src.ToUpperInvariant()}IRT";

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("markets", out var markets))
                return raw;

            var sb = new StringBuilder();
            foreach (var name in new[] { keyUsdt, keyIrt, symbol.ToUpperInvariant() })
            {
                if (markets.TryGetProperty(name, out var m))
                {
                    sb.AppendLine($"✅ پیدا شد: {name}");
                    sb.AppendLine(m.ToString());
                }
            }

            return sb.Length > 0 ? sb.ToString() : $"در لیست تعهدی چیزی شبیه {keyUsdt}/{keyIrt} دیده نشد.\nبخشی از پاسخ:\n{(raw.Length > 500 ? raw[..500] : raw)}";
        }
        catch
        {
            return raw;
        }
    }

    static bool HasRecentSignal(string symbol, string tradeMode = "MARGIN", string exchangeCode = "NOBITEX")
    {
        // Avoid immediately re-entering the same symbol after a resolved signal.
        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(DatabaseConnectionString);
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT COUNT(1) FROM SignalTracks
WHERE Symbol=$s AND TradeMode=$m AND ExchangeCode=$exchange AND SignalAt >= $cutoff";
            cmd.Parameters.AddWithValue("$s", symbol);
            cmd.Parameters.AddWithValue("$m", tradeMode);
            cmd.Parameters.AddWithValue("$exchange", exchangeCode);
            cmd.Parameters.AddWithValue("$cutoff", DateTime.UtcNow.AddHours(-4).ToString("o"));
            var n = Convert.ToInt32(cmd.ExecuteScalar());
            return n > 0;
        }
        catch
        {
            return false;
        }
    }

    static bool TryGetPayload(string id, out string full)
    {
        if (SignalPayloads.TryGetValue(id, out full!))
        {
            // A single signal is broadcast to many users; consuming it on the
            // first callback would make every other user's button expire.
            return true;
        }
        full = "";
        return false;
    }

    static HttpClient CreateNobitexHttp()
    {
        var handler = new HttpClientHandler { UseProxy = false, Proxy = null };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");

        return http;
    }

    static (string Src, string Dst)? ParseNobitexPair(string symbol)
    {
        symbol = symbol.ToUpperInvariant();
        if (symbol.EndsWith("USDT") && symbol.Length > 4)
            return (symbol[..^4].ToLowerInvariant(), "usdt");
        if (symbol.EndsWith("IRT") && symbol.Length > 3)
            return (symbol[..^3].ToLowerInvariant(), "rls");
        if (symbol.EndsWith("RLS") && symbol.Length > 3)
            return (symbol[..^3].ToLowerInvariant(), "rls");
        return null;
    }
    static HttpClient CreateNobitexPublicHttp()
    {
        var handler = new HttpClientHandler
        {
            UseProxy = false,
            Proxy = null
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        return http;
    }
    static async Task<HashSet<string>> GetNobitexUsdtSymbolsAsync()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var http = CreateNobitexPublicHttp();
            // لیست همه بازارها
            var json = await http.GetStringAsync("https://apiv2.nobitex.ir/v3/orderbook/all");

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var name = prop.Name.ToUpperInvariant();

                // فقط بازارهای تتری نوبیتکس → معادل بایننس ...USDT
                if (name.EndsWith("USDT") && name != "USDT")
                    set.Add(name);

                // اختیاری: بازار ریالی → پایه را به USDT بایننس نگاشت کن
                // مثلا BTCIRT → BTCUSDT
                if (name.EndsWith("IRT") || name.EndsWith("RLS"))
                {
                    var baseCoin = name
                        .Replace("IRT", "")
                        .Replace("RLS", "");
                    if (!string.IsNullOrEmpty(baseCoin) &&
                        baseCoin != "USDT" &&
                        baseCoin != "RLS")
                    {
                        set.Add(baseCoin + "USDT");
                    }
                }
            }

            Console.WriteLine($"Nobitex-related USDT symbols: {set.Count}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Nobitex list failed: {ex.Message}");
            // fallback حداقلی
            set.UnionWith(new[] { "BTCUSDT", "ETHUSDT", "BNBUSDT", "XRPUSDT", "SOLUSDT" });
        }

        return set;
    }

    static long? TrackSignal(string symbol, decimal entry, decimal sl, decimal tp,
        string side = "LONG", string tradeMode = "MARGIN", string exchangeCode = "NOBITEX")
    {
        try
        {
            side = string.IsNullOrWhiteSpace(side) ? "LONG" : side.ToUpperInvariant();

            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(DatabaseConnectionString);
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO SignalTracks
    (Symbol, Side, TradeMode, ExchangeCode, EntryPrice, StopLoss, TakeProfit, Status, SignalAt)
VALUES
    ($s, $side, $mode, $exchange, $e, $sl, $tp, 'Pending', $t);
SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$s", symbol);
            cmd.Parameters.AddWithValue("$side", side);
            cmd.Parameters.AddWithValue("$mode", tradeMode);
            cmd.Parameters.AddWithValue("$exchange", exchangeCode);
            cmd.Parameters.AddWithValue("$e", entry);
            cmd.Parameters.AddWithValue("$sl", sl);
            cmd.Parameters.AddWithValue("$tp", tp);
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
            long id = Convert.ToInt64(cmd.ExecuteScalar());
            LogSignalEvaluation(id, entry, "CREATED", "Signal created and persisted before Telegram delivery.");
            Console.WriteLine($"Signal #{id} tracked: {exchangeCode}/{symbol} {side} entry={entry:N8} SL={sl:N8} TP={tp:N8}");
            return id;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"TrackSignal: {ex.Message}");
            return null;
        }
    }

    static void LogSignalEvaluation(long signalId, decimal? price, string outcome, string? details = null)
    {
        try
        {
            using var conn = new SqliteConnection(DatabaseConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO SignalEvaluationLogs(SignalTrackId, CheckedAt, MarketPrice, Outcome, Details)
VALUES($id, $at, $price, $outcome, $details);";
            cmd.Parameters.AddWithValue("$id", signalId);
            cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$price", price is null ? DBNull.Value : price.Value);
            cmd.Parameters.AddWithValue("$outcome", outcome);
            cmd.Parameters.AddWithValue("$details", details is null ? DBNull.Value : details);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Signal evaluation log #{signalId}: {ex.Message}");
        }
    }

    // ==================== به‌روزرسانی وضعیت ====================

    static async Task ListenTelegramCommands(TelegramBotClient bot, BinanceRestClient restClient)
    {
        int offset = 0;
        while (true)
        {
            try
            {
                var updates = await bot.GetUpdates(offset, timeout: 30);
                foreach (var update in updates)
                {
                    offset = update.Id + 1;

                    if (update.CallbackQuery is { } cb)
                    {
                        long chatId = cb.Message?.Chat.Id ?? cb.From.Id;
                        var callbackData = cb.Data ?? "";
                        if (callbackData == "ui|addaccount" &&
                            !string.Equals(cb.Message?.Chat.Type.ToString(), "Private", StringComparison.OrdinalIgnoreCase))
                        {
                            await bot.AnswerCallbackQuery(cb.Id, "برای امنیت، اتصال حساب فقط در گفت‌وگوی خصوصی ربات امکان‌پذیر است.");
                            continue;
                        }
                        var uiResult = await HandleUiCallback(chatId, callbackData);
                        var answer = uiResult?.Text ?? await HandleCallback(chatId, callbackData, restClient);
                        await bot.AnswerCallbackQuery(cb.Id, answer.Length > 180 ? answer[..180] : answer);
                        if (uiResult is not null)
                            await bot.SendMessage(chatId, answer, replyMarkup: MainReplyKeyboard());
                        else
                            await bot.SendMessage(chatId, answer, replyMarkup: MainReplyKeyboard());
                        continue;
                    }

                    var msg = update.Message;
                    if (msg?.Text is null) continue;

                    long userChatId = msg.Chat.Id;
                    string text = msg.Text.Trim();

                    if (AccountLinkStates.TryGetValue(userChatId, out var accountLink) && !text.StartsWith('/'))
                    {
                        if (accountLink.Step == "label")
                        {
                            if (string.IsNullOrWhiteSpace(text) || text.Length > 80)
                            {
                                await bot.SendMessage(userChatId, "نام حساب باید بین ۱ تا ۸۰ نویسه باشد. دوباره وارد کن یا /cancel بزن.");
                                continue;
                            }
                            accountLink.Label = text;
                            accountLink.Step = "apikey";
                            await bot.SendMessage(userChatId,
                                "اکنون API Key عمومی نوبیتکس را ارسال کن. از پنل نوبیتکس یک API Key با دسترسی READ بساز (برای ثبت سفارش در نسخه آینده، TRADE هم لازم می‌شود). دسترسی WITHDRAW را فعال نکن. پیام کلید پس از دریافت حذف می‌شود.",
                                replyMarkup: MainReplyKeyboard());
                            continue;
                        }

                        if (accountLink.Step == "apikey")
                        {
                            try { await bot.DeleteMessage(userChatId, msg.MessageId); }
                            catch (Exception ex) { Console.WriteLine($"Could not delete credential message for user {userChatId}: {ex.GetType().Name}"); }

                            if (text.Length is < 12 or > 512 || text.Any(char.IsWhiteSpace))
                            {
                                await bot.SendMessage(userChatId, "فرمت API Key معتبر به نظر نمی‌رسد. مقدار را بدون فاصله دوباره ارسال کن یا /cancel بزن.");
                                continue;
                            }
                            accountLink.ApiKey = text;
                            accountLink.Step = "secret";
                            text = string.Empty;
                            await bot.SendMessage(userChatId,
                                "حالا کلید خصوصی (API Secret / Private Key) متناظر با همین API Key را ارسال کن. این کلید فقط برای امضای درخواست‌ها استفاده می‌شود و به‌صورت رمزنگاری‌شده ذخیره خواهد شد. پیام پس از دریافت حذف می‌شود.",
                                replyMarkup: MainReplyKeyboard());
                            continue;
                        }

                        if (accountLink.Step == "secret")
                        {
                            try { await bot.DeleteMessage(userChatId, msg.MessageId); }
                            catch (Exception ex) { Console.WriteLine($"Could not delete private-key message for user {userChatId}: {ex.GetType().Name}"); }

                            if (text.Length is < 32 or > 512 || text.Any(char.IsWhiteSpace))
                            {
                                await bot.SendMessage(userChatId, "فرمت کلید خصوصی معتبر نیست. مقدار کامل Base64/Base64URL را بدون فاصله دوباره ارسال کن یا /cancel بزن.");
                                continue;
                            }

                            try
                            {
                                var credentials = new ExchangeCredentials(accountLink.ApiKey, text);
                                var validation = await NobitexReadOnly.ValidateCredentialsDetailedAsync(credentials);
                                if (!validation.IsValid)
                                {
                                    Console.WriteLine($"Nobitex API-key validation failed for chat {userChatId}: {validation.Message}");
                                    string reason = validation.Message.Length > 700
                                        ? validation.Message[..700]
                                        : validation.Message;
                                    await bot.SendMessage(userChatId,
                                        "❌ اعتبارسنجی نوبیتکس ناموفق بود؛ کلیدها ذخیره نشدند.\n\n" +
                                        $"جزئیات پاسخ: {reason}\n\n" +
                                        "اگر پاسخ HTTP 401/403 است، مجوز READ و محدودیت IP کلید را بررسی کن. اگر خطای شبکه/429 است، مسیر دسترسی سرور به نوبیتکس را بررسی کن. کلید خصوصی را دوباره در چت نفرست مگر پس از رفع علت.");
                                    continue;
                                }

                                var accountRepository = new UserExchangeAccountRepository(DatabaseConnectionString);
                                long accountId;
                                if (accountLink.AccountId is long existingAccountId)
                                {
                                    if (!accountRepository.UpdateCredentials(userChatId, existingAccountId, accountLink.ApiKey, text))
                                        throw new KeyNotFoundException("Active Nobitex account was not found for credential update.");
                                    accountId = existingAccountId;
                                }
                                else
                                {
                                    accountId = accountRepository.Add(userChatId, "NOBITEX", accountLink.Label, accountLink.ApiKey, text);
                                }
                                accountLink.ApiKey = string.Empty;
                                AccountLinkStates.TryRemove(userChatId, out _);
                                await bot.SendMessage(userChatId,
                                    $"✅ کلیدهای API حساب «{accountLink.Label}» با امضای Ed25519 تأیید و ذخیره شدند (شناسه #{accountId}). کلید عمومی و خصوصی به‌صورت رمزنگاری‌شده ذخیره شدند. فعلاً اجرای سفارش واقعی غیرفعال است.",
                                    replyMarkup: MainReplyKeyboard());
                            }
                            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UnauthorizedAccessException or System.Text.Json.JsonException)
                            {
                                await bot.SendMessage(userChatId, "ارتباط با نوبیتکس یا اعتبارسنجی امضای API Key ناموفق بود؛ بعداً دوباره تلاش کن. کلیدها ذخیره نشدند.");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Account linking failed for user {userChatId}: {ex.GetType().Name}");
                                await bot.SendMessage(userChatId, "ثبت حساب انجام نشد. کلیدها ذخیره نشدند؛ تنظیمات رمزنگاری و پایگاه داده میزبان را بررسی کن.");
                            }
                            finally
                            {
                                // Do not retain the submitted private key in the per-user conversation state.
                                text = string.Empty;
                            }
                            continue;
                        }
                    }

                    if (text.Equals("/cancel", StringComparison.OrdinalIgnoreCase))
                    {
                        AccountLinkStates.TryRemove(userChatId, out _);
                        OrderWizards.TryRemove(userChatId, out _);
                        ManualUiStates.TryRemove(userChatId, out _);
                        await bot.SendMessage(userChatId, "فرآیند لغو شد.", replyMarkup: MainReplyKeyboard());
                        continue;
                    }

                    // عضویت
                    if (text.StartsWith("/start"))
                    {
                        RegisterUser(userChatId, msg.From?.Username, msg.From?.FirstName);
                        await bot.SendMessage(userChatId,
                            "خوش آمدید ✅\nاز صفحه‌کلید ثابت پایین برای انتخاب بخش‌ها استفاده کنید. خرید و فروش در این نسخه غیرفعال است.",
                            replyMarkup: MainReplyKeyboard());
                        continue;
                    }

                    // Reply Keyboard sends ordinary text instead of callback queries.
                    // Keep the main navigation in the persistent keyboard, not below messages.
                    string? menuAction = text switch
                    {
                        "📊 تحلیل و گزارش" => "ui|analysismenu",
                        "⚙️ تنظیمات" => "ui|settingsmenu",
                        "💼 حساب‌ها" => "ui|accountsmenu",
                        "📡 منبع سیگنال" => "ui|signalsource",
                        "📑 گزارش سیگنال‌ها" => "ui|report",
                        "📊 آمار سیگنال‌ها" => "ui|stats",
                        "⏸ توقف سیگنال" => "ui|stop",
                        "▶️ فعال‌سازی سیگنال" => "ui|start",
                        "🆘 راهنما" => "ui|help",
                        "🇮🇷 فقط نوبیتکس" => "ui|signalsource|NOBITEX",
                        "🟡 فقط بایننس" => "ui|signalsource|BINANCE",
                        "🔀 هر دو صرافی" => "ui|signalsource|BOTH",
                        "🏠 منوی اصلی" => "ui|home",
                        "↩️ بازگشت" => "ui|settingsmenu",
                        _ => null
                    };
                    if (menuAction is not null)
                    {
                        var menuResult = await HandleUiCallback(userChatId, menuAction);
                        await bot.SendMessage(userChatId, menuResult?.Text ?? "گزینه نامعتبر است.",
                            replyMarkup: MainReplyKeyboard());
                        continue;
                    }

                    if (OrderWizards.TryGetValue(userChatId, out var wizard) && !text.StartsWith('/'))
                    {
                        var (wizardText, wizardKeyboard) = await HandleOrderWizardInput(userChatId, text);
                        await bot.SendMessage(userChatId, wizardText, replyMarkup: MainReplyKeyboard());
                        continue;
                    }

                    if (ManualUiStates.ContainsKey(userChatId) && !text.StartsWith('/'))
                    {
                        var (uiText, uiKeyboard) = await HandleManualUiInput(userChatId, text);
                        await bot.SendMessage(userChatId, uiText, replyMarkup: MainReplyKeyboard());
                        continue;
                    }

                    if (text.Equals("/accounts", StringComparison.OrdinalIgnoreCase))
                    {
                        var accounts = new UserExchangeAccountRepository(DatabaseConnectionString).List(userChatId);
                        string accountText = accounts.Count == 0
                            ? "حسابی متصل نیست. از منوی «حساب‌ها و صرافی‌ها» گزینه «اتصال حساب نوبیتکس» را انتخاب کن."
                            : "حساب‌های صرافی شما:\n" + string.Join("\n", accounts.Select(a =>
                                $"#{a.Id} | {a.ExchangeCode} | {a.AccountLabel} | {(a.IsActive ? "فعال" : "غیرفعال")}"));
                        await bot.SendMessage(userChatId, accountText);
                        continue;
                    }

                    if (text.StartsWith("/account-check", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] commandParts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (commandParts.Length != 2 || !long.TryParse(commandParts[1], out long accountId) || accountId <= 0)
                        {
                            await bot.SendMessage(userChatId, "برای بررسی اتصال، از منوی «حساب‌ها» استفاده کنید.", replyMarkup: MainReplyKeyboard());
                            continue;
                        }

                        var account = new UserExchangeAccountRepository(DatabaseConnectionString).List(userChatId)
                            .FirstOrDefault(x => x.Id == accountId && x.IsActive);
                        if (account is null)
                        {
                            await bot.SendMessage(userChatId, "حساب فعال متعلق به شما پیدا نشد.");
                            continue;
                        }
                        IExchangeAdapter? adapter = account.ExchangeCode.ToUpperInvariant() switch
                        {
                            "NOBITEX" => NobitexReadOnly,
                            "BINANCE_TESTNET" => BinanceTestnetReadOnly,
                            _ => null
                        };
                        if (adapter is null)
                        {
                            await bot.SendMessage(userChatId, "آداپتر read-only این صرافی هنوز پیاده‌سازی نشده است.");
                            continue;
                        }
                        var credentials = new UserExchangeAccountRepository(DatabaseConnectionString).GetCredentials(userChatId, accountId);
                        bool valid = await adapter.ValidateCredentialsAsync(credentials);
                        await bot.SendMessage(userChatId, valid
                            ? $"✅ اتصال حساب {account.AccountLabel} به {adapter.ExchangeCode} معتبر است. فقط دسترسی خواندن بررسی شد؛ معامله فعال نیست."
                            : $"❌ اعتبارسنجی {adapter.ExchangeCode} ناموفق بود؛ کلیدها، مجوز USER_DATA و دسترسی شبکه را بررسی کن.");
                        continue;
                    }

                    if (text.StartsWith("/balances", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] commandParts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (commandParts.Length != 2 || !long.TryParse(commandParts[1], out long accountId) || accountId <= 0)
                        {
                            await bot.SendMessage(userChatId, "برای مشاهده موجودی، از منوی «حساب‌ها» استفاده کنید.", replyMarkup: MainReplyKeyboard());
                            continue;
                        }

                        var account = new UserExchangeAccountRepository(DatabaseConnectionString).List(userChatId)
                            .FirstOrDefault(x => x.Id == accountId && x.IsActive);
                        if (account is null)
                        {
                            await bot.SendMessage(userChatId, "حساب فعال متعلق به شما پیدا نشد.");
                            continue;
                        }
                        IExchangeAdapter? adapter = account.ExchangeCode.ToUpperInvariant() switch
                        {
                            "NOBITEX" => NobitexReadOnly,
                            "BINANCE_TESTNET" => BinanceTestnetReadOnly,
                            _ => null
                        };
                        if (adapter is null)
                        {
                            await bot.SendMessage(userChatId, "خواندن موجودی این صرافی هنوز پیاده‌سازی نشده است.");
                            continue;
                        }
                        var credentials = new UserExchangeAccountRepository(DatabaseConnectionString).GetCredentials(userChatId, accountId);
                        ExchangeAccountSnapshot snapshot;
                        try { snapshot = await adapter.GetAccountSnapshotAsync(credentials); }
                        catch (Exception ex) when (ex is HttpRequestException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
                        {
                            await bot.SendMessage(userChatId, $"دریافت موجودی {adapter.ExchangeCode} ناموفق بود؛ اعتبار کلید و دسترسی شبکه را بررسی کن.");
                            continue;
                        }
                        string balanceText = string.Join("\n", snapshot.Balances.Where(x => x.Total != 0 || x.Locked != 0)
                            .OrderByDescending(x => x.Total).Take(30)
                            .Select(x => $"{x.Asset}: کل {x.Total:G29} | آزاد {x.Available:G29} | مسدود {x.Locked:G29}"));
                        if (string.IsNullOrWhiteSpace(balanceText)) balanceText = "موجودی غیرصفر دریافت نشد.";
                        await bot.SendMessage(userChatId, $"موجودی {account.AccountLabel} (فقط خواندنی):\n{balanceText}");
                        continue;
                    }

                    if (text.StartsWith("/preview", StringComparison.OrdinalIgnoreCase))
                    {
                        await bot.SendMessage(userChatId,
                            "قابلیت خرید، فروش و ساخت پیش‌نویس سفارش فعلاً غیرفعال است. فقط تحلیل و دریافت سیگنال فعال است.",
                            replyMarkup: MainReplyKeyboard());
                        continue;
                    }

                    if (text.StartsWith("/stop"))
                    {
                        SetUserActive(userChatId, false);
                        await bot.SendMessage(userChatId, "سیگنال‌ها برای شما متوقف شد. برای فعال‌سازی دوباره از صفحه‌کلید پایین استفاده کنید.", replyMarkup: MainReplyKeyboard());
                        continue;
                    }

                    if (PendingOrders.TryGetValue(userChatId, out var pending) &&
    pending.BudgetToman == null)
                    {
                        if (text.Equals("/cancel", StringComparison.OrdinalIgnoreCase))
                        {
                            PendingOrders.Remove(userChatId);
                            OrderWizards.TryRemove(userChatId, out _);
                            ManualUiStates.TryRemove(userChatId, out _);
                            await bot.SendMessage(userChatId, "فرآیند لغو شد.", replyMarkup: MainReplyKeyboard());
                            continue;
                        }

                        var raw = text.Replace(",", "").Replace("،", "").Replace(" ", "").Trim();

                        if (!decimal.TryParse(raw,
                                System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out var toman) || toman < 50_000m)
                        {
                            await bot.SendMessage(userChatId,
                                "عدد معتبر نیست.\nمثال: 300000\nحداقل ۵۰٬۰۰۰ تومان\nبرای لغو از دکمه لغو استفاده کنید.",
                                replyMarkup: MainReplyKeyboard());
                            continue;
                        }

                        pending.BudgetToman = toman;
                        decimal approxUsdt = toman / UsdtTomanRate;

                        var kb = new InlineKeyboardMarkup(new[]
                        {
        new[]
        {
            InlineKeyboardButton.WithCallbackData("✅ تأیید ثبت دستی (بدون ارسال)", "confirm_pending"),
            InlineKeyboardButton.WithCallbackData("❌ انصراف", "cancel_pending")
        }
    });

                        await bot.SendMessage(
                            userChatId,
                            $"⚠️ تأیید سفارش تعهدی نوبیتکس\n" +
                            $"{pending.Side} {pending.Symbol}\n" +
                            $"مبلغ: {toman:N0} تومان\n" +
                            $"≈ {approxUsdt:N2} USDT (نرخ {UsdtTomanRate:N0})\n" +
                            $"اهرم: {pending.Leverage}x\n" +
                            $"Entry≈ {pending.Entry:N4}\n" +
                            $"SL: {pending.StopLoss:N4} | TP: {pending.TakeProfit:N4}\n\n" +
                            $"اگر درست است تأیید نهایی را بزن.",
                            replyMarkup: MainReplyKeyboard());

                        continue; // خیلی مهم
                    }

                    var reply = await HandleUserCommand(userChatId, text, restClient);
                    if (!string.IsNullOrEmpty(reply))
                        await bot.SendMessage(userChatId, reply, replyMarkup: MainReplyKeyboard());
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Telegram listen error: {ex.Message}");
                await Task.Delay(5000);
            }
        }
    }

    static ReplyKeyboardMarkup MainReplyKeyboard() => new(new[]
    {
        new KeyboardButton[] { "📊 تحلیل و گزارش", "⚙️ تنظیمات" },
        new KeyboardButton[] { "📡 منبع سیگنال", "📑 گزارش سیگنال‌ها" },
        new KeyboardButton[] { "🇮🇷 فقط نوبیتکس", "🟡 فقط بایننس" },
        new KeyboardButton[] { "🔀 هر دو صرافی", "📊 آمار سیگنال‌ها" },
        new KeyboardButton[] { "⏸ توقف سیگنال", "▶️ فعال‌سازی سیگنال" },
        new KeyboardButton[] { "💼 حساب‌ها", "🆘 راهنما" }
    })
    {
        ResizeKeyboard = true,
        IsPersistent = true,
        InputFieldPlaceholder = "یک بخش را انتخاب کنید"
    };

    static InlineKeyboardMarkup MainMenuKeyboard() => new(new[]
    {
        new[] { InlineKeyboardButton.WithCallbackData("💼 حساب‌ها و صرافی‌ها", "ui|accountsmenu"), InlineKeyboardButton.WithCallbackData("📈 معاملات", "ui|tradesmenu") },
        new[] { InlineKeyboardButton.WithCallbackData("📊 تحلیل و گزارش", "ui|analysismenu"), InlineKeyboardButton.WithCallbackData("⚙️ تنظیمات", "ui|settingsmenu") },
        new[] { InlineKeyboardButton.WithCallbackData("🆘 راهنما", "ui|help") }
    });

    static InlineKeyboardMarkup BackHomeKeyboard(string backCallback) => new(new[]
    {
        new[] { InlineKeyboardButton.WithCallbackData("↩️ بازگشت", backCallback), InlineKeyboardButton.WithCallbackData("🏠 منوی اصلی", "ui|home") }
    });

    static InlineKeyboardMarkup AccountMenuKeyboard() => new(new[]
    {
        new[] { InlineKeyboardButton.WithCallbackData("🧾 حساب‌های من", "ui|accounts"), InlineKeyboardButton.WithCallbackData("💰 مشاهده موجودی", "ui|balances") },
        new[] { InlineKeyboardButton.WithCallbackData("➕ اتصال حساب نوبیتکس", "ui|addaccount") },
        new[] { InlineKeyboardButton.WithCallbackData("🔌 بررسی اتصال", "ui|checkaccounts") },
        new[] { InlineKeyboardButton.WithCallbackData("🏠 منوی اصلی", "ui|home") }
    });

    static InlineKeyboardMarkup TradesMenuKeyboard() => new(new[]
    {
        new[] { InlineKeyboardButton.WithCallbackData("📂 مشاهده معاملات ثبت‌شده", "ui|open") },
        new[] { InlineKeyboardButton.WithCallbackData("🏠 منوی اصلی", "ui|home") }
    });

    static InlineKeyboardMarkup AnalysisMenuKeyboard() => new(new[]
    {
        new[] { InlineKeyboardButton.WithCallbackData("📑 گزارش سیگنال‌ها", "ui|report"), InlineKeyboardButton.WithCallbackData("📊 آمار معاملات", "ui|stats") },
        new[] { InlineKeyboardButton.WithCallbackData("📂 معاملات باز", "ui|open") },
        new[] { InlineKeyboardButton.WithCallbackData("⏸ توقف سیگنال", "ui|stop"), InlineKeyboardButton.WithCallbackData("▶️ فعال‌سازی سیگنال", "ui|start") },
        new[] { InlineKeyboardButton.WithCallbackData("🏠 منوی اصلی", "ui|home") }
    });

    static InlineKeyboardMarkup SettingsMenuKeyboard() => new(new[]
    {
        new[] { InlineKeyboardButton.WithCallbackData("💱 مشاهده نرخ تتر", "ui|rate"), InlineKeyboardButton.WithCallbackData("✏️ تنظیم نرخ تتر", "ui|rateedit") },
        new[] { InlineKeyboardButton.WithCallbackData("📡 منبع سیگنال‌ها", "ui|signalsource") },
        new[] { InlineKeyboardButton.WithCallbackData("🏠 منوی اصلی", "ui|home") }
    });

    static async Task<(string Text, InlineKeyboardMarkup? Keyboard)?> HandleUiCallback(long chatId, string data)
    {
        var p = data.Split('|');
        if (!TradingFeaturesEnabled &&
            ((p.Length == 2 && p[0] is "approve_order" or "reject_order") ||
             (p.Length >= 2 && p[0] == "ui" && p[1] is
                 "sigtrade" or "sigaccount" or "neworder" or "orderaccount" or "mode" or "side" or "type" or
                 "spotoco" or "registerapproved" or "bought" or "buycurrent" or "buymanual" or "buyconfirm" or "cancelpending")))
        {
            return ("قابلیت خرید، فروش و ثبت سفارش فعلاً غیرفعال است. در این نسخه فقط تحلیل، دریافت سیگنال و مشاهده گزارش‌ها فعال است.", TradesMenuKeyboard());
        }
        if (p.Length == 2 && p[0] == "approve_order")
        {
            var repository = new ManualOrderApprovalRepository(DatabaseConnectionString);
            var result = repository.ApproveForManualExecution(chatId, p[1]);
            if (result != ApprovalTransitionResult.Updated)
                return (result switch
                {
                    ApprovalTransitionResult.Expired => "مهلت تأیید تمام شده؛ پیش‌نمایش جدید بساز.",
                    ApprovalTransitionResult.AccountInactive => "حساب صرافی غیرفعال شده است.",
                    ApprovalTransitionResult.NotFound => "درخواست پیدا نشد یا متعلق به شما نیست.",
                    _ => "این درخواست قبلاً تعیین‌تکلیف شده است."
                }, MainMenuKeyboard());

            bool canRegister = repository.CanRegisterAsOpenPosition(chatId, p[1]);
            var keyboard = canRegister
                ? new InlineKeyboardMarkup(new[]
                {
                    new[] { InlineKeyboardButton.WithCallbackData("📝 ثبت پوزیشن پس از اجرای دستی", $"ui|registerapproved|{p[1]}") },
                    new[] { InlineKeyboardButton.WithCallbackData("🏠 منوی اصلی", "ui|home") }
                })
                : MainMenuKeyboard();
            return ("✅ پیش‌نویس تأیید شد؛ هیچ سفارشی به صرافی ارسال نشد. پس از اینکه سفارش را شخصاً در نوبیتکس اجرا کردی، دکمه ثبت پوزیشن را بزن تا حد ضرر و سود برای پایش به دفتر معاملات اضافه شود.", keyboard);
        }
        if (p.Length == 2 && p[0] == "reject_order")
        {
            var result = new ManualOrderApprovalRepository(DatabaseConnectionString).Reject(chatId, p[1]);
            return (result == ApprovalTransitionResult.Updated
                ? "درخواست رد شد. هیچ سفارشی ارسال نشد."
                : "درخواست پیدا نشد یا قبلاً تعیین‌تکلیف شده است.", MainMenuKeyboard());
        }
        if (p.Length < 2 || p[0] != "ui") return null;
        var accounts = new UserExchangeAccountRepository(DatabaseConnectionString).List(chatId);
        switch (p[1])
        {
            case "sigtrade" when p.Length == 4 && (p[3] is "buy" or "sell"):
            {
                if (!SignalPayloads.ContainsKey(p[2]))
                    return ("این پیشنهاد منقضی شده است؛ برای دریافت پیشنهاد تازه منتظر تحلیل بعدی بمان.", MainMenuKeyboard());
                var signalAccounts = accounts.Where(a => a.IsActive &&
                    a.ExchangeCode.Equals("NOBITEX", StringComparison.OrdinalIgnoreCase)).ToList();
                var selectedPayload = SignalPayloads[p[2]].Split('|');
                if (selectedPayload.Length < 8 || !selectedPayload[7].Equals(SignalSourcePreference.Nobitex, StringComparison.OrdinalIgnoreCase))
                    return ("این سیگنال از بایننس است؛ فعلاً فقط ثبت آزمایشی در دفتر مجاز است و مسیر سفارش نوبیتکس برای آن نمایش داده نمی‌شود.", MainMenuKeyboard());
                if (signalAccounts.Count == 0)
                    return ("برای معامله از روی پیشنهاد، ابتدا حساب نوبیتکس را متصل کن. اتصال فعلی فقط خواندنی است و سفارش تا زمان فعال‌سازی صریح اجرا ارسال نمی‌کند.", AccountMenuKeyboard());
                return ($"حساب نوبیتکس برای {(p[3] == "buy" ? "خرید" : "فروش")} را انتخاب کن:",
                    new InlineKeyboardMarkup(signalAccounts.Select(a => new[] { InlineKeyboardButton.WithCallbackData(
                        $"#{a.Id} {a.AccountLabel}", $"ui|sigaccount|{p[2]}|{p[3]}|{a.Id}") })
                        .Append(new[] { InlineKeyboardButton.WithCallbackData("❌ لغو", "ui|cancel") })));
            }
            case "sigaccount" when p.Length == 5 && (p[3] is "buy" or "sell") &&
                long.TryParse(p[4], out var signalAccountId):
            {
                if (!SignalPayloads.TryGetValue(p[2], out var signalPayload))
                    return ("این پیشنهاد منقضی شده است؛ از پیشنهاد جدید استفاده کن.", MainMenuKeyboard());
                var signalParts = signalPayload.Split('|');
                if (signalParts.Length < 8 || !signalParts[7].Equals(SignalSourcePreference.Nobitex, StringComparison.OrdinalIgnoreCase))
                    return ("این مسیر فقط برای سیگنال‌های نوبیتکس است؛ سیگنال بایننس فعلاً فقط قابلیت ثبت آزمایشی در دفتر را دارد.", MainMenuKeyboard());
                var selectedSignalAccount = accounts.FirstOrDefault(a => a.Id == signalAccountId && a.IsActive &&
                    a.ExchangeCode.Equals("NOBITEX", StringComparison.OrdinalIgnoreCase));
                if (selectedSignalAccount is null)
                    return ("حساب فعال نوبیتکس متعلق به شما پیدا نشد.", AccountMenuKeyboard());
                if (signalParts.Length < 7 || !decimal.TryParse(signalParts[2], System.Globalization.NumberStyles.Number,
                        System.Globalization.CultureInfo.InvariantCulture, out var signalEntry) ||
                    !decimal.TryParse(signalParts[3], System.Globalization.NumberStyles.Number,
                        System.Globalization.CultureInfo.InvariantCulture, out var signalSl) ||
                    !decimal.TryParse(signalParts[4], System.Globalization.NumberStyles.Number,
                        System.Globalization.CultureInfo.InvariantCulture, out var signalTp))
                    return ("اطلاعات قیمت پیشنهاد معتبر نیست؛ از پیشنهاد جدید استفاده کن.", MainMenuKeyboard());

                bool isBuy = p[3] == "buy";
                OrderWizards[chatId] = new OrderWizardState
                {
                    AccountId = signalAccountId,
                    Symbol = signalParts[1].ToUpperInvariant(),
                    Mode = ExchangeTradeMode.Spot,
                    Side = isBuy ? ExchangeOrderSide.Buy : ExchangeOrderSide.Sell,
                    Type = ExchangeOrderType.Market,
                    IsSignalTrade = true,
                    SignalEntryPrice = signalEntry,
                    StopLoss = isBuy ? signalSl : null,
                    TakeProfit = isBuy ? signalTp : null,
                    Step = isBuy ? "budget" : "quantity"
                };
                ManualUiStates.TryRemove(chatId, out _);
                return (isBuy
                    ? $"خرید Spot {signalParts[1]} با بودجه USDT\nموجودی تتر از حساب نوبیتکس خوانده می‌شود و مقدار تقریبی با قیمت لحظه‌ای محاسبه خواهد شد. بودجه به USDT را وارد کن (حداقل ۱۱ USDT):"
                    : $"فروش Spot {signalParts[1]}\nموجودی آزاد کوین از حساب نوبیتکس بررسی می‌شود. مقدار {signalParts[1]} برای فروش را وارد کن:", CancelWizardKeyboard());
            }
            case "home":
                OrderWizards.TryRemove(chatId, out _);
                ManualUiStates.TryRemove(chatId, out _);
                AccountLinkStates.TryRemove(chatId, out _);
                return ("منوی اصلی:", MainMenuKeyboard());
            case "accountsmenu": return ("مدیریت حساب‌ها (تمام عملیات صرافی در این بخش فقط خواندنی است):", AccountMenuKeyboard());
            case "tradesmenu": return ("مدیریت معاملات:", TradesMenuKeyboard());
            case "analysismenu": return ("تحلیل، گزارش و وضعیت سیگنال‌ها:", AnalysisMenuKeyboard());
            case "settingsmenu": return ($"تنظیمات ربات | نرخ فعلی تتر: {UsdtTomanRate:N0} تومان", SettingsMenuKeyboard());
            case "signalsource" when p.Length == 3:
            {
                if (!SignalSourcePreference.IsValid(p[2]))
                    return ("منبع انتخاب‌شده معتبر نیست.", SettingsMenuKeyboard());
                bool saved = new UserSignalPreferenceRepository(DatabaseConnectionString).SetSource(chatId, p[2]);
                if (!saved) return ("ابتدا /start را بزن تا حساب کاربری‌ات ثبت شود.", MainMenuKeyboard());
                string label = p[2] switch
                {
                    SignalSourcePreference.Binance => "فقط بایننس",
                    SignalSourcePreference.Both => "هر دو صرافی به‌صورت مستقل",
                    _ => "فقط نوبیتکس"
                };
                return ($"✅ منبع سیگنال روی «{label}» تنظیم شد. از چرخه تحلیل بعدی اعمال می‌شود.", SettingsMenuKeyboard());
            }
            case "signalsource":
            {
                string source = new UserSignalPreferenceRepository(DatabaseConnectionString).GetSource(chatId);
                string label = source switch
                {
                    SignalSourcePreference.Binance => "فقط بایننس",
                    SignalSourcePreference.Both => "هر دو صرافی، مستقل از هم",
                    _ => "فقط نوبیتکس"
                };
                return ($"منبع فعلی سیگنال: {label}\n\nمنبع موردنظر را انتخاب کن:", new InlineKeyboardMarkup(new[]
                {
                    new[] { InlineKeyboardButton.WithCallbackData("🇮🇷 فقط نوبیتکس", "ui|signalsource|NOBITEX") },
                    new[] { InlineKeyboardButton.WithCallbackData("🟡 فقط بایننس", "ui|signalsource|BINANCE") },
                    new[] { InlineKeyboardButton.WithCallbackData("🔀 هر دو (جداگانه)", "ui|signalsource|BOTH") },
                    new[] { InlineKeyboardButton.WithCallbackData("↩️ تنظیمات", "ui|settingsmenu") }
                }));
            }
            case "help":
                return ("راهنمای ربات\n\n• منبع سیگنال را از تنظیمات بین نوبیتکس، بایننس یا هر دو انتخاب کن؛ در حالت هر دو، قیمت و کندل‌ها کاملاً مستقل می‌مانند.\n• سیگنال بایننس با داده عمومی Spot ساخته می‌شود و برای تحلیل به API Key نیاز ندارد. ثبت پوزیشن بایننس در دفتر فقط آزمایشی/دستی است.\n• حساب‌ها، اتصال و موجودی نوبیتکس: فقط خواندنی\n• خرید/فروش: با دکمه‌ها حساب، Spot/Margin، سمت، نوع سفارش، مقدار و قیمت را انتخاب کنید.\n• خرید Spot نوبیتکس: پس از اجرای دستی خرید و ثبت قیمت واقعی، پوزیشن و مقدار آن در دفتر ذخیره می‌شود؛ از فهرست معاملات باز می‌توان درخواست OCO خروج را جداگانه ثبت کرد.\n• OCO Spot موجودی آزاد و قیمت لحظه‌ای را بررسی می‌کند؛ اگر قیمت از محدوده حدها عبور کرده باشد، درخواست رد می‌شود.\n• تأیید سفارش ورود هنوز فقط پیش‌نویس را برای اجرای دستی تأیید می‌کند و سفارش ورود نمی‌فرستد.\n• اجرای واقعی از قفل ایمنی سراسری پیروی می‌کند و در این نسخه غیرفعال است؛ تست‌های فعلی با Mock هستند.\n• برای ورود نماد، مقدار، قیمت و حدها، فقط متن عددی/نماد را در مرحله مربوطه ارسال کنید.", BackHomeKeyboard("ui|home"));
            case "cancel":
                OrderWizards.TryRemove(chatId, out _);
                ManualUiStates.TryRemove(chatId, out _);
                AccountLinkStates.TryRemove(chatId, out _);
                bool hadPending = PendingOrders.Remove(chatId);
                return (hadPending ? "فرآیند و سفارش معلق لغو شد." : "فرآیند ورودی لغو شد. پیش‌نویس‌های سفارش را از دکمه «رد سفارش» همان پیام تعیین‌تکلیف کنید." , TradesMenuKeyboard());
            case "registerapproved" when p.Length == 3:
                OrderWizards.TryRemove(chatId, out _);
                ManualUiStates[chatId] = new ManualUiState { Action = "registerapproved", Step = "entryprice", ApprovalId = p[2] };
                return ("سفارش را واقعاً در صرافی اجرا کرده‌ای؟ فقط در صورت تکمیل خرید/فروش، قیمت میانگین اجرای واقعی را وارد کن تا پوزیشن با حد ضرر و سود در دفتر ثبت شود:", CancelWizardKeyboard());
            case "spotoco" when p.Length == 3 && int.TryParse(p[2], out var spotOcoTradeId):
            {
                var trade = GetOpenTrades(chatId).FirstOrDefault(x => x.Id == spotOcoTradeId);
                if (trade is null || trade.TradeMode != "SPOT" || trade.Side != "BUY" ||
                    trade.ExchangeCode != "NOBITEX" || trade.ExchangeAccountId is null || trade.Quantity <= 0)
                    return ("پوزیشن Spot معتبر برای ثبت OCO پیدا نشد یا مقدار آن ثبت نشده است.", TradesMenuKeyboard());
                var repository = new ManualOrderApprovalRepository(DatabaseConnectionString);
                if (!repository.TryBeginSpotOcoSubmission(chatId, spotOcoTradeId))
                    return ("ثبت OCO برای این پوزیشن قبلاً انجام شده، در حال انجام است یا حساب فعال نیست. برای نتیجه، فهرست معاملات باز را تازه‌سازی کن.", TradesMenuKeyboard());

                try
                {
                    var marketPrice = await GetNobitexLastPriceAsync(trade.Symbol);
                    if (marketPrice is null || marketPrice <= 0)
                    {
                        repository.CompleteSpotOcoSubmission(chatId, spotOcoTradeId, "Rejected",
                            Array.Empty<string>(), "Live market price could not be retrieved; no OCO was sent.");
                        return ("قیمت لحظه‌ای دریافت نشد؛ هیچ OCO ارسال نشد. دوباره تلاش کن.", TradesMenuKeyboard());
                    }

                    var ocoCredentials = new UserExchangeAccountRepository(DatabaseConnectionString)
                        .GetCredentials(chatId, trade.ExchangeAccountId.Value);
                    var result = await NobitexTrading.PlaceSpotOcoExitAsync(ocoCredentials,
                        new NobitexSpotOcoExitRequest(trade.Symbol, trade.Quantity, marketPrice.Value,
                            trade.TakeProfit, trade.StopLoss, trade.StopLoss));
                    string status = result.Accepted ? "Accepted" : result.OutcomeUnknown ? "OutcomeUnknown" : "Rejected";
                    repository.CompleteSpotOcoSubmission(chatId, spotOcoTradeId, status,
                        result.ExchangeOrderIds, result.Message);
                    return (result.Accepted
                        ? $"✅ OCO برای {trade.Symbol} پذیرفته شد. شناسه سفارش‌ها: {string.Join(", ", result.ExchangeOrderIds)}"
                        : result.OutcomeUnknown
                            ? $"⚠️ نتیجه ثبت OCO نامشخص است: {result.Message}\nتا بررسی تاریخچه سفارش‌ها در نوبیتکس، دوباره ارسال نکن."
                            : $"OCO ثبت نشد: {result.Message}", TradesMenuKeyboard());
                }
                catch (Exception ex) when (ex is HttpRequestException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or KeyNotFoundException)
                {
                    repository.CompleteSpotOcoSubmission(chatId, spotOcoTradeId, "Rejected",
                        Array.Empty<string>(), "Pre-submission verification failed.");
                    Console.WriteLine($"Spot OCO verification failed for trade {spotOcoTradeId}: {ex.GetType().Name}");
                    return ("بررسی حساب یا بازار ناموفق بود؛ OCO ارسال نشد. اتصال حساب و دسترسی API را بررسی کن.", TradesMenuKeyboard());
                }
            }
            case "accounts":
                if (accounts.Count == 0) return ("حسابی متصل نیست. از دکمه زیر برای اتصال حساب نوبیتکس استفاده کن.", new InlineKeyboardMarkup(new[]
                    { new[] { InlineKeyboardButton.WithCallbackData("➕ اتصال حساب نوبیتکس", "ui|addaccount") }, new[] { InlineKeyboardButton.WithCallbackData("↩️ بازگشت", "ui|accountsmenu") } }));
                return ("حساب‌های متصل شما:", new InlineKeyboardMarkup(accounts.Select(a => new[]
                    { InlineKeyboardButton.WithCallbackData($"#{a.Id} {a.ExchangeCode} | {a.AccountLabel} {(a.IsActive ? "🟢" : "⚪")}", $"ui|account|{a.Id}") })
                    .Append(new[] { InlineKeyboardButton.WithCallbackData("➕ اتصال حساب", "ui|addaccount") })
                    .Append(new[] { InlineKeyboardButton.WithCallbackData("↩️ منوی اصلی", "ui|home") })));
            case "addaccount":
                if (AccountLinkStates.ContainsKey(chatId)) return ("فرآیند اتصال حساب از قبل فعال است. نام حساب، API Key عمومی و کلید خصوصی را به‌ترتیب در مراحل خواسته‌شده ارسال کن؛ برای لغو /cancel را بفرست.", CancelWizardKeyboard());
                AccountLinkStates[chatId] = new AccountLinkState();
                return ("اتصال حساب نوبیتکس با API Key جدید\n\nابتدا یک نام دلخواه برای این حساب وارد کن (مثلاً حساب اصلی). سپس API Key عمومی و کلید خصوصی متناظر دریافت می‌شود و هر درخواست با Ed25519 امضا خواهد شد. دسترسی READ لازم است؛ برای معاملات آینده TRADE هم لازم می‌شود. هرگز WITHDRAW را فعال نکن. برای لغو /cancel را بفرست.", CancelWizardKeyboard());
            case "relink" when p.Length == 3 && long.TryParse(p[2], out var relinkAccountId):
            {
                if (AccountLinkStates.ContainsKey(chatId))
                    return ("فرآیند اتصال حساب دیگری در حال اجراست؛ ابتدا آن را لغو کن.", CancelWizardKeyboard());
                var legacyAccount = accounts.FirstOrDefault(a => a.Id == relinkAccountId && a.IsActive &&
                    a.ExchangeCode.Equals("NOBITEX", StringComparison.OrdinalIgnoreCase));
                if (legacyAccount is null)
                    return ("حساب فعال نوبیتکس متعلق به شما پیدا نشد.", AccountMenuKeyboard());
                AccountLinkStates[chatId] = new AccountLinkState
                {
                    Step = "apikey",
                    Label = legacyAccount.AccountLabel,
                    AccountId = legacyAccount.Id
                };
                return ($"به‌روزرسانی کلیدهای حساب «{legacyAccount.AccountLabel}»\n\nAPI Key عمومی جدید را ارسال کن. سپس کلید خصوصی Ed25519 متناظر را می‌گیرم و قبل از ذخیره اعتبارسنجی می‌کنم. کلیدهای قبلی فقط پس از موفقیت اعتبارسنجی جایگزین می‌شوند.", CancelWizardKeyboard());
            }
            case "account":
            {
                if (p.Length != 3 || !long.TryParse(p[2], out long accountMenuId)) return ("شناسه حساب نامعتبر است.", MainMenuKeyboard());
                var selected = accounts.FirstOrDefault(a => a.Id == accountMenuId && a.IsActive);
                if (selected is null) return ("حساب فعال متعلق به شما پیدا نشد.", MainMenuKeyboard());
                var accountRows = new List<InlineKeyboardButton[]>
                {
                    new[] { InlineKeyboardButton.WithCallbackData("🔌 بررسی اتصال", $"ui|check|{accountMenuId}"), InlineKeyboardButton.WithCallbackData("💰 مشاهده موجودی", $"ui|balance|{accountMenuId}") }
                };
                if (selected.ExchangeCode.Equals("NOBITEX", StringComparison.OrdinalIgnoreCase))
                    accountRows.Add(new[] { InlineKeyboardButton.WithCallbackData("🔑 به‌روزرسانی API Key و Secret", $"ui|relink|{accountMenuId}") });
                accountRows.Add(new[] { InlineKeyboardButton.WithCallbackData("⛔ غیرفعال‌کردن حساب", $"ui|deactivate|{accountMenuId}") });
                accountRows.Add(new[] { InlineKeyboardButton.WithCallbackData("↩️ حساب‌ها", "ui|accounts"), InlineKeyboardButton.WithCallbackData("🏠 منو", "ui|home") });
                return ($"حساب #{selected.Id}\n{selected.ExchangeCode} | {selected.AccountLabel}", new InlineKeyboardMarkup(accountRows));
            }
            case "deactivate":
                if (p.Length != 3 || !long.TryParse(p[2], out long deactivateId)) return ("شناسه حساب نامعتبر است.", AccountMenuKeyboard());
                bool deactivated = new UserExchangeAccountRepository(DatabaseConnectionString).Deactivate(chatId, deactivateId);
                return (deactivated ? "حساب غیرفعال شد. کلیدهای رمزنگاری‌شده در پایگاه داده باقی می‌مانند اما برای عملیات حساب قابل دریافت نیستند." : "حساب فعال متعلق به شما پیدا نشد.", AccountMenuKeyboard());
            case "check":
            case "balance":
                if (p.Length != 3 || !long.TryParse(p[2], out long selectedAccountId)) return ("شناسه حساب نامعتبر است.", MainMenuKeyboard());
                var account = accounts.FirstOrDefault(a => a.Id == selectedAccountId && a.IsActive);
                if (account is null) return ("حساب فعال متعلق به شما پیدا نشد.", MainMenuKeyboard());
                IExchangeAdapter? adapter = account.ExchangeCode.ToUpperInvariant() switch
                {
                    "NOBITEX" => NobitexReadOnly,
                    "BINANCE_TESTNET" => BinanceTestnetReadOnly,
                    _ => null
                };
                if (adapter is null) return ("آداپتر این صرافی موجود نیست.", MainMenuKeyboard());
                var credentials = new UserExchangeAccountRepository(DatabaseConnectionString).GetCredentials(chatId, selectedAccountId);
                try
                {
                    if (p[1] == "check")
                    {
                        bool valid = await adapter.ValidateCredentialsAsync(credentials);
                        return (valid ? $"✅ اتصال {account.AccountLabel} معتبر است (فقط خواندنی)." : "❌ اعتبارسنجی اتصال ناموفق بود.",
                            new InlineKeyboardMarkup(new[] { new[] { InlineKeyboardButton.WithCallbackData("↩️ بازگشت", $"ui|account|{selectedAccountId}") } }));
                    }
                    var snapshot = await adapter.GetAccountSnapshotAsync(credentials);
                    string balances = string.Join("\n", snapshot.Balances.Where(x => x.Total != 0 || x.Locked != 0)
                        .OrderByDescending(x => x.Total).Take(30)
                        .Select(x => $"{x.Asset}: کل {x.Total:G29} | آزاد {x.Available:G29} | مسدود {x.Locked:G29}"));
                    if (string.IsNullOrWhiteSpace(balances)) balances = "موجودی غیرصفر دریافت نشد.";
                    return ($"💰 موجودی {account.AccountLabel} (فقط خواندنی):\n{balances}",
                        new InlineKeyboardMarkup(new[] { new[] { InlineKeyboardButton.WithCallbackData("↩️ بازگشت", $"ui|account|{selectedAccountId}") } }));
                }
                catch (Exception ex) when (ex is HttpRequestException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
                { return ("ارتباط با نوبیتکس ناموفق بود؛ API Key، کلید خصوصی متناظر، مجوز READ و ساعت سیستم را بررسی کنید.", MainMenuKeyboard()); }
            case "balances" or "checkaccounts":
                var active = accounts.Where(a => a.IsActive).ToList();
                if (active.Count == 0) return ("حساب فعال ندارید.", MainMenuKeyboard());
                return (p[1] == "balances" ? "برای مشاهده موجودی، حساب را انتخاب کنید:" : "برای بررسی اتصال، حساب را انتخاب کنید:",
                    new InlineKeyboardMarkup(active.Select(a => new[] { InlineKeyboardButton.WithCallbackData(
                        $"{a.ExchangeCode} | {a.AccountLabel}", $"ui|{(p[1] == "balances" ? "balance" : "check")}|{a.Id}") })
                        .Append(new[] { InlineKeyboardButton.WithCallbackData("↩️ منو", "ui|home") })));
            case "neworder":
                ManualUiStates.TryRemove(chatId, out _);
                var nobitexAccounts = accounts.Where(a => a.IsActive && a.ExchangeCode.Equals("NOBITEX", StringComparison.OrdinalIgnoreCase)).ToList();
                if (nobitexAccounts.Count == 0) return ("برای ثبت سفارش ابتدا یک حساب فعال نوبیتکس اضافه کنید. Binance فقط خواندنی است.", MainMenuKeyboard());
                return ("حساب نوبیتکس را انتخاب کنید:", new InlineKeyboardMarkup(nobitexAccounts.Select(a => new[]
                    { InlineKeyboardButton.WithCallbackData($"#{a.Id} {a.AccountLabel}", $"ui|orderaccount|{a.Id}") })
                    .Append(new[] { InlineKeyboardButton.WithCallbackData("❌ لغو", "ui|cancel") })));
            case "orderaccount":
                if (p.Length != 3 || !long.TryParse(p[2], out long orderAccountId)) return ("شناسه حساب نامعتبر است.", MainMenuKeyboard());
                if (!accounts.Any(a => a.Id == orderAccountId && a.IsActive && a.ExchangeCode.Equals("NOBITEX", StringComparison.OrdinalIgnoreCase)))
                    return ("حساب نوبیتکس معتبر نیست.", MainMenuKeyboard());
                ManualUiStates.TryRemove(chatId, out _);
                OrderWizards[chatId] = new OrderWizardState { AccountId = orderAccountId };
                return ("نوع معامله را انتخاب کنید:", new InlineKeyboardMarkup(new[]
                {
                    new[] { InlineKeyboardButton.WithCallbackData("🟦 Spot", "ui|mode|spot"), InlineKeyboardButton.WithCallbackData("🟧 Margin / تعهدی", "ui|mode|margin") },
                    new[] { InlineKeyboardButton.WithCallbackData("❌ لغو", "ui|cancel") }
                }));
            case "mode" when p.Length == 3 && OrderWizards.TryGetValue(chatId, out var modeWizard):
                if (p[2] == "spot") modeWizard.Mode = ExchangeTradeMode.Spot;
                else if (p[2] == "margin") modeWizard.Mode = ExchangeTradeMode.Margin;
                else return ("نوع معامله نامعتبر است.", MainMenuKeyboard());
                return ("جهت معامله را انتخاب کنید:", new InlineKeyboardMarkup(new[]
                {
                    new[] { InlineKeyboardButton.WithCallbackData("🟢 خرید / Long", "ui|side|buy"), InlineKeyboardButton.WithCallbackData("🔴 فروش / Short", "ui|side|sell") },
                    new[] { InlineKeyboardButton.WithCallbackData("❌ لغو", "ui|cancel") }
                }));
            case "side" when p.Length == 3 && OrderWizards.TryGetValue(chatId, out var sideWizard):
                sideWizard.Side = p[2] == "buy" ? ExchangeOrderSide.Buy : p[2] == "sell" ? ExchangeOrderSide.Sell : (ExchangeOrderSide)(-1);
                if (!Enum.IsDefined(sideWizard.Side)) return ("جهت نامعتبر است.", MainMenuKeyboard());
                if (sideWizard.Mode == ExchangeTradeMode.Margin && sideWizard.Side == ExchangeOrderSide.Sell)
                    sideWizard.Side = ExchangeOrderSide.Sell; // short
                return ("نوع سفارش را انتخاب کنید:", new InlineKeyboardMarkup(new[]
                {
                    new[] { InlineKeyboardButton.WithCallbackData("📌 Limit", "ui|type|limit"),
                        sideWizard.Mode == ExchangeTradeMode.Spot ? InlineKeyboardButton.WithCallbackData("⚡ Market", "ui|type|market") : InlineKeyboardButton.WithCallbackData("🚫", "ui|noop") },
                    new[] { InlineKeyboardButton.WithCallbackData("❌ لغو", "ui|cancel") }
                }));
            case "type" when p.Length == 3 && OrderWizards.TryGetValue(chatId, out var typeWizard):
                if (p[2] == "limit") typeWizard.Type = ExchangeOrderType.Limit;
                else if (p[2] == "market" && typeWizard.Mode == ExchangeTradeMode.Spot) typeWizard.Type = ExchangeOrderType.Market;
                else return ("نوع سفارش نامعتبر است.", MainMenuKeyboard());
                typeWizard.Step = "symbol";
                return ("نماد را ارسال کنید؛ مثال: BTCUSDT یا BTCIRT", CancelWizardKeyboard());
            case "noop": return ("این گزینه در این نوع معامله پشتیبانی نمی‌شود.", MainMenuKeyboard());
            case "open":
                var open = GetOpenTrades(chatId);
                if (open.Count == 0) return ("معامله بازی در دفتر شما وجود ندارد.", TradesMenuKeyboard());
                string openText = string.Join("\n", open.Select(trade =>
                    $"{trade.Side} {trade.Symbol} | {trade.TradeMode} | Entry: {trade.EntryPrice:N4} | SL: {trade.StopLoss:N4} | TP: {trade.TakeProfit:N4}"));
                return ($"معاملات ثبت‌شده (فقط مشاهده):\n{openText}\n\nثبت یا بستن معامله در این نسخه غیرفعال است.",
                    BackHomeKeyboard("ui|tradesmenu"));
            case "stats": return (GetPerformanceStats(chatId), AnalysisMenuKeyboard());
            case "report": return (GetSignalReport(), AnalysisMenuKeyboard());
            case "rate": return ($"نرخ فعلی تتر: {UsdtTomanRate:N0} تومان", SettingsMenuKeyboard());
            case "rateedit":
                OrderWizards.TryRemove(chatId, out _);
                ManualUiStates[chatId] = new ManualUiState { Action = "rate", Step = "value" };
                return ($"نرخ جدید تتر به تومان را وارد کنید. نرخ فعلی: {UsdtTomanRate:N0}", CancelWizardKeyboard());
            case "bought":
                OrderWizards.TryRemove(chatId, out _);
                ManualUiStates[chatId] = new ManualUiState { Action = "bought", Step = "symbol" };
                return ("نماد خریدی که قبلاً در صرافی انجام داده‌ای وارد کن؛ مثال: BTC یا BTCUSDT. این عملیات فقط دفتر معاملات را ثبت می‌کند.", CancelWizardKeyboard());
            case "buycurrent" when ManualUiStates.TryGetValue(chatId, out var buyCurrent) && buyCurrent.Action == "bought":
                var buyPrice = await GetNobitexLastPriceAsync(buyCurrent.Symbol);
                if (buyPrice is null || buyPrice <= 0) return ("قیمت فعلی دریافت نشد؛ می‌توانی قیمت را دستی وارد کنی.", new InlineKeyboardMarkup(new[] { new[] { InlineKeyboardButton.WithCallbackData("✏️ ورود قیمت دستی", "ui|buymanual"), InlineKeyboardButton.WithCallbackData("❌ لغو", "ui|cancel") } }));
                buyCurrent.EntryPrice = buyPrice.Value;
                return (BuildManualBuyPreview(buyCurrent), ManualBuyApprovalKeyboard());
            case "buymanual" when ManualUiStates.TryGetValue(chatId, out var buyManual) && buyManual.Action == "bought":
                buyManual.Step = "price";
                return ($"قیمت خرید {buyManual.Symbol} را وارد کن:", CancelWizardKeyboard());
            case "cancelpending":
                bool removedPending = PendingOrders.Remove(chatId);
                return (removedPending ? "سفارش معلق لغو شد؛ هیچ سفارشی ارسال نشد." : "سفارش معلقی برای لغو وجود ندارد.", TradesMenuKeyboard());
            case "buyconfirm" when ManualUiStates.TryGetValue(chatId, out var buyConfirm) && buyConfirm.Action == "bought":
                if (buyConfirm.EntryPrice <= 0) return ("قیمت خرید معتبر نیست؛ فرآیند را از ابتدا شروع کنید.", TradesMenuKeyboard());
                if (HasOpenTrade(chatId, buyConfirm.Symbol, "SPOT")) return (($"قبلاً معامله باز روی {buyConfirm.Symbol} داری."), TradesMenuKeyboard());
                OpenTrade(chatId, buyConfirm.Symbol, buyConfirm.EntryPrice, buyConfirm.EntryPrice * 0.985m,
                    buyConfirm.EntryPrice * 1.03m, "BUY", 1, "SPOT");
                ManualUiStates.TryRemove(chatId, out _);
                return (($"✅ خرید {buyConfirm.Symbol} در دفتر ثبت شد.\nقیمت ورود: {buyConfirm.EntryPrice:N4}\nهیچ سفارشی به صرافی ارسال نشد."), TradesMenuKeyboard());
            case "stop": SetUserActive(chatId, false); return ("سیگنال‌ها متوقف شدند.", MainMenuKeyboard());
            case "start": SetUserActive(chatId, true); return ("سیگنال‌ها فعال شدند.", MainMenuKeyboard());
            default: return ("گزینه نامعتبر است.", MainMenuKeyboard());
        }
    }

    static InlineKeyboardMarkup CancelWizardKeyboard() => new(new[]
    {
        new[] { InlineKeyboardButton.WithCallbackData("❌ لغو", "ui|cancel"), InlineKeyboardButton.WithCallbackData("🏠 منو", "ui|home") }
    });

    static InlineKeyboardMarkup ManualBuyApprovalKeyboard() => new(new[]
    {
        new[] { InlineKeyboardButton.WithCallbackData("✅ ثبت خرید در دفتر", "ui|buyconfirm"), InlineKeyboardButton.WithCallbackData("❌ لغو", "ui|cancel") }
    });

    static string BuildManualBuyPreview(ManualUiState state) =>
        $"پیش‌نمایش ثبت خرید در دفتر\nنماد: {state.Symbol}\nقیمت ورود: {state.EntryPrice:N4}\n" +
        $"SL اولیه: {state.EntryPrice * 0.985m:N4}\nTP اولیه: {state.EntryPrice * 1.03m:N4}\n\n" +
        "این فقط ثبت سابقه معامله است و هیچ سفارش خریدی به صرافی ارسال نمی‌کند.";

    static async Task<(string Text, InlineKeyboardMarkup? Keyboard)> HandleOrderWizardInput(long chatId, string input)
    {
        if (!OrderWizards.TryGetValue(chatId, out var state)) return ("فرآیند سفارش فعال نیست.", MainMenuKeyboard());
        string value = NormalizeDigits(input.Trim());
        bool TryNumber(out decimal number) => decimal.TryParse(value.Replace(",", "").Replace("،", ""),
            System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out number) && number > 0;
        switch (state.Step)
        {
            case "budget" when state.IsSignalTrade && state.Mode == ExchangeTradeMode.Spot && state.Side == ExchangeOrderSide.Buy:
            {
                if (!TryNumber(out var budgetUsdt) || budgetUsdt < 11m)
                    return ("بودجه باید حداقل ۱۱ USDT و عددی مثبت باشد.", CancelWizardKeyboard());
                try
                {
                    var credentials = new UserExchangeAccountRepository(DatabaseConnectionString)
                        .GetCredentials(chatId, state.AccountId);
                    var snapshot = await NobitexReadOnly.GetAccountSnapshotAsync(credentials);
                    var usdt = snapshot.Balances.FirstOrDefault(x => x.Asset.Equals("USDT", StringComparison.OrdinalIgnoreCase));
                    if (!snapshot.Authenticated || usdt is null || budgetUsdt > usdt.Available)
                        return ($"موجودی آزاد USDT کافی نیست. موجودی آزاد فعلی: {usdt?.Available ?? 0m:N8} USDT", CancelWizardKeyboard());

                    var marketPrice = await GetNobitexLastPriceAsync(state.Symbol);
                    if (marketPrice is null || marketPrice <= 0)
                        return ("قیمت لحظه‌ای نوبیتکس دریافت نشد؛ هیچ پیش‌نویسی ساخته نشد. دوباره تلاش کن.", CancelWizardKeyboard());
                    if (state.SignalEntryPrice <= 0 || state.StopLoss is not > 0 || state.TakeProfit is not > 0)
                        return ("حد ضرر/سود پیشنهاد معتبر نیست؛ پیش‌نویس ساخته نشد.", CancelWizardKeyboard());

                    decimal riskPercent = (state.SignalEntryPrice - state.StopLoss.Value) / state.SignalEntryPrice;
                    decimal rewardPercent = (state.TakeProfit.Value - state.SignalEntryPrice) / state.SignalEntryPrice;
                    if (riskPercent <= 0 || rewardPercent <= 0 || riskPercent > 0.10m || rewardPercent < riskPercent * 1.2m)
                        return ("نسبت ریسک/بازده پیشنهاد معتبر نیست؛ پیش‌نویس ساخته نشد.", CancelWizardKeyboard());

                    // Nobitex expects the base-asset quantity, not the USDT budget.
                    // Keep a small reserve for fees/slippage and always round quantity down.
                    decimal signalBuyGuard = marketPrice.Value * (1m + Math.Min(0.005m, rewardPercent / 2m));
                    decimal computedQuantity = Math.Floor((budgetUsdt / signalBuyGuard * 0.995m) * 100_000_000m) / 100_000_000m;
                    if (computedQuantity <= 0 || computedQuantity * marketPrice.Value < 11m)
                        return ("ارزش سفارش پس از ذخیره کارمزد/لغزش کمتر از حداقل ۱۱ USDT است.", CancelWizardKeyboard());

                    state.Quantity = computedQuantity;
                    state.RequestedBudgetUsdt = budgetUsdt;
                    state.PriceGuard = signalBuyGuard;
                    state.StopLoss = marketPrice.Value * (1m - riskPercent);
                    state.TakeProfit = marketPrice.Value * (1m + rewardPercent);
                    return await CreateWizardApproval(chatId, state);
                }
                catch (Exception ex) when (ex is HttpRequestException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or KeyNotFoundException)
                {
                    Console.WriteLine($"Signal Spot buy preflight failed for {state.Symbol}: {ex.GetType().Name}");
                    return ("دریافت موجودی یا قیمت نوبیتکس ناموفق بود؛ هیچ سفارشی ارسال نشد. اتصال حساب و دسترسی API را بررسی کن.", CancelWizardKeyboard());
                }
            }
            case "quantity" when state.IsSignalTrade && state.Mode == ExchangeTradeMode.Spot && state.Side == ExchangeOrderSide.Sell:
            {
                if (!TryNumber(out var sellQuantity))
                    return ("مقدار فروش باید عددی مثبت باشد.", CancelWizardKeyboard());
                try
                {
                    var credentials = new UserExchangeAccountRepository(DatabaseConnectionString)
                        .GetCredentials(chatId, state.AccountId);
                    var snapshot = await NobitexReadOnly.GetAccountSnapshotAsync(credentials);
                    string baseAsset = state.Symbol[..^4];
                    var asset = snapshot.Balances.FirstOrDefault(x => x.Asset.Equals(baseAsset, StringComparison.OrdinalIgnoreCase));
                    if (!snapshot.Authenticated || asset is null || sellQuantity > asset.Available)
                        return ($"موجودی آزاد {baseAsset} کافی نیست. موجودی آزاد: {asset?.Available ?? 0m:G29}", CancelWizardKeyboard());
                    var marketPrice = await GetNobitexLastPriceAsync(state.Symbol);
                    if (marketPrice is null || marketPrice <= 0)
                        return ("قیمت لحظه‌ای نوبیتکس دریافت نشد؛ هیچ پیش‌نویسی ساخته نشد.", CancelWizardKeyboard());
                    if (sellQuantity * marketPrice.Value < 11m)
                        return ("ارزش سفارش فروش باید حداقل ۱۱ USDT باشد.", CancelWizardKeyboard());
                    state.Quantity = sellQuantity;
                    state.PriceGuard = marketPrice.Value * 0.995m;
                    return await CreateWizardApproval(chatId, state);
                }
                catch (Exception ex) when (ex is HttpRequestException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or KeyNotFoundException)
                {
                    Console.WriteLine($"Signal Spot sell preflight failed for {state.Symbol}: {ex.GetType().Name}");
                    return ("دریافت موجودی یا قیمت نوبیتکس ناموفق بود؛ هیچ سفارشی ساخته نشد.", CancelWizardKeyboard());
                }
            }
            case "symbol":
                string symbol = value.Replace("/", "").Replace("-", "").ToUpperInvariant();
                if (symbol.Length is < 6 or > 20 || !symbol.All(char.IsAsciiLetter)) return ("نماد معتبر نیست. نمونه: BTCUSDT", CancelWizardKeyboard());
                state.Symbol = symbol; state.Step = "quantity";
                return ("مقدار سفارش را وارد کنید؛ مثال: 0.001", CancelWizardKeyboard());
            case "quantity":
                if (!TryNumber(out var quantity)) return ("مقدار باید عددی مثبت باشد.", CancelWizardKeyboard());
                state.Quantity = quantity;
                if (state.Mode == ExchangeTradeMode.Margin) { state.Step = "leverage"; return ("اهرم را وارد کنید؛ مثال: 2 یا 1.5", CancelWizardKeyboard()); }
                state.Step = state.Type == ExchangeOrderType.Limit ? "price" : "guard";
                return (state.Type == ExchangeOrderType.Limit ? "قیمت Limit را وارد کنید:" : "Price Guard را برای محدود کردن لغزش قیمت وارد کنید:", CancelWizardKeyboard());
            case "leverage":
                if (!TryNumber(out var leverage) || leverage < 1 || leverage > 20 || leverage * 2m % 1m != 0)
                    return ("اهرم باید بین ۱ تا ۲۰ و با گام ۰٫۵ باشد.", CancelWizardKeyboard());
                state.Leverage = leverage; state.Step = "price";
                return ("قیمت ورود Limit را وارد کنید:", CancelWizardKeyboard());
            case "price":
                if (!TryNumber(out var price)) return ("قیمت باید مثبت باشد.", CancelWizardKeyboard());
                state.LimitPrice = price;
                if (state.Mode == ExchangeTradeMode.Margin ||
                    (state.Mode == ExchangeTradeMode.Spot && state.Side == ExchangeOrderSide.Buy))
                { state.Step = "sl"; return ("قیمت حد ضرر (Stop Loss) را وارد کنید:", CancelWizardKeyboard()); }
                return await CreateWizardApproval(chatId, state);
            case "guard":
                if (!TryNumber(out var guard)) return ("Price Guard باید مثبت باشد.", CancelWizardKeyboard());
                state.PriceGuard = guard;
                if (state.Side == ExchangeOrderSide.Buy)
                { state.Step = "sl"; return ("قیمت حد ضرر (Stop Loss) را وارد کنید:", CancelWizardKeyboard()); }
                return await CreateWizardApproval(chatId, state);
            case "sl":
                if (!TryNumber(out var sl)) return ("Stop Loss باید مثبت باشد.", CancelWizardKeyboard());
                state.StopLoss = sl; state.Step = "tp";
                return ("قیمت Take Profit را وارد کنید:", CancelWizardKeyboard());
            case "tp":
                if (!TryNumber(out var tp)) return ("Take Profit باید مثبت باشد.", CancelWizardKeyboard());
                state.TakeProfit = tp;
                return await CreateWizardApproval(chatId, state);
            default: return ("مرحله نامعتبر است؛ فرآیند را از ابتدا آغاز کنید.", MainMenuKeyboard());
        }
    }

    static string NormalizeDigits(string value)
    {
        return value.Replace('۰', '0').Replace('۱', '1').Replace('۲', '2').Replace('۳', '3')
            .Replace('۴', '4').Replace('۵', '5').Replace('۶', '6').Replace('۷', '7')
            .Replace('۸', '8').Replace('۹', '9').Replace('٠', '0').Replace('١', '1')
            .Replace('٢', '2').Replace('٣', '3').Replace('٤', '4').Replace('٥', '5')
            .Replace('٦', '6').Replace('٧', '7').Replace('٨', '8').Replace('٩', '9');
    }

    static async Task<(string Text, InlineKeyboardMarkup? Keyboard)> HandleManualUiInput(long chatId, string input)
    {
        if (!ManualUiStates.TryGetValue(chatId, out var state)) return ("فرآیند فعالی وجود ندارد.", MainMenuKeyboard());
        string value = NormalizeDigits(input.Trim()).Replace(",", "").Replace("،", "");
        if (state.Action == "registerapproved" && state.Step == "entryprice")
        {
            if (!decimal.TryParse(value, System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out var actualEntry) || actualEntry <= 0)
                return ("قیمت اجرای واقعی باید عددی مثبت باشد.", CancelWizardKeyboard());

            bool registered = state.ApprovalId is not null &&
                new ManualOrderApprovalRepository(DatabaseConnectionString)
                    .RegisterAsOpenPosition(chatId, state.ApprovalId, actualEntry);
            ManualUiStates.TryRemove(chatId, out _);
            return (registered
                ? $"✅ پوزیشن {state.ApprovalId![..8]} در دفتر معاملات شما ثبت شد. قیمت ورود واقعی: {actualEntry:N4}. پایش حد ضرر/سود فعال است؛ خروج همچنان باید در صرافی دستی انجام شود."
                : "ثبت پوزیشن انجام نشد؛ ممکن است درخواست متعلق به شما نباشد، حساب غیرفعال شده یا قبلاً ثبت شده باشد.", TradesMenuKeyboard());
        }
        if (state.Action == "rate")
        {
            if (!decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var rate) || rate < 1000)
                return ("نرخ معتبر نیست؛ عددی حداقل ۱۰۰۰ تومان وارد کن.", CancelWizardKeyboard());
            UsdtTomanRate = rate;
            ManualUiStates.TryRemove(chatId, out _);
            return (($"✅ نرخ تتر به {UsdtTomanRate:N0} تومان تغییر کرد."), SettingsMenuKeyboard());
        }
        if (state.Action == "bought" && state.Step == "symbol")
        {
            string symbol = value.Replace("/", "").Replace("-", "").ToUpperInvariant();
            if (symbol.Length is < 3 or > 20 || !symbol.All(char.IsAsciiLetter))
                return ("نماد معتبر نیست؛ مثال BTC یا BTCUSDT.", CancelWizardKeyboard());
            if (!symbol.EndsWith("USDT", StringComparison.Ordinal)) symbol += "USDT";
            state.Symbol = symbol;
            return ($"نماد {symbol} انتخاب شد. قیمت مرجع را از بازار بگیرم یا دستی وارد می‌کنی؟", new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("💹 استفاده از قیمت فعلی", "ui|buycurrent"), InlineKeyboardButton.WithCallbackData("✏️ ورود قیمت دستی", "ui|buymanual") },
                new[] { InlineKeyboardButton.WithCallbackData("❌ لغو", "ui|cancel") }
            }));
        }
        if (state.Action == "bought" && state.Step == "price")
        {
            if (!decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var entry) || entry <= 0)
                return ("قیمت باید عددی مثبت باشد.", CancelWizardKeyboard());
            state.EntryPrice = entry;
            return (BuildManualBuyPreview(state), ManualBuyApprovalKeyboard());
        }
        return ("مرحله ورودی نامعتبر است؛ فرآیند را لغو و دوباره شروع کن.", CancelWizardKeyboard());
    }

    static Task<(string Text, InlineKeyboardMarkup? Keyboard)> CreateWizardApproval(long chatId, OrderWizardState state)
    {
        try
        {
            var repository = new ManualOrderApprovalRepository(DatabaseConnectionString);
            string approvalId = repository.Create(new ManualOrderDraft(chatId, state.AccountId, "NOBITEX", state.Symbol,
                state.Mode, state.Side, state.Type, state.Quantity, state.LimitPrice,
                state.StopLoss, state.TakeProfit, state.Leverage, state.PriceGuard));
            OrderWizards.TryRemove(chatId, out _);
            string side = state.Mode == ExchangeTradeMode.Spot ? state.Side.ToString().ToUpperInvariant() :
                state.Side == ExchangeOrderSide.Buy ? "LONG" : "SHORT";
            string preview = $"⚠️ پیش‌نمایش سفارش نوبیتکس (۵ دقیقه)\nحساب: #{state.AccountId}\n{state.Mode} {side} {state.Symbol}\n" +
                $"نوع: {state.Type} | مقدار: {state.Quantity}\n" +
                (state.RequestedBudgetUsdt is decimal budget ? $"بودجه درخواستی: {budget:N4} USDT\n" : "") +
                (state.LimitPrice is null ? "" : $"قیمت ورود: {state.LimitPrice}\n") +
                (state.PriceGuard is null ? "" : $"Price Guard: {state.PriceGuard}\n") +
                (state.StopLoss is not null && state.TakeProfit is not null
                    ? (state.Mode == ExchangeTradeMode.Margin ? $"اهرم: {state.Leverage}x\n" : "") +
                      $"حد ضرر: {state.StopLoss} | حد سود: {state.TakeProfit}\n" : "") +
                (state.Mode == ExchangeTradeMode.Spot && state.Side == ExchangeOrderSide.Buy
                    ? "پس از اجرای دستی خرید و ثبت قیمت/مقدار واقعی پوزیشن، می‌توانی OCO خروج را از فهرست معاملات باز به نوبیتکس ارسال کنی.\n" : "") +
                "\nتأیید این مرحله فقط پیش‌نویس را تأیید می‌کند؛ ارسال سفارش واقعی هنوز غیرفعال است.";
            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("✅ تأیید پیش‌نویس", $"approve_order|{approvalId}"),
                        InlineKeyboardButton.WithCallbackData("❌ رد سفارش", $"reject_order|{approvalId}") },
                new[] { InlineKeyboardButton.WithCallbackData("🏠 منوی اصلی", "ui|home") }
            });
            return Task.FromResult((preview, (InlineKeyboardMarkup?)keyboard));
        }
        catch (Exception ex)
        {
            OrderWizards.TryRemove(chatId, out _);
            Console.WriteLine($"CreateWizardApproval failed for chat {chatId}: {ex}");
            return Task.FromResult(("ساخت پیش‌نویس ناموفق بود. جزئیات خطا در گزارش برنامه ثبت شد.", (InlineKeyboardMarkup?)MainMenuKeyboard()));
        }
    }

    static async Task<string> HandleCallback(long chatId, string data, BinanceRestClient client)
    {
        if (string.IsNullOrWhiteSpace(data))
            return "دستور نامعتبر";

        if (!TradingFeaturesEnabled &&
            (data.StartsWith("note_", StringComparison.OrdinalIgnoreCase) ||
             data.StartsWith("sold|", StringComparison.OrdinalIgnoreCase) ||
             data.StartsWith("long|", StringComparison.OrdinalIgnoreCase) ||
             data.StartsWith("short|", StringComparison.OrdinalIgnoreCase) ||
             data.StartsWith("bought|", StringComparison.OrdinalIgnoreCase) ||
             data.StartsWith("approve_order|", StringComparison.OrdinalIgnoreCase)))
            return "ثبت، خرید و فروش معامله فعلاً غیرفعال است؛ فقط تحلیل و مشاهده سیگنال‌ها در دسترس است.";

        // ----- دکمه‌های کوتاه exec_xxx / note_xxx -----
        if (data.StartsWith("exec_", StringComparison.OrdinalIgnoreCase))
        {
            return "خرید/فروش خودکار فعلاً غیرفعال است.\nفقط سیگنال‌ها ارسال می‌شوند.";
        }

        if (data.StartsWith("note_", StringComparison.OrdinalIgnoreCase))
        {
            string id = data[(data.IndexOf('_') + 1)..];

            if (!TryGetPayload(id, out var full))
                return "این سیگنال منقضی شده. منتظر سیگنال جدید بمان.";

            // full = long|SYMBOL|entry|sl|tp|lev
            var p = full.Split('|');
            if (p.Length < 6)
                return "داده سیگنال ناقص است.";

            string side = p[0].ToUpperInvariant() switch
            {
                "SHORT" => "SHORT",
                "BUY" => "BUY",
                "SELL" => "SELL",
                _ => "LONG"
            };
            string symbol = p[1];
            string tradeMode = p.Length >= 7 ? p[6].ToUpperInvariant() : "MARGIN";
            string exchangeCode = p.Length >= 8 ? p[7].ToUpperInvariant() : SignalSourcePreference.Nobitex;
            decimal entry = decimal.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture);
            decimal sl = decimal.Parse(p[3], System.Globalization.CultureInfo.InvariantCulture);
            decimal tp = decimal.Parse(p[4], System.Globalization.CultureInfo.InvariantCulture);
            int lev = int.TryParse(p[5], out var lv) ? Math.Min(lv, 3) : 1;

            // note = فقط یادداشت در ربات
            if (HasOpenTrade(chatId, symbol, tradeMode, exchangeCode))
                return $"قبلاً معامله باز روی {symbol} داری.";

            OpenTrade(chatId, symbol, entry, sl, tp, side, lev, tradeMode, exchangeCode);
            return $"📝 در دفتر معاملات ثبت شد (بدون سفارش صرافی)\nصرافی: {exchangeCode}\n{tradeMode} {side} {symbol}\nEntry: {entry:N4}\nSL: {sl:N4}\nTP: {tp:N4}\n\nاین ثبت فقط برای پایش/ثبت دستی است؛ هیچ سفارشی ارسال نشده است.";
        }

        if (data.StartsWith("skip_", StringComparison.OrdinalIgnoreCase))
            return "لغو شد.";

        if (data.Equals("cancel_pending", StringComparison.OrdinalIgnoreCase))
        {
            PendingOrders.Remove(chatId);
            return "لغو شد.";
        }

        var approvalParts = data.Split('|');
        if (approvalParts.Length == 2 && approvalParts[0] == "approve_order")
        {
            var result = new ManualOrderApprovalRepository(DatabaseConnectionString)
                .ApproveForManualExecution(chatId, approvalParts[1]);
            return result switch
            {
                ApprovalTransitionResult.Updated => "✅ درخواست تأیید شد و برای اجرای دستی آماده است. هیچ سفارشی به صرافی ارسال نشد.",
                ApprovalTransitionResult.Expired => "مهلت تأیید تمام شده؛ پیش‌نمایش جدید بساز.",
                ApprovalTransitionResult.AccountInactive => "حساب صرافی غیرفعال شده است.",
                ApprovalTransitionResult.NotFound => "درخواست پیدا نشد یا متعلق به شما نیست.",
                _ => "این درخواست قبلاً تعیین‌تکلیف شده است."
            };
        }

        if (approvalParts.Length == 2 && approvalParts[0] == "reject_order")
        {
            var result = new ManualOrderApprovalRepository(DatabaseConnectionString).Reject(chatId, approvalParts[1]);
            return result == ApprovalTransitionResult.Updated
                ? "درخواست رد شد. هیچ سفارشی ارسال نشد."
                : "درخواست پیدا نشد یا قبلاً تعیین‌تکلیف شده است.";
        }

        // ----- بقیه با | -----
        var parts = data.Split('|');
        if (parts.Length == 0)
            return "دستور نامعتبر";

        if (parts[0] == "skip")
            return "لغو شد.";

        if (parts[0] == "sold" && parts.Length >= 2 && int.TryParse(parts[1], out int tradeId))
        {
            var trade = GetOpenTrades(chatId).FirstOrDefault(t => t.Id == tradeId);
            if (trade is null) return "معامله بازی پیدا نشد.";
            var livePrice = await GetNobitexLastPriceAsync(trade.Symbol);
            if (livePrice is null || livePrice <= 0)
                return "قیمت فعلی دریافت نشد؛ معامله در دفتر باز باقی ماند.";

            CloseTrade(trade.Id, livePrice.Value, "UserConfirmedManualExit");
            decimal pnl = CalcPnlPercent(trade.Side, trade.EntryPrice, livePrice.Value);
            return $"✅ خروج دستی در دفتر ثبت شد: {trade.TradeMode} {trade.Side} {trade.Symbol}\n" +
                   $"Exit: {livePrice.Value:N4}\nPnL: {pnl:N2}%\n" +
                   "این دکمه سفارش صرافی ارسال نمی‌کند؛ فقط دفتر معاملات را به‌روزرسانی می‌کند.";
        }

        if (parts[0] == "confirm_pending")
        {
            PendingOrders.Remove(chatId);
            return "ارسال سفارش در این نسخه غیرفعال است؛ این مرحله فقط ثبت و بررسی دستی سیگنال را پشتیبانی می‌کند.";
        }

        // سازگاری قدیمی: long| / short| / bought| → فقط یادداشت
        if ((parts[0] == "long" || parts[0] == "short" || parts[0] == "bought") && parts.Length >= 5)
        {
            string side = parts[0] == "short" ? "SHORT" : "LONG";
            string symbol = parts[1];
            decimal entry = decimal.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
            decimal sl = decimal.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture);
            decimal tp = decimal.Parse(parts[4], System.Globalization.CultureInfo.InvariantCulture);
            int lev = parts.Length >= 6 && int.TryParse(parts[5], out var l) ? Math.Min(l, 3) : 1;

            if (HasOpenTrade(chatId, symbol))
                return $"قبلاً معامله باز روی {symbol} داری.";

            OpenTrade(chatId, symbol, entry, sl, tp, side, lev);
            return $"📝 ثبت شد (بدون سفارش صرافی): {side} {symbol}\nEntry: {entry:N4}\nSL: {sl:N4}\nTP: {tp:N4}\nLeverage: {lev}x";
        }

        if (parts[0] == "sold" && parts.Length >= 2 && int.TryParse(parts[1], out var closedTradeId))
        {
            var trade = GetOpenTrades(chatId).FirstOrDefault(t => t.Id == closedTradeId);
            if (trade is null) return "پوزیشن باز متعلق به شما پیدا نشد یا قبلاً بسته شده است.";
            var ticker = await GetMarketLastPriceAsync(trade.ExchangeCode, trade.Symbol);
            if (ticker is null || ticker <= 0)
                return $"قیمت {trade.Symbol} از {trade.ExchangeCode} دریافت نشد؛ پوزیشن در دفتر باز باقی ماند.";
            decimal exitPrice = ticker.Value;
            CloseTrade(trade.Id, exitPrice, "ManualClose");
            decimal pnl = trade.Side == "SHORT"
                ? (trade.EntryPrice - exitPrice) / trade.EntryPrice * 100
                : (exitPrice - trade.EntryPrice) / trade.EntryPrice * 100;
            return $"✅ خروج ثبت شد: {trade.Side} {trade.Symbol}\nExchange: {trade.ExchangeCode}\nExit: {exitPrice:N4}\nPnL: {pnl:N2}%\nثبت خروج در دفتر انجام شد؛ ارسال سفارش خودکار انجام نشده است.";
        }

        if (parts[0] == "skip")
            return "لغو شد.";

        return "از منوی دکمه‌ای زیر استفاده کنید:";
    }

    static async Task<string> HandleUserCommand(long chatId, string text, BinanceRestClient client)
    {
        text = text.Trim();

        string normalizedCommand = text.TrimStart('/').Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.ToLowerInvariant() ?? "";
        if (!TradingFeaturesEnabled && normalizedCommand is
            "buy" or "sell" or "bought" or "sold" or "long" or "short" or "order" or "neworder" or "preview")
            return "قابلیت خرید، فروش و ثبت سفارش فعلاً غیرفعال است؛ فقط تحلیل و مشاهده سیگنال‌ها در دسترس است.";

        // اگر فقط عدد فرستاده (مبلغ تومان) — اینجا پردازش نکن
        // باید در ListenTelegramCommands با PendingOrders هندل شود
        var maybeNumber = text.Replace(",", "").Replace("،", "").Replace(" ", "");
        if (decimal.TryParse(maybeNumber,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out _) &&
            !text.StartsWith("/"))
        {
            if (PendingOrders.TryGetValue(chatId, out var pend) && pend.BudgetToman == null)
            {
                // اگر به‌اشتباه به اینجا رسید، همان منطق مبلغ
                pend.BudgetToman = decimal.Parse(maybeNumber,
                    System.Globalization.CultureInfo.InvariantCulture);

                // فقط متن؛ دکمه را Listen بهتر می‌فرستد
                return
                    $"مبلغ {pend.BudgetToman:N0} تومان ثبت شد.\n" +
                    $"اگر دکمه تأیید نیامد، دوباره روی سیگنال بزن یا در Listen بلوک Pending را فعال کن.";
            }

            return "برای ورود از دکمه سیگنال استفاده کن؛ بعد مبلغ را بفرست. برای لغو از دکمه لغو استفاده کن.";
        }

        if (text.StartsWith("/")) text = text[1..];

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "";

        string action = parts[0].ToLowerInvariant();

        // /rate 95000
        if (action == "rate")
        {
            if (parts.Length < 2 ||
                !decimal.TryParse(parts[1].Replace(",", ""),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var rate) || rate < 1000)
            {
                return $"نرخ فعلی تتر: {UsdtTomanRate:N0} تومان\nبرای تغییر نرخ از بخش تنظیمات استفاده کنید.";
            }
            UsdtTomanRate = rate;
            return $"✅ نرخ تتر: {UsdtTomanRate:N0} تومان";
        }

        if (action == "open")
        {
            var trades = GetOpenTrades(chatId);
            if (trades.Count == 0) return "معامله بازی نداری.";
            return "معاملات باز شما:\n" + string.Join("\n",
                trades.Select(t =>
                    $"{t.Side} {t.Symbol} | Entry {t.EntryPrice:N4} | SL {t.StopLoss:N4} | TP {t.TakeProfit:N4} | lev {t.Leverage}x"));
        }

        if (action == "report")
            return GetSignalReport();

        if (action == "stats")
            return GetPerformanceStats(chatId);

        if (action == "cancel")
        {
            PendingOrders.Remove(chatId);
            OrderWizards.TryRemove(chatId, out _);
            ManualUiStates.TryRemove(chatId, out _);
            return "فرآیند ورودی و سفارش معلق لغو شد. پیش‌نویس‌های تأیید سفارش را از دکمه رد همان پیام تعیین‌تکلیف کنید.";
        }

        if (parts.Length < 2)
            return "برای ثبت خرید، خروج دستی، معاملات باز و گزارش‌ها از منوی دکمه‌ای استفاده کنید.";

        string symbol = parts[1].ToUpperInvariant();
        if (!symbol.EndsWith("USDT")) symbol += "USDT";

        if (action == "bought")
        {
            if (HasOpenTrade(chatId, symbol, "SPOT"))
                return $"قبلاً معامله باز روی {symbol} داری.";

            decimal entry;
            if (parts.Length >= 3 && decimal.TryParse(parts[2],
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var manualPrice))
                entry = manualPrice;
            else
            {
                var live = await GetNobitexLastPriceAsync(symbol);
                if (live is null) return $"قیمت {symbol} گرفته نشد.";
                entry = live.Value;
            }

            decimal sl = entry * 0.985m;
            decimal tp = entry * 1.03m;
            OpenTrade(chatId, symbol, entry, sl, tp, "BUY", 1, "SPOT");
            return $"✅ ثبت شد: خرید {symbol}\nEntry: {entry:N4}\nSL: {sl:N4}\nTP: {tp:N4}";
        }

        if (action == "sold")
        {
            var trades = GetOpenTrades(chatId).Where(t => t.Symbol == symbol).ToList();
            if (trades.Count == 0)
                return $"معامله بازی برای {symbol} نیست.";

            decimal exitPrice;
            if (parts.Length >= 3 && decimal.TryParse(parts[2],
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var manualExit))
                exitPrice = manualExit;
            else
            {
                var live = await GetNobitexLastPriceAsync(symbol);
                exitPrice = live ?? trades[0].EntryPrice;
            }

            foreach (var t in trades)
                CloseTrade(t.Id, exitPrice, "ManualSell");

            var t0 = trades[0];
            decimal pnl = t0.Side == "SHORT"
                ? (t0.EntryPrice - exitPrice) / t0.EntryPrice * 100
                : (exitPrice - t0.EntryPrice) / t0.EntryPrice * 100;

            return $"✅ بسته شد: {symbol}\nExit: {exitPrice:N4}\nPnL: {pnl:N2}%";
        }

        return "";
    }

    // ==================== تحلیل ====================

    static async Task<List<(string Text, InlineKeyboardMarkup? Keyboard)>?> AnalyzeSymbol(
    BinanceRestClient client, string symbol, string exchangeCode = "NOBITEX")
    {
        try
        {
            bool isBinance = exchangeCode.Equals(SignalSourcePreference.Binance, StringComparison.OrdinalIgnoreCase);
            var data15m = isBinance
                ? await BinancePublicMarket.GetKlinesAsync(symbol, "15m", 100)
                : await GetNobitexQuotesAsync(symbol, "15", 100);
            await Task.Delay(200);
            var data1h = isBinance
                ? await BinancePublicMarket.GetKlinesAsync(symbol, "1h", 100)
                : await GetNobitexQuotesAsync(symbol, "60", 100);
            await Task.Delay(200);

            if (data15m == null || data1h == null || data15m.Count < 50 || data1h.Count < 50)
            {
                Console.WriteLine($"SKIP {exchangeCode}/{symbol}: insufficient candles (15m={data15m?.Count ?? 0}, 1h={data1h?.Count ?? 0}, minimum=50).");
                return null;
            }

            decimal? live = isBinance
                ? await BinancePublicMarket.GetLastPriceAsync(symbol)
                : await GetNobitexLastPriceAsync(symbol);
            decimal price = live ?? data15m.Last().Close;
            if (price <= 0)
            {
                Console.WriteLine($"SKIP {exchangeCode}/{symbol}: invalid live/fallback price.");
                return null;
            }

            if (IsStablePair(symbol, price))
            {
                Console.WriteLine($"SKIP {exchangeCode}/{symbol}: stable pair.");
                return null;
            }

            var a15 = AnalyzeTimeframe(data15m);
            var a1h = AnalyzeTimeframe(data1h);
            if (!a15.IsValid || !a1h.IsValid)
            {
                Console.WriteLine($"SKIP {exchangeCode}/{symbol}: indicators invalid (15m={a15.IsValid}, 1h={a1h.IsValid}).");
                return null;
            }
            var (support, resistance) = FindSupportResistance(data1h);

            var sma50 = data1h.GetSma(50).Last().Sma;
            bool aboveSma50 = sma50 is not null && price >= (decimal)sma50.Value;
            bool belowSma50 = sma50 is not null && price <= (decimal)sma50.Value;

            bool nearSupport = StrategyRules.IsNearSupport(price, support);
            bool nearResistance = StrategyRules.IsNearResistance(price, resistance);

            int buyScore = a15.BuyScore + a1h.BuyScore;
            int sellScore = a15.SellScore + a1h.SellScore;

            bool bothBullish = a15.BuyScore > a15.SellScore && a1h.BuyScore > a1h.SellScore;
            bool bothBearish = a15.SellScore > a15.BuyScore && a1h.SellScore > a1h.BuyScore;

            if (bothBullish) buyScore += 2;
            if (bothBearish) sellScore += 2;
            if (nearSupport) buyScore += 2;
            if (nearResistance) sellScore += 2;
            // سرعت: AI فقط نزدیک سیگنال
            if (buyScore < 4 && sellScore < 4)
            {
                Console.WriteLine($"FILTER {exchangeCode}/{symbol}: weak scores B/S={buyScore}/{sellScore}.");
                return null;
            }

            var (aiScore, aiReason, _) = await GetNewsAiScoreAsync(symbol, buyScore, sellScore);
            if (aiScore > 0) buyScore += aiScore;
            if (aiScore < 0) sellScore += -aiScore;

            string? side = null;

            // LONG
            if (aiScore >= 0 &&
                buyScore >= 6 && buyScore >= sellScore + 2 &&
                a15.Rsi < 65 && a1h.Rsi < 70 &&
                StrategyRules.IsConfirmedLong(bothBullish, nearSupport, aboveSma50))
            {
                side = "LONG";
            }
            // SHORT
            else if (aiScore <= 0 &&
                     sellScore >= 6 && sellScore >= buyScore + 2 &&
                     a15.Rsi > 35 && a1h.Rsi > 30 &&
                     StrategyRules.IsConfirmedShort(bothBearish, nearResistance, belowSma50))
            {
                side = "SHORT";
            }

            var results = new List<(string Text, InlineKeyboardMarkup? Keyboard)>();

            // Spot is a separate, unleveraged strategy. It only creates BUY ideas;
            // SELL recommendations are generated per user's existing Spot holding.
            bool spotBuy = aiScore >= 0 && buyScore >= 5 && buyScore >= sellScore + 1 &&
                           a15.Rsi < 70 && a1h.Rsi < 75 &&
                           StrategyRules.IsConfirmedLong(bothBullish, nearSupport, aboveSma50);
            if (!spotBuy && side is null)
                Console.WriteLine($"FILTER {exchangeCode}/{symbol}: no confirmed setup (B/S={buyScore}/{sellScore}, RSI={a15.Rsi:N1}/{a1h.Rsi:N1}, bullish={bothBullish}, bearish={bothBearish}, support={nearSupport}, resistance={nearResistance}, aboveSMA50={aboveSma50}, belowSMA50={belowSma50}, AI={aiScore}).");
            if (spotBuy)
                AddSignal("SPOT", "BUY", TradeSide.Buy, 1);

            if (side is not null)
                AddSignal("MARGIN", side, side == "LONG" ? TradeSide.Long : TradeSide.Short, 1);

            return results.Count == 0 ? null : results;

            void AddSignal(string mode, string signalSide, TradeSide tradeSide, int leverage)
            {
                if (HasRecentSignal(symbol, mode, exchangeCode))
                {
                    Console.WriteLine($"SKIP {exchangeCode}/{symbol} {mode}: duplicate signal inside 4-hour cooldown.");
                    return;
                }
                if (!StrategyRules.TryCreatePlan(tradeSide, price, support, resistance, out var plan))
                {
                    Console.WriteLine($"SKIP {exchangeCode}/{symbol} {mode}: risk/reward plan rejected (support={support:N4}, resistance={resistance:N4}).");
                    return;
                }
                if (mode == "MARGIN") leverage = SuggestLeverage(plan.RiskPercent);

                decimal rewardPercent = plan.RewardRiskRatio * plan.RiskPercent;
                Console.WriteLine($"{symbol,-12} | {price,12:N4} | {mode} {signalSide} | B:{buyScore} S:{sellScore}");
                SaveAnalysis(symbol, price, a15.Rsi, buyScore, sellScore, $"{mode}_{signalSide}", exchangeCode);
                long? signalId = TrackSignal(symbol, price, plan.StopLoss, plan.TakeProfit, signalSide, mode, exchangeCode);
                if (signalId is null)
                {
                    Console.WriteLine($"SKIP DELIVERY {exchangeCode}/{symbol}: signal could not be persisted.");
                    return;
                }

                string emoji = signalSide is "LONG" or "BUY" ? "🟢" : "🔴";
                string text =
                    $"{emoji} {mode} {signalSide} {symbol} | Signal #{signalId}\n" +
                    $"Entry: {price:N4} ({(isBinance ? "Binance Spot" : "Nobitex")})\n" +
                    $"Support: {support:N4} | Resistance: {resistance:N4}\n" +
                    $"Stop Loss: {plan.StopLoss:N4} ({plan.RiskPercent:N2}%)\n" +
                    $"Take Profit: {plan.TakeProfit:N4} ({rewardPercent:N2}%)\n" +
                    $"R/R: 1:{plan.RewardRiskRatio:N2}\n" +
                    (mode == "SPOT" ? "استراتژی: Spot (بدون اهرم)\n" : $"استراتژی: Margin | اهرم پیشنهادی: {leverage}x\n") +
                    $"منبع قیمت: {(isBinance ? "Binance Spot" : "Nobitex")}\n" +
                    $"Score B/S: {buyScore}/{sellScore}\n" +
                    $"RSI 15m/1h: {a15.Rsi:N1}/{a1h.Rsi:N1}\n" +
                    $"AI/News: {aiScore} ({aiReason})\n\n" +
                    "این فقط پیشنهاد تحلیلی است؛ ارسال سفارش واقعی در این نسخه غیرفعال است.";

                // Signal messages stay informational; trade controls are intentionally
                // omitted while TradingFeaturesEnabled is false.
                results.Add((text, null));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Analyze {symbol}: {ex.Message}");
            if (ex.Message.Contains("429") || ex.Message.Contains("rate") || ex.Message.Contains("Too many"))
                await Task.Delay(60000);
            return null;
        }
    }

    static int SuggestLeverage(decimal riskPercent)
    {
        // Until account-aware position sizing and liquidation checks exist,
        // never suggest leverage above 1x based only on chart stop distance.
        return 1;
    }


    static async Task<bool> IsOllamaRunningAsync()
    {
        try
        {
            var handler = new HttpClientHandler { UseProxy = false };
            using var probe = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
            var resp = await probe.GetAsync($"{OllamaUrl}/api/tags");
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    static async Task<(int Score, string Reason, List<string> News)> GetNewsAiScoreAsync(
    string symbol, int techBuy, int techSell)
    {
        var news = await GetFreeNewsAsync(symbol);
        if (news.Count == 0)
            return (0, "no-news", news);

        // 1) اگر Ollama بالا باشد
        try
        {
            if (await IsOllamaRunningAsync())
            {
                var ollama = await GetOllamaNewsScoreAsync(symbol, news, techBuy, techSell);
                return (ollama.Score, ollama.Reason, news);
            }

            //var handler = new HttpClientHandler { UseProxy = false };
            //using var probe = new HttpClient(handler)
            //{
            //    Timeout = TimeSpan.FromSeconds(20)
            //};
            //var ok = await probe.GetAsync("http://localhost:11434/api/tags");
            //if (ok.IsSuccessStatusCode)
            //{
            //    var ollama = await GetOllamaNewsScoreAsync(symbol, news, techBuy, techSell);
            //    return (ollama.Score, ollama.Reason, news);
            //}
        }
        catch { }

        //// 2) اگر کلید Gemini باشد
        //var geminiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? GeminiApiKey;
        //if (!string.IsNullOrWhiteSpace(geminiKey))
        //{
        //    var gemini = await GetGeminiNewsScoreAsync(symbol, news, techBuy, techSell);
        //    return (gemini.Score, gemini.Reason, news);
        //}

        // 3) رایگان بدون AI
        return (GetKeywordNewsScore(news), "keyword-free", news);
    }

    // ==================== معاملات باز ====================

    static async Task<(int Score, string Reason)> GetOllamaNewsScoreAsync(
    string symbol, List<string> headlines, int techBuy, int techSell)
    {
        try
        {
            var handler = new HttpClientHandler { UseProxy = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(3) };

            var newsText = string.Join("\n", headlines.Select((h, i) => $"{i + 1}. {h}"));
            var prompt =
                $@"Symbol: {symbol}
Technical BuyScore={techBuy}, SellScore={techSell}
News:
{newsText}

Rate short-term news impact for traders from -2 to +2.
Reply ONLY valid JSON: {{""score"": 1, ""reason"": ""short reason""}}";

            var body = new
            {
                model = OllamaModel,
                stream = false,
                prompt,
                format = "json"
            };

            var json = System.Text.Json.JsonSerializer.Serialize(body);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var resp = await http.PostAsync($"{OllamaUrl}/api/generate", content);
            var respText = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                return (GetKeywordNewsScore(headlines), "ollama-failed");

            using var doc = System.Text.Json.JsonDocument.Parse(respText);
            var raw = doc.RootElement.GetProperty("response").GetString() ?? "";
            raw = raw.Replace("```json", "").Replace("```", "").Trim();

            using var parsed = System.Text.Json.JsonDocument.Parse(raw);
            int score = parsed.RootElement.GetProperty("score").GetInt32();
            string reason = parsed.RootElement.TryGetProperty("reason", out var r)
                ? r.GetString() ?? ""
                : "";

            if (score > 2) score = 2;
            if (score < -2) score = -2;

            return (score, reason);
        }
        catch
        {
            return (GetKeywordNewsScore(headlines), "ollama-error-fallback");
        }
    }

    static async Task<List<string>> GetFreeNewsAsync(string symbol)
    {
        // نماد را ساده کن: BTCUSDT -> BTC
        string coin = symbol.Replace("USDT", "").Replace("USD", "");
        var headlines = new List<string>();

        var feeds = new[]
        {
        "https://cointelegraph.com/rss",
        "https://www.coindesk.com/arc/outboundfeeds/rss/"
    };

        foreach (var url in feeds)
        {
            try
            {
                var feed = await CodeHollow.FeedReader.FeedReader.ReadAsync(url);
                var items = feed.Items
                    .Where(i =>
                        (i.Title?.Contains(coin, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (i.Title?.Contains("Bitcoin", StringComparison.OrdinalIgnoreCase) ?? false) && coin == "BTC" ||
                        (i.Title?.Contains("Ethereum", StringComparison.OrdinalIgnoreCase) ?? false) && coin == "ETH" ||
                        (i.Title?.Contains("crypto", StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (i.Title?.Contains("market", StringComparison.OrdinalIgnoreCase) ?? false))
                    .Take(5);

                foreach (var item in items)
                {
                    if (!string.IsNullOrWhiteSpace(item.Title))
                        headlines.Add(item.Title.Trim());
                }
            }
            catch
            {
                // اگر یک RSS قطع بود، بقیه را ادامه بده
            }
        }

        return headlines.Distinct().Take(8).ToList();
    }

    static async Task<string?> AnalyzeSellForOpenTrade(OpenTradeRow trade)
    {
        bool isBinance = trade.ExchangeCode.Equals(SignalSourcePreference.Binance, StringComparison.OrdinalIgnoreCase);
        if (!isBinance && !trade.ExchangeCode.Equals(SignalSourcePreference.Nobitex, StringComparison.OrdinalIgnoreCase))
            return null;
        string symbol = trade.Symbol;
        var data15m = isBinance
            ? await BinancePublicMarket.GetKlinesAsync(symbol, "15m", 100)
            : await GetNobitexQuotesAsync(symbol, "15", 100);
        await Task.Delay(150);
        var data1h = isBinance
            ? await BinancePublicMarket.GetKlinesAsync(symbol, "1h", 100)
            : await GetNobitexQuotesAsync(symbol, "60", 100);
        if (data15m == null || data1h == null || data15m.Count < 50 || data1h.Count < 50)
            return null;

        var live = isBinance
            ? await BinancePublicMarket.GetLastPriceAsync(symbol)
            : await GetNobitexLastPriceAsync(symbol);
        if (live is null || live <= 0) return null;
        decimal price = live.Value;

        var a15 = AnalyzeTimeframe(data15m);
        var a1h = AnalyzeTimeframe(data1h);
        if (!a15.IsValid || !a1h.IsValid) return null;

        int buyScore = a15.BuyScore + a1h.BuyScore;
        int sellScore = a15.SellScore + a1h.SellScore;

        bool bothBearish = a15.SellScore > a15.BuyScore && a1h.SellScore > a1h.BuyScore;
        bool bothBullish = a15.BuyScore > a15.SellScore && a1h.BuyScore > a1h.SellScore;
        if (bothBearish) sellScore += 2;
        if (bothBullish) buyScore += 2;

        bool shouldExit = trade.TradeMode == "SPOT"
            ? bothBearish && sellScore >= 7 && sellScore >= buyScore + 3 && a15.Rsi > 40 && a1h.Rsi > 35
            : trade.Side == "SHORT"
                ? bothBullish && buyScore >= 7 && buyScore >= sellScore + 3 && a15.Rsi < 60 && a1h.Rsi < 65
                : bothBearish && sellScore >= 7 && sellScore >= buyScore + 3 && a15.Rsi > 40 && a1h.Rsi > 35;

        if (!shouldExit)
            return null;

        string action = trade.TradeMode == "SPOT" ? "SELL" : trade.Side == "SHORT" ? "COVER SHORT" : "CLOSE LONG";
        return $"🔴 {action} (معامله باز شما)\n" +
               $"{symbol}\n" +
               $"Price: {price:N4} ({trade.ExchangeCode})\n" +
               $"Score B/S: {buyScore}/{sellScore}\n" +
               $"RSI 15m/1h: {a15.Rsi:N1}/{a1h.Rsi:N1}\n\n" +
               $"پس از انجام دستی معامله در صرافی، دکمه ثبت خروج را بزن.";
    }

    static int GetKeywordNewsScore(List<string> headlines)
    {
        if (headlines.Count == 0) return 0;

        string[] bullish = { "approve", "approval", "etf", "partnership", "adoption", "surge", "rally",
        "record", "inflow", "bullish", "launch", "upgrade", "listing", "growth" };

        string[] bearish = { "hack", "lawsuit", "ban", "sec", "fraud", "crash", "exploit", "outflow",
        "bearish", "investigation", "fine", "delay", "reject", "liquidation", "bankrupt" };

        int score = 0;
        foreach (var h in headlines)
        {
            var lower = h.ToLowerInvariant();
            if (bullish.Any(w => lower.Contains(w))) score += 1;
            if (bearish.Any(w => lower.Contains(w))) score -= 1;
        }

        // محدود بین -2 تا +2
        if (score > 2) score = 2;
        if (score < -2) score = -2;
        return score;
    }

    // ==================== دیتابیس ====================

    static void InitializeDatabase()
    {
        DatabaseInitializer.Initialize(DatabaseConnectionString);
        Console.WriteLine("Multi-user database ready.");
    }

    static void ProvisionExchangeAccountInteractive()
    {
        Console.WriteLine("ثبت امن حساب صرافی؛ اطلاعات محرمانه را در تلگرام ارسال نکنید.");
        Console.Write("Telegram ChatId (کاربر باید قبلاً /start زده باشد): ");
        if (!long.TryParse(Console.ReadLine(), out long chatId) || chatId <= 0)
            throw new InvalidOperationException("ChatId نامعتبر است.");
        Console.Write("Exchange code (مثلاً NOBITEX): ");
        string exchange = Console.ReadLine()?.Trim() ?? "";
        if (!exchange.Equals("NOBITEX", StringComparison.OrdinalIgnoreCase) &&
            !exchange.Equals("BINANCE_TESTNET", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("در این مرحله فقط NOBITEX و BINANCE_TESTNET پشتیبانی می‌شوند.");
        Console.Write("Account label: ");
        string label = Console.ReadLine()?.Trim() ?? "";
        Console.WriteLine("برای امنیت، فقط مجوزهای لازم را فعال کنید و مجوز برداشت وجه ندهید.");
        bool isNobitex = exchange.Equals("NOBITEX", StringComparison.OrdinalIgnoreCase);
        bool isBinanceTestnet = exchange.Equals("BINANCE_TESTNET", StringComparison.OrdinalIgnoreCase);
        if (isBinanceTestnet)
            Console.WriteLine("کلیدها باید از محیط آزمایشی Spot ساخته شوند: https://testnet.binance.vision (نه حساب اصلی Binance).");
        string apiKey = ReadConsoleSecret(isNobitex
            ? "Nobitex API Key (کلید عمومی): "
            : isBinanceTestnet ? "Binance Spot Testnet API Key: " : "API Key: ");
        string apiSecret = ReadConsoleSecret(isNobitex
            ? "Nobitex API Private Key / Secret (کلید خصوصی امضای Ed25519): "
            : "API Secret: ");
        string passphrase = isNobitex || isBinanceTestnet ? "" : ReadConsoleSecret("Passphrase (در صورت نیاز): ");
        long id = new UserExchangeAccountRepository(DatabaseConnectionString)
            .Add(chatId, exchange, label, apiKey, string.IsNullOrEmpty(apiSecret) ? null : apiSecret,
                string.IsNullOrEmpty(passphrase) ? null : passphrase);
        Console.WriteLine($"حساب ذخیره شد. AccountId={id}. مقادیر محرمانه نمایش داده نمی‌شوند.");
        Console.WriteLine("اعتبارسنجی اتصال و معامله واقعی در این نسخه فعال نیست.");
    }

    static string ReadConsoleSecret(string prompt)
    {
        Console.Write(prompt);
        var value = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (value.Length > 0) value.Length--; continue; }
            if (!char.IsControl(key.KeyChar)) value.Append(key.KeyChar);
        }
        Console.WriteLine();
        return value.ToString();
    }

    static (string Text, InlineKeyboardMarkup? Keyboard) CreateManualOrderPreview(long chatId, string commandText)
    {
        const string usage = "برای ساخت پیش‌نمایش سفارش، از منوی «معاملات» گزینه «ثبت سفارش دستی» را انتخاب کنید و مراحل را دنبال کنید.";
        var parts = commandText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 7 || parts.Length > 10) return (usage, null);
        if (!long.TryParse(parts[1], out long accountId) || accountId <= 0 ||
            !decimal.TryParse(parts[6], System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out decimal quantity) || quantity <= 0)
            return (usage, null);

        if (!Enum.TryParse<ExchangeTradeMode>(parts[3], true, out var mode) || !Enum.IsDefined(mode)) return (usage, null);
        string sideText = parts[4].ToUpperInvariant();
        ExchangeOrderSide side;
        if (mode == ExchangeTradeMode.Spot && sideText is "BUY" or "SELL")
            side = sideText == "BUY" ? ExchangeOrderSide.Buy : ExchangeOrderSide.Sell;
        else if (mode != ExchangeTradeMode.Spot && sideText is "LONG" or "SHORT")
            side = sideText == "LONG" ? ExchangeOrderSide.Buy : ExchangeOrderSide.Sell;
        else return ("برای Spot از buy/sell و برای Margin/Futures از long/short استفاده کن.\n" + usage, null);

        if (!Enum.TryParse<ExchangeOrderType>(parts[5], true, out var orderType) || !Enum.IsDefined(orderType)) return (usage, null);
        int next = 7;
        decimal? limitPrice = null;
        decimal? priceGuard = null;
        if (orderType == ExchangeOrderType.Limit)
        {
            if (parts.Length <= next || !decimal.TryParse(parts[next], System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsedPrice) || parsedPrice <= 0)
                return ("برای سفارش Limit قیمت مثبت الزامی است.\n" + usage, null);
            limitPrice = parsedPrice;
            next++;
        }
        else if (mode == ExchangeTradeMode.Spot)
        {
            if (parts.Length <= next || !decimal.TryParse(parts[next], System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsedGuard) || parsedGuard <= 0)
                return ("برای Market اسپات، PriceGuard مثبت الزامی است.\n" + usage, null);
            priceGuard = parsedGuard;
            next++;
        }
        decimal? stopLoss = null;
        decimal? takeProfit = null;
        if (mode != ExchangeTradeMode.Spot)
        {
            if (orderType != ExchangeOrderType.Limit || parts.Length != next + 2 ||
                !decimal.TryParse(parts[next], System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsedSl) ||
                !decimal.TryParse(parts[next + 1], System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsedTp))
                return ("برای Margin/Futures سفارش Limit همراه StopLoss و TakeProfit الزامی است.\n" + usage, null);
            stopLoss = parsedSl;
            takeProfit = parsedTp;
            next += 2;
        }
        if (parts.Length != next) return (usage, null);

        var account = new UserExchangeAccountRepository(DatabaseConnectionString).List(chatId)
            .FirstOrDefault(x => x.Id == accountId && x.IsActive);
        if (account is null) return ("حساب فعال متعلق به شما پیدا نشد. از منوی «حساب‌ها و صرافی‌ها» حساب را انتخاب کنید.", null);

        try
        {
            string approvalId = new ManualOrderApprovalRepository(DatabaseConnectionString).Create(
                new ManualOrderDraft(chatId, accountId, account.ExchangeCode, parts[2], mode, side,
                    orderType, quantity, limitPrice, stopLoss, takeProfit, PriceGuard: priceGuard));
            string displaySide = mode == ExchangeTradeMode.Spot ? side.ToString().ToUpperInvariant() : sideText;
            string preview = $"⚠️ پیش‌نمایش سفارش (اعتبار ۵ دقیقه)\n" +
                $"صرافی: {account.ExchangeCode} | حساب: #{account.Id} {account.AccountLabel}\n" +
                $"نماد: {parts[2].ToUpperInvariant()}\nحالت: {mode}\nجهت: {displaySide}\n" +
                $"نوع: {orderType}\nمقدار: {quantity}\n" +
                (limitPrice is null ? "" : $"قیمت Limit: {limitPrice.Value}\n") +
                (priceGuard is null ? "" : $"Price Guard: {priceGuard.Value}\n") +
                (stopLoss is decimal slValue && takeProfit is decimal tpValue
                    ? $"StopLoss: {slValue} | TakeProfit: {tpValue}\n" : "") +
                "\nتأیید فقط درخواست را برای اجرای دستی علامت‌گذاری می‌کند؛ هیچ سفارشی ارسال نمی‌شود.";
            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("✅ تأیید برای اجرای دستی", $"approve_order|{approvalId}"),
                        InlineKeyboardButton.WithCallbackData("❌ رد", $"reject_order|{approvalId}") }
            });
            return (preview, keyboard);
        }
        catch (Exception ex) { return ($"ساخت پیش‌نمایش ناموفق بود: {ex.Message}", null); }
    }

    static void RegisterUser(long chatId, string? username, string? firstName)
    {
        using var conn = new SqliteConnection(DatabaseConnectionString);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
        INSERT INTO Users (ChatId, Username, FirstName, IsActive)
        VALUES ($id, $u, $f, 1)
        ON CONFLICT(ChatId) DO UPDATE SET
            Username=$u,
            FirstName=$f,
            IsActive=1;";
        cmd.Parameters.AddWithValue("$id", chatId);
        cmd.Parameters.AddWithValue("$u", username ?? "");
        cmd.Parameters.AddWithValue("$f", firstName ?? "");
        cmd.ExecuteNonQuery();
    }

    static bool IsStablePair(string symbol, decimal price)
    {
        string[] stables =
        {
            "USDCUSDT","RLUSDUSDT","FDUSDUSDT","TUSDUSDT","DAIUSDT",
            "USDPUSDT","USD1USDT","USDEUSDT","EURUSDT","AEURUSDT"
        };
        if (stables.Contains(symbol)) return true;

        // تقریبی: قیمت نزدیک 1 دلار
        if (symbol.EndsWith("USDT") && price > 0.95m && price < 1.05m)
            return true;

        return false;
    }

    static void SetUserActive(long chatId, bool active)
    {
        using var conn = new SqliteConnection(DatabaseConnectionString);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Users SET IsActive=$a WHERE ChatId=$id";
        cmd.Parameters.AddWithValue("$a", active ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", chatId);
        cmd.ExecuteNonQuery();
    }

    static List<long> GetActiveChatIds()
    {
        var list = new List<long>();
        using var conn = new SqliteConnection(DatabaseConnectionString);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ChatId FROM Users WHERE IsActive=1";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(reader.GetInt64(0));
        return list;
    }

    static void SaveAnalysis(string symbol, decimal price, double rsi, int buy, int sell, string decision,
        string exchangeCode = "NOBITEX")
    {
        using var conn = new SqliteConnection(DatabaseConnectionString);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO AnalysisResults (Symbol, ExchangeCode, Timeframe, Price, RSI, BuyScore, SellScore, Decision)
            VALUES ($s, $exchange, '15m+1h', $p, $r, $b, $sell, $d)";
        cmd.Parameters.AddWithValue("$s", symbol);
        cmd.Parameters.AddWithValue("$exchange", exchangeCode);
        cmd.Parameters.AddWithValue("$p", price);
        cmd.Parameters.AddWithValue("$r", rsi);
        cmd.Parameters.AddWithValue("$b", buy);
        cmd.Parameters.AddWithValue("$sell", sell);
        cmd.Parameters.AddWithValue("$d", decision);
        cmd.ExecuteNonQuery();
    }

    static bool HasOpenTrade(long chatId, string symbol, string? tradeMode = null, string? exchangeCode = null)
    {
        using var conn = new SqliteConnection(DatabaseConnectionString);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM OpenTrades WHERE ChatId=$c AND Symbol=$s AND Status='Open'" +
            (tradeMode is null ? "" : " AND TradeMode=$m") +
            (exchangeCode is null ? "" : " AND ExchangeCode=$exchange");
        cmd.Parameters.AddWithValue("$c", chatId);
        cmd.Parameters.AddWithValue("$s", symbol);
        if (tradeMode is not null) cmd.Parameters.AddWithValue("$m", tradeMode);
        if (exchangeCode is not null) cmd.Parameters.AddWithValue("$exchange", exchangeCode);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    static void OpenTrade(long chatId, string symbol, decimal entry, decimal sl, decimal tp,
        string side = "LONG", int leverage = 1, string tradeMode = "MARGIN",
        string exchangeCode = "NOBITEX", long? exchangeAccountId = null)
    {
        using var conn = new SqliteConnection(DatabaseConnectionString);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
        INSERT INTO OpenTrades
            (ChatId, Symbol, EntryPrice, StopLoss, TakeProfit, Status, Side, Leverage,
             TradeMode, ExchangeCode, ExchangeAccountId)
        VALUES ($c, $s, $e, $sl, $tp, 'Open', $side, $lev, $mode, $exchange, $account)";
        cmd.Parameters.AddWithValue("$c", chatId);
        cmd.Parameters.AddWithValue("$s", symbol);
        cmd.Parameters.AddWithValue("$e", entry);
        cmd.Parameters.AddWithValue("$sl", sl);
        cmd.Parameters.AddWithValue("$tp", tp);
        cmd.Parameters.AddWithValue("$side", side);
        cmd.Parameters.AddWithValue("$lev", leverage);
        cmd.Parameters.AddWithValue("$mode", tradeMode);
        cmd.Parameters.AddWithValue("$exchange", exchangeCode);
        cmd.Parameters.AddWithValue("$account", exchangeAccountId is null ? DBNull.Value : exchangeAccountId.Value);
        cmd.ExecuteNonQuery();
    }

    static List<OpenTradeRow> GetOpenTrades(long? chatId = null)
    {
        var list = new List<OpenTradeRow>();
        using var conn = new SqliteConnection(DatabaseConnectionString);
        conn.Open();
        var cmd = conn.CreateCommand();

        if (chatId.HasValue)
        {
            cmd.CommandText = @"SELECT Id, ChatId, Symbol, EntryPrice, StopLoss, TakeProfit,
                                       IFNULL(Side,'LONG'), IFNULL(Leverage,1),
                                       IFNULL(TradeMode,'MARGIN'), IFNULL(ExchangeCode,'NOBITEX'), ExchangeAccountId, ExitReason,
                                       IFNULL(Quantity,'0'), IFNULL(OcoStatus,'NotSubmitted')
                            FROM OpenTrades WHERE Status='Open' AND ChatId=$c";
            cmd.Parameters.AddWithValue("$c", chatId.Value);
        }
        else
        {
            cmd.CommandText = @"SELECT Id, ChatId, Symbol, EntryPrice, StopLoss, TakeProfit,
                                       IFNULL(Side,'LONG'), IFNULL(Leverage,1),
                                       IFNULL(TradeMode,'MARGIN'), IFNULL(ExchangeCode,'NOBITEX'), ExchangeAccountId, ExitReason,
                                       IFNULL(Quantity,'0'), IFNULL(OcoStatus,'NotSubmitted')
                            FROM OpenTrades WHERE Status='Open'";
        }

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new OpenTradeRow
            {
                Id = reader.GetInt32(0),
                ChatId = reader.GetInt64(1),
                Symbol = reader.GetString(2),
                EntryPrice = reader.GetDecimal(3),
                StopLoss = reader.GetDecimal(4),
                TakeProfit = reader.GetDecimal(5),
                Side = reader.GetString(6),
                Leverage = Convert.ToInt32(reader.GetValue(7)),
                TradeMode = reader.GetString(8),
                ExchangeCode = reader.GetString(9),
                ExchangeAccountId = reader.IsDBNull(10) ? null : reader.GetInt64(10),
                ExitReason = reader.IsDBNull(11) ? null : reader.GetString(11),
                Quantity = decimal.TryParse(reader.GetString(12), System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out var quantity) ? quantity : 0m,
                OcoStatus = reader.GetString(13)
            });
        }
        return list;
    }

    static void CloseTrade(int id, decimal exitPrice, string reason)
    {
        using var conn = new SqliteConnection(DatabaseConnectionString);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
        UPDATE OpenTrades
        SET Status='Closed', ExitPrice=$p, ExitReason=$r, ClosedAt=CURRENT_TIMESTAMP
        WHERE Id=$id";
        cmd.Parameters.AddWithValue("$p", exitPrice);
        cmd.Parameters.AddWithValue("$r", reason);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    static void MarkExitSuggested(int id, string trigger)
    {
        using var conn = new SqliteConnection(DatabaseConnectionString);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE OpenTrades
SET ExitReason=$reason
WHERE Id=$id AND Status='Open' AND (ExitReason IS NULL OR ExitReason NOT LIKE 'ExitSuggested:%');";
        cmd.Parameters.AddWithValue("$reason", $"ExitSuggested:{trigger}");
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    static async Task CheckOpenTrades(BinanceRestClient client, TelegramBotClient bot)
    {
        var trades = GetOpenTrades(); // همه کاربران، Status = Open

        foreach (var t in trades)
        {
            try
            {
                decimal? live = await GetMarketLastPriceAsync(t.ExchangeCode, t.Symbol);
                await Task.Delay(150);

                if (live is null || live <= 0)
                    continue;

                decimal price = live.Value;
                string side = string.IsNullOrWhiteSpace(t.Side) ? "LONG" : t.Side.ToUpperInvariant();

                decimal pnlPercent = side == "SHORT"
                    ? (t.EntryPrice - price) / t.EntryPrice * 100
                    : (price - t.EntryPrice) / t.EntryPrice * 100;

                bool hitSl;
                bool hitTp;

                if (side == "SHORT")
                {
                    hitSl = price >= t.StopLoss;
                    hitTp = price <= t.TakeProfit;
                }
                else
                {
                    hitSl = price <= t.StopLoss;
                    hitTp = price >= t.TakeProfit;
                }

                if (hitSl || hitTp)
                {
                    if (t.ExitReason?.StartsWith("ExitSuggested:", StringComparison.Ordinal) == true)
                        continue;

                    string trigger = hitSl ? "STOP LOSS" : "TAKE PROFIT";
                    MarkExitSuggested(t.Id, trigger);
                    var keyboard = new InlineKeyboardMarkup(new[]
                    {
                        new[]
                        {
                            InlineKeyboardButton.WithCallbackData(
                                "✅ بعد از خروج دستی ثبت کن", $"sold|{t.Id}")
                        }
                    });

                    await bot.SendMessage(t.ChatId,
                        $"{(hitSl ? "🛑" : "🎯")} {trigger} لمس شد\n" +
                        $"{t.TradeMode} {side} {t.Symbol}\n" +
                        $"Entry: {t.EntryPrice:N4}\nNow: {price:N4}\n" +
                        $"PnL: {pnlPercent:N2}%\n\n" +
                        "پوزیشن هنوز در دفتر باز است. ابتدا در صرافی به‌صورت دستی خارج شو؛ سپس ثبت خروج را بزن.",
                        replyMarkup: MainReplyKeyboard());
                    continue;
                }
                else
                {
                    // هشدار نزدیک بودن — بدون بستن معامله
                    if (pnlPercent <= -1.0m)
                    {
                        await bot.SendMessage(t.ChatId,
                            $"⚠️ نزدیک حد ضرر\n" +
                            $"{side} {t.Symbol}\n" +
                            $"Now: {price:N4}\n" +
                            $"PnL: {pnlPercent:N2}%\n" +
                            $"SL: {t.StopLoss:N4} | TP: {t.TakeProfit:N4}");
                    }
                    else if (pnlPercent >= 2.0m)
                    {
                        var keyboard = new InlineKeyboardMarkup(new[]
                        {
                        new[]
                        {
                            InlineKeyboardButton.WithCallbackData("✅ خروج دستی را ثبت کن", $"sold|{t.Id}")
                        }
                    });

                        await bot.SendMessage(t.ChatId,
                            $"📈 در سود\n" +
                            $"{side} {t.Symbol}\n" +
                            $"Now: {price:N4}\n" +
                            $"PnL: {pnlPercent:N2}%\n" +
                            $"SL: {t.StopLoss:N4} | TP: {t.TakeProfit:N4}",
                            replyMarkup: MainReplyKeyboard());
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"CheckOpenTrades {t.Symbol}: {ex.Message}");
            }
        }
    }

    // ==================== داده و اندیکاتور ====================

    static async Task<List<string>> GetTopVolumeSymbols(BinanceRestClient client)
    {
        var nobitex = await GetNobitexUsdtSymbolsAsync();

        var tickersResult = await client.SpotApi.ExchangeData.GetTickersAsync();
        if (!tickersResult.Success)
            return nobitex.Take(20).ToList();

        var stablecoins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "USDCUSDT","TUSDUSDT","BUSDUSDT","DAIUSDT","FDUSDUSDT",
        "USDPUSDT","USDDUSDT","USD1USDT","EURUSDT","AEURUSDT","RLUSDUSDT"
    };

        return tickersResult.Data
            .Where(t => t.Symbol.EndsWith("USDT"))
            .Where(t => nobitex.Contains(t.Symbol))
            .Where(t => !stablecoins.Contains(t.Symbol))
            .Where(t => !t.Symbol.Contains("UP") && !t.Symbol.Contains("DOWN")
                     && !t.Symbol.Contains("BULL") && !t.Symbol.Contains("BEAR"))
            .OrderByDescending(t => t.QuoteVolume)
            .Take(20)
            .Select(t => t.Symbol)
            .ToList();
    }

    static async Task<List<Quote>?> GetNobitexQuotesAsync(string symbol, string resolution, int candleCount)
    {
        // symbol مثل BTCUSDT یا BTCIRT
        // resolution: 15 = 15m ، 60 = 1h
        try
        {
            long to = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            // تقریبی: برای 15m و 60m
            int minutes = resolution == "60" ? 60 : 15;
            long from = to - (long)candleCount * minutes * 60;

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var url =
                $"https://apiv2.nobitex.ir/market/udf/history?symbol={symbol}&resolution={resolution}&from={from}&to={to}";

            var json = await http.GetStringAsync(url);
            using var doc = System.Text.Json.JsonDocument.Parse(json);

            // فرمت UDF: s, t[], o[], h[], l[], c[], v[]
            if (!doc.RootElement.TryGetProperty("s", out var status) ||
                status.GetString() != "ok")
                return null;

            var t = doc.RootElement.GetProperty("t");
            var o = doc.RootElement.GetProperty("o");
            var h = doc.RootElement.GetProperty("h");
            var l = doc.RootElement.GetProperty("l");
            var c = doc.RootElement.GetProperty("c");
            var v = doc.RootElement.GetProperty("v");

            int n = t.GetArrayLength();
            var quotes = new List<Quote>(n);

            for (int i = 0; i < n; i++)
            {
                quotes.Add(new Quote
                {
                    Date = DateTimeOffset.FromUnixTimeSeconds(t[i].GetInt64()).UtcDateTime,
                    Open = (decimal)o[i].GetDouble(),
                    High = (decimal)h[i].GetDouble(),
                    Low = (decimal)l[i].GetDouble(),
                    Close = (decimal)c[i].GetDouble(),
                    Volume = (decimal)v[i].GetDouble()
                });
            }

            // UDF timestamps identify candle-open time. Never feed the still-forming
            // 15m/1h candle into indicators; otherwise historical signals repaint.
            DateTime utcNow = DateTime.UtcNow;
            return quotes
                .Where(q => StrategyRules.IsCandleClosed(q.Date, TimeSpan.FromMinutes(minutes), utcNow))
                .OrderBy(q => q.Date)
                .ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Nobitex candles {symbol} {resolution}: {ex.Message}");
            return null;
        }
    }

    static async Task<decimal?> GetNobitexLastPriceAsync(string symbol)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var json = await http.GetStringAsync($"https://apiv2.nobitex.ir/v3/orderbook/{symbol}");
            using var doc = System.Text.Json.JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("lastTradePrice", out var ltp) &&
                decimal.TryParse(ltp.GetString(),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var price))
                return price;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Nobitex price {symbol}: {ex.Message}");
        }
        return null;
    }

    static Task<decimal?> GetMarketLastPriceAsync(string exchangeCode, string symbol) =>
        exchangeCode.Equals(SignalSourcePreference.Binance, StringComparison.OrdinalIgnoreCase)
            ? BinancePublicMarket.GetLastPriceAsync(symbol)
            : exchangeCode.Equals(SignalSourcePreference.Nobitex, StringComparison.OrdinalIgnoreCase)
                ? GetNobitexLastPriceAsync(symbol)
                : Task.FromResult<decimal?>(null);

    static async Task<List<Quote>?> GetQuotes(BinanceRestClient client, string symbol, KlineInterval interval, int limit)
    {
        var result = await client.SpotApi.ExchangeData.GetKlinesAsync(symbol, interval, limit: limit);
        if (!result.Success) return null;
        return [.. result.Data.Select(k => new Quote
        {
            Date = k.OpenTime,
            Open = k.OpenPrice,
            High = k.HighPrice,
            Low = k.LowPrice,
            Close = k.ClosePrice,
            Volume = k.Volume
        })];
    }

    static TfResult AnalyzeTimeframe(List<Skender.Stock.Indicators.Quote> quotes)
    {
        if (quotes.Count < 50)
            return new TfResult { IsValid = false };

        var rsiList = quotes.GetRsi(14).ToList();
        var macdList = quotes.GetMacd(12, 26, 9).ToList();
        var sma20 = quotes.GetSma(20).LastOrDefault()?.Sma;
        var sma50 = quotes.GetSma(50).LastOrDefault()?.Sma;
        var last = quotes.Last().Close;

        var rsiValue = rsiList.LastOrDefault()?.Rsi;
        var macd = macdList.LastOrDefault();
        if (rsiValue is null || macd?.Macd is null || macd.Signal is null ||
            sma20 is null || sma50 is null || last <= 0)
            return new TfResult { IsValid = false };

        double rsi = rsiValue.Value;
        double macdVal = macd.Macd.Value;
        double signal = macd.Signal.Value;
        if (!double.IsFinite(rsi) || !double.IsFinite(macdVal) || !double.IsFinite(signal))
            return new TfResult { IsValid = false };

        int buy = 0, sell = 0;

        if (rsi < 30) buy += 2;
        else if (rsi < 45) buy += 1;
        if (rsi > 70) sell += 2;
        else if (rsi > 55) sell += 1;

        if (macdVal > signal) buy += 1;
        else sell += 1;

        if (sma20 is not null && last > (decimal)sma20) buy += 1;
        if (sma50 is not null && last > (decimal)sma50) buy += 1;
        if (sma20 is not null && last < (decimal)sma20) sell += 1;
        if (sma50 is not null && last < (decimal)sma50) sell += 1;

        return new TfResult { IsValid = true, Rsi = rsi, BuyScore = buy, SellScore = sell };
    }

    static (decimal Support, decimal Resistance) FindSupportResistance(List<Quote> quotes)
    {
        // از تعداد بیشتری کندل برای سطوح استاتیک استفاده کن
        var candles = quotes.Count > 120 ? quotes.TakeLast(120).ToList() : quotes.ToList();
        if (candles.Count < 30)
            return (0, 0);

        decimal currentPrice = candles.Last().Close;
        const int swingStrength = 2;          // هر طرف 2 کندل
        const decimal clusterPercent = 0.004m; // 0.4% → یکی حساب شوند

        var swingLows = new List<decimal>();
        var swingHighs = new List<decimal>();

        for (int i = swingStrength; i < candles.Count - swingStrength - 1; i++)
        {
            bool isLow = true;
            bool isHigh = true;

            for (int j = 1; j <= swingStrength; j++)
            {
                if (candles[i].Low >= candles[i - j].Low || candles[i].Low >= candles[i + j].Low)
                    isLow = false;
                if (candles[i].High <= candles[i - j].High || candles[i].High <= candles[i + j].High)
                    isHigh = false;
            }

            if (isLow) swingLows.Add(candles[i].Low);
            if (isHigh) swingHighs.Add(candles[i].High);
        }

        // ادغام سطوح نزدیک به هم (سطح استاتیک)
        var staticSupports = ClusterLevels(swingLows, clusterPercent);
        var staticResistances = ClusterLevels(swingHighs, clusterPercent);

        // نزدیک‌ترین حمایت زیر قیمت
        decimal support = staticSupports
            .Where(x => x < currentPrice)
            .OrderByDescending(x => x)
            .FirstOrDefault();

        // نزدیک‌ترین مقاومت بالای قیمت
        decimal resistance = staticResistances
            .Where(x => x > currentPrice)
            .OrderBy(x => x)
            .FirstOrDefault();

        // fallback اگر سطحی پیدا نشد
        if (support <= 0)
            support = candles.Take(candles.Count - 1).Min(c => c.Low);
        if (resistance <= 0)
            resistance = candles.Take(candles.Count - 1).Max(c => c.High);

        return (support, resistance);
    }

    /// <summary>
    /// قیمت‌های نزدیک را به یک سطح استاتیک تبدیل می‌کند
    /// </summary>
    static List<decimal> ClusterLevels(List<decimal> levels, decimal clusterPercent)
    {
        if (levels.Count == 0) return new List<decimal>();
        var sorted = levels.OrderBy(x => x).ToList();
        var clusters = new List<List<decimal>>();
        var current = new List<decimal> { sorted[0] };

        for (int i = 1; i < sorted.Count; i++)
        {
            decimal refPrice = current.Average();
            decimal diff = Math.Abs(sorted[i] - refPrice) / refPrice;

            if (diff <= clusterPercent)
            {
                current.Add(sorted[i]);
            }
            else
            {
                clusters.Add(current);
                current = new List<decimal> { sorted[i] };
            }
        }
        clusters.Add(current);

        // میانگین هر خوشه = یک سطح استاتیک
        // اگر چند برخورد داشته باشد معتبرتر است؛ اینجا همه را برمی‌گردانیم
        return [.. clusters
            .Select(c => c.Average())
            .OrderBy(x => x)];
    }

    static decimal CalcPnlPercent(string side, decimal entry, decimal exit)
    {
        if (entry <= 0) return 0;
        side = string.IsNullOrWhiteSpace(side) ? "LONG" : side.ToUpperInvariant();
        return side == "SHORT"
            ? (entry - exit) / entry * 100m
            : (exit - entry) / entry * 100m;
    }

    // ==================== GetPendingSignals ====================
    static List<SignalTrack> GetPendingSignals()
    {
        var list = new List<SignalTrack>();
        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(DatabaseConnectionString);
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT Id,
       Symbol,
       IFNULL(Side, 'LONG'),
       IFNULL(TradeMode, 'MARGIN'),
       EntryPrice,
       StopLoss,
       TakeProfit,
       Status,
       SignalAt,
       ResolvePrice,
       IFNULL(ExchangeCode, 'NOBITEX')
FROM SignalTracks
WHERE Status = 'Pending';";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new SignalTrack
                {
                    Id = r.GetInt64(0),
                    Symbol = r.GetString(1),
                    Side = r.IsDBNull(2) ? "LONG" : r.GetString(2),
                    TradeMode = r.GetString(3),
                    Entry = r.GetDecimal(4),
                    StopLoss = r.GetDecimal(5),
                    TakeProfit = r.GetDecimal(6),
                    Status = r.GetString(7),
                    SignalAt = DateTime.Parse(r.GetString(8), null,
                        System.Globalization.DateTimeStyles.RoundtripKind),
                    ExitPrice = r.IsDBNull(9) ? null : r.GetDecimal(9),
                    ExchangeCode = r.GetString(10)
                });
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"GetPendingSignals: {ex.Message}");
        }
        return list;
    }

    // ==================== UpdateSignal ====================
    static void UpdateSignal(long id, string status, decimal? exitPrice)
    {
        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(DatabaseConnectionString);
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
UPDATE SignalTracks
SET Status = $st,
    ResolvePrice = $ex,
    ResolvedAt = $rt
WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$st", status);
            cmd.Parameters.AddWithValue("$ex", exitPrice is null ? DBNull.Value : exitPrice.Value);
            cmd.Parameters.AddWithValue("$rt", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"UpdateSignal: {ex.Message}");
        }
    }

    // ==================== ResolvePendingSignals ====================
    static async Task ResolvePendingSignals()
    {
        var pending = GetPendingSignals();

        foreach (var s in pending)
        {
            try
            {
                var live = await GetMarketLastPriceAsync(s.ExchangeCode, s.Symbol);
                if (live is null || live <= 0)
                {
                    var unavailableOutcome = StrategyRules.EvaluateSignal(
                        s.Side, 0m, s.StopLoss, s.TakeProfit, s.SignalAt,
                        DateTime.UtcNow, TimeSpan.FromHours(24));
                    if (unavailableOutcome == SignalOutcome.Expired)
                    {
                        UpdateSignal(s.Id, "Expired", null);
                        LogSignalEvaluation(s.Id, null, "RESOLVED_EXPIRED",
                            "Signal lifetime exceeded 24 hours; no reliable market price was available at expiry.");
                        Console.WriteLine($"Signal Expired without exit price: {s.Side} {s.Symbol}");
                        continue;
                    }
                    LogSignalEvaluation(s.Id, null, "PRICE_UNAVAILABLE",
                        $"Could not retrieve ticker from {s.ExchangeCode}; signal remains pending.");
                    Console.WriteLine($"Signal #{s.Id} check unavailable: {s.ExchangeCode}/{s.Symbol}");
                    continue;
                }
                decimal price = live.Value;
                string side = string.IsNullOrWhiteSpace(s.Side) ? "LONG" : s.Side.ToUpperInvariant();

                var outcome = StrategyRules.EvaluateSignal(
                    side, price, s.StopLoss, s.TakeProfit, s.SignalAt,
                    DateTime.UtcNow, TimeSpan.FromHours(24));

                LogSignalEvaluation(s.Id, price, outcome.ToString(),
                    $"Side={side}; Entry={s.Entry:N8}; SL={s.StopLoss:N8}; TP={s.TakeProfit:N8}; Exchange={s.ExchangeCode}");

                // If both thresholds were crossed between polls, intrabar order is
                // unknowable from a ticker snapshot; classify conservatively as SL.
                if (outcome == SignalOutcome.Expired)
                {
                    UpdateSignal(s.Id, "Expired", price);
                    LogSignalEvaluation(s.Id, price, "RESOLVED_EXPIRED", "Signal lifetime exceeded 24 hours.");
                    Console.WriteLine($"Signal Expired: {side} {s.Symbol} @ {price:N4}");
                }
                else if (outcome == SignalOutcome.HitStopLoss)
                {
                    UpdateSignal(s.Id, "HitSL", price);
                    LogSignalEvaluation(s.Id, price, "RESOLVED_HIT_SL", "Stop-loss threshold reached.");
                    Console.WriteLine($"Signal HitSL: {side} {s.Symbol} @ {price:N4}");
                }
                else if (outcome == SignalOutcome.HitTakeProfit)
                {
                    UpdateSignal(s.Id, "HitTP", price);
                    LogSignalEvaluation(s.Id, price, "RESOLVED_HIT_TP", "Take-profit threshold reached.");
                    Console.WriteLine($"Signal HitTP: {side} {s.Symbol} @ {price:N4}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Resolve signal {s.Symbol}: {ex.Message}");
            }
        }
    }

    // ==================== GetSignalReport ====================
    static string GetSignalReport()
    {
        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(DatabaseConnectionString);
            conn.Open();

            int total = 0, hitTp = 0, hitSl = 0, pending = 0, expired = 0;
            var pnls = new List<decimal>();
            var lines = new List<string>();

            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT Id, Symbol, IFNULL(Side,'LONG'), IFNULL(ExchangeCode,'NOBITEX'), EntryPrice, StopLoss, TakeProfit,
       Status, SignalAt, ResolvePrice,
       (SELECT COUNT(1) FROM SignalEvaluationLogs e WHERE e.SignalTrackId=SignalTracks.Id) AS Checks,
       (SELECT MAX(CheckedAt) FROM SignalEvaluationLogs e WHERE e.SignalTrackId=SignalTracks.Id) AS LastCheckedAt
FROM SignalTracks
ORDER BY Id DESC
LIMIT 200;";

            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    total++;
                    string symbol = r.GetString(1);
                    string side = r.IsDBNull(2) ? "LONG" : r.GetString(2);
                    string exchangeCode = r.GetString(3);
                    decimal entry = r.GetDecimal(4);
                    decimal sl = r.GetDecimal(5);
                    decimal tp = r.GetDecimal(6);
                    string status = r.GetString(7);
                    string at = r.GetString(8);
                    decimal? exit = r.IsDBNull(9) ? null : r.GetDecimal(9);
                    long checks = r.IsDBNull(10) ? 0 : r.GetInt64(10);
                    string lastChecked = r.IsDBNull(11) ? "not checked" : r.GetString(11);

                    switch (status)
                    {
                        case "HitTP": hitTp++; break;
                        case "HitSL": hitSl++; break;
                        case "Pending": pending++; break;
                        case "Expired": expired++; break;
                    }

                    if ((status == "HitTP" || status == "HitSL") && exit is not null)
                        pnls.Add(CalcPnlPercent(side, entry, exit.Value));

                    if (lines.Count < 15)
                    {
                        lines.Add(
                            $"{exchangeCode} | {symbol} | {side} | E:{entry:N4} | TP:{tp:N4} | SL:{sl:N4} | {status} | checks:{checks} | last:{lastChecked}");
                    }
                }
            }

            int decided = hitTp + hitSl;
            double winRate = decided > 0 ? (double)hitTp / decided * 100.0 : 0;
            decimal avgPnl = pnls.Count > 0 ? pnls.Average() : 0;

            return
                "📊 گزارش سیگنال‌ها\n" +
                $"کل: {total}\n" +
                $"✅ HitTP: {hitTp}\n" +
                $"🛑 HitSL: {hitSl}\n" +
                $"⏳ Pending: {pending}\n" +
                $"⌛ Expired: {expired}\n" +
                $"نرخ موفقیت (TP/تعیین‌تکلیف): {winRate:N1}%\n" +
                $"میانگین PnL فرضی: {avgPnl:N2}%\n\n" +
                "آخرین موارد:\n" +
                string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            return $"خطا در گزارش: {ex.Message}";
        }
    }

    // ==================== GetPerformanceStats (معاملات باز/بسته کاربر) ====================
    static string GetPerformanceStats(long chatId)
    {
        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(DatabaseConnectionString);
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT Symbol, IFNULL(Side,'LONG'), EntryPrice, StopLoss, TakeProfit, Status, ExitPrice, IFNULL(Leverage,1)
FROM OpenTrades
WHERE ChatId=$c
ORDER BY Id DESC
LIMIT 50;";
            cmd.Parameters.AddWithValue("$c", chatId);

            int open = 0, closed = 0;
            var pnls = new List<decimal>();
            var lines = new List<string>();

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string symbol = r.GetString(0);
                string side = r.IsDBNull(1) ? "LONG" : r.GetString(1);
                decimal entry = r.GetDecimal(2);
                decimal sl = r.GetDecimal(3);
                decimal tp = r.GetDecimal(4);
                string status = r.GetString(5);
                decimal? exit = r.IsDBNull(6) ? null : r.GetDecimal(6);
                int lev = r.IsDBNull(7) ? 1 : Convert.ToInt32(r.GetValue(7));

                if (status == "Open") open++;
                else closed++;

                string pnlTxt = "-";
                if (exit is not null && status != "Open")
                {
                    var pnl = CalcPnlPercent(side, entry, exit.Value);
                    pnls.Add(pnl);
                    pnlTxt = $"{pnl:N2}%";
                }

                lines.Add($"{side} {symbol} | E:{entry:N4} | {status} | PnL:{pnlTxt} | lev {lev}x");
            }

            decimal avg = pnls.Count > 0 ? pnls.Average() : 0;
            return
                "📈 آمار معاملات شما\n" +
                $"باز: {open} | بسته: {closed}\n" +
                $"میانگین PnL بسته‌شده: {avg:N2}%\n\n" +
                string.Join("\n", lines.Take(20));
        }
        catch (Exception ex)
        {
            return $"خطا در آمار: {ex.Message}";
        }
    }

    class OpenTradeRow
    {
        public int Id { get; set; }
        public long ChatId { get; set; }
        public string Symbol { get; set; } = "";
        public decimal EntryPrice { get; set; }
        public decimal StopLoss { get; set; }
        public decimal TakeProfit { get; set; }
        public string Side { get; set; } = "LONG";
        public int Leverage { get; set; } = 1;
        public string TradeMode { get; set; } = "MARGIN";
        public string ExchangeCode { get; set; } = "NOBITEX";
        public long? ExchangeAccountId { get; set; }
        public string? ExitReason { get; set; }
        public decimal Quantity { get; set; }
        public string OcoStatus { get; set; } = "NotSubmitted";
    }

    class SignalTrack
    {
        public long Id { get; set; }
        public string Symbol { get; set; } = "";
        public string Side { get; set; } = "LONG";
        public string TradeMode { get; set; } = "MARGIN";
        public decimal Entry { get; set; }
        public decimal StopLoss { get; set; }
        public decimal TakeProfit { get; set; }
        public string Status { get; set; } = "Pending";
        public DateTime SignalAt { get; set; }
        public decimal? ExitPrice { get; set; }
        public string ExchangeCode { get; set; } = "NOBITEX";
    }

    class NobitexOrderResult
    {
        public bool Ok { get; set; }
        public string Error { get; set; } = "";
        public long? OrderId { get; set; }
        public decimal? FillPrice { get; set; }
        public string Raw { get; set; } = "";
    }

    class PendingOrder
    {
        public string Side { get; set; } = "LONG";
        public string Symbol { get; set; } = "";
        public decimal Entry { get; set; }
        public decimal StopLoss { get; set; }
        public decimal TakeProfit { get; set; }
        public int Leverage { get; set; } = 1;
        public decimal? BudgetToman { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    class TfResult
    {
        public bool IsValid { get; set; }
        public double Rsi { get; set; }
        public int BuyScore { get; set; }
        public int SellScore { get; set; }
    }
}
