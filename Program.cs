using Binance.Net.Clients;
using Binance.Net.Enums;
using Microsoft.Data.Sqlite;
using Skender.Stock.Indicators;
using Telegram.Bot;
using Telegram.Bot.Types.ReplyMarkups;

class Program
{
    const string OllamaModel = "llama3.2:latest";
    const string OllamaUrl = "http://127.0.0.1:11434";
    const string TelegramToken = "8651996991:AAFn4VOg06S6SM5G82Cni5kygjyi_i-CdE0";

    static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("Crypto Analyzer - Multi User");
        Console.WriteLine("SELL only for open trades | Users must /start");
        Console.WriteLine("Press Ctrl+C to stop\n");

        InitializeDatabase();

        var restClient = new BinanceRestClient();
        var botClient = new TelegramBotClient(TelegramToken);

        // گوش دادن به دکمه و دستورهای تلگرام
        _ = Task.Run(() => ListenTelegramCommands(botClient, restClient));

        while (true)
        {
            try
            {
                Console.WriteLine($"\n===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");

                // 1) بررسی حد ضرر / هدف سود معاملات باز همه کاربران
                await CheckOpenTrades(restClient, botClient);

                await ResolvePendingSignals(restClient);

                // 2) لیست ارزهای پرمعامله
                var symbols = await GetTopVolumeSymbols(restClient);
                Console.WriteLine($"Analyzing {symbols.Count} symbols...\n");

                // 3) فقط سیگنال‌های BUY (پیشنهاد ورود)
                var buySignals = new List<(string Text, InlineKeyboardMarkup? Keyboard)>();

                foreach (var symbol in symbols)
                {
                    var signal = await AnalyzeSymbol(restClient, symbol);
                    if (signal != null)
                        buySignals.Add(signal.Value);

                    await Task.Delay(500);
                }

                // 4) کاربران فعال
                var users = GetActiveChatIds();
                Console.WriteLine($"Active users: {users.Count}");
                Console.WriteLine($"BUY signals: {buySignals.Count}");

                foreach (var chatId in users)
                {
                    // 4-a) ارسال پیشنهادهای خرید به همه
                    foreach (var (text, keyboard) in buySignals)
                    {
                        try
                        {
                            await botClient.SendMessage(chatId, text, replyMarkup: keyboard);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Send BUY fail {chatId}: {ex.Message}");
                        }
                        await Task.Delay(50);
                    }

                    // 4-b) SELL فقط برای ارزهایی که این کاربر خرید کرده و معامله باز دارد
                    var openTrades = GetOpenTrades(chatId);
                    foreach (var trade in openTrades)
                    {
                        try
                        {
                            var sellText = await AnalyzeSellForOpenTrade(restClient, trade.Symbol);
                            if (string.IsNullOrEmpty(sellText))
                                continue;

                            var sellKeyboard = new InlineKeyboardMarkup(new[]
                            {
                            new[]
                            {
                                InlineKeyboardButton.WithCallbackData("✅ فروختم", $"sold|{trade.Symbol}")
                            }
                        });

                            await botClient.SendMessage(chatId, sellText, replyMarkup: sellKeyboard);
                            Console.WriteLine($"SELL sent to {chatId} for {trade.Symbol}");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Send SELL fail {chatId}/{trade.Symbol}: {ex.Message}");
                        }

                        await Task.Delay(300);
                    }
                }

                if (buySignals.Count == 0)
                    Console.WriteLine("No BUY signals this round.");

                Console.WriteLine($"Next run at {DateTime.Now.AddMinutes(5):HH:mm}");
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

            await Task.Delay(TimeSpan.FromMinutes(5));
        }
    }

    static async Task<HashSet<string>> GetNobitexUsdtSymbolsAsync()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
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

    static void TrackSignal(string symbol, decimal entry, decimal sl, decimal tp)
    {
        using var conn = new SqliteConnection("Data Source=crypto_analyzer.db");
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
        INSERT INTO SignalTracks (Symbol, EntryPrice, StopLoss, TakeProfit, Status)
        VALUES ($s, $e, $sl, $tp, 'Pending')";
        cmd.Parameters.AddWithValue("$s", symbol);
        cmd.Parameters.AddWithValue("$e", entry);
        cmd.Parameters.AddWithValue("$sl", sl);
        cmd.Parameters.AddWithValue("$tp", tp);
        cmd.ExecuteNonQuery();
    }

    // ==================== خواندن Pending ====================

    static List<SignalTrack> GetPendingSignals()
    {
        var list = new List<SignalTrack>();
        using var conn = new SqliteConnection("Data Source=crypto_analyzer.db");
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
        SELECT Id, Symbol, EntryPrice, StopLoss, TakeProfit, SignalAt
        FROM SignalTracks
        WHERE Status = 'Pending'";

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            DateTime signalAt;
            var raw = r.GetString(5);
            if (!DateTime.TryParse(raw, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out signalAt))
                signalAt = DateTime.UtcNow;

            list.Add(new SignalTrack
            {
                Id = r.GetInt32(0),
                Symbol = r.GetString(1),
                EntryPrice = r.GetDecimal(2),
                StopLoss = r.GetDecimal(3),
                TakeProfit = r.GetDecimal(4),
                SignalAt = signalAt
            });
        }
        return list;
    }

    // ==================== به‌روزرسانی وضعیت ====================

    static void UpdateSignal(int id, string status, decimal resolvePrice)
    {
        using var conn = new SqliteConnection("Data Source=crypto_analyzer.db");
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
        UPDATE SignalTracks
        SET Status = $st,
            ResolvedAt = CURRENT_TIMESTAMP,
            ResolvePrice = $p
        WHERE Id = $id";
        cmd.Parameters.AddWithValue("$st", status);
        cmd.Parameters.AddWithValue("$p", resolvePrice);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    // ==================== چک کردن رسیدن به TP / SL ====================

    static async Task ResolvePendingSignals(BinanceRestClient client)
    {
        var pending = GetPendingSignals();

        foreach (var s in pending)
        {
            try
            {
                var ticker = await client.SpotApi.ExchangeData.GetTickerAsync(s.Symbol);
                await Task.Delay(150);
                if (!ticker.Success) continue;

                decimal price = ticker.Data.LastPrice;

                if (price >= s.TakeProfit)
                {
                    UpdateSignal(s.Id, "HitTP", price);
                    Console.WriteLine($"Signal HitTP: {s.Symbol} @ {price:N4}");
                }
                else if (price <= s.StopLoss)
                {
                    UpdateSignal(s.Id, "HitSL", price);
                    Console.WriteLine($"Signal HitSL: {s.Symbol} @ {price:N4}");
                }
                else if (DateTime.UtcNow - s.SignalAt.ToUniversalTime() > TimeSpan.FromHours(24))
                {
                    UpdateSignal(s.Id, "Expired", price);
                    Console.WriteLine($"Signal Expired: {s.Symbol} @ {price:N4}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Resolve signal {s.Symbol}: {ex.Message}");
            }
        }
    }

    // ==================== گزارش /report ====================

    static string GetSignalReport()
    {
        using var conn = new SqliteConnection("Data Source=crypto_analyzer.db");
        conn.Open();

        int hitTp = 0, hitSl = 0, pending = 0, expired = 0, total = 0;

        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Status, COUNT(1) FROM SignalTracks GROUP BY Status";

        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                string st = r.GetString(0);
                int c = r.GetInt32(1);
                total += c;
                if (st == "HitTP") hitTp = c;
                else if (st == "HitSL") hitSl = c;
                else if (st == "Pending") pending = c;
                else if (st == "Expired") expired = c;
            }
        }

        int resolved = hitTp + hitSl;
        double winRate = resolved > 0 ? (double)hitTp / resolved * 100.0 : 0;

        // میانگین سود/ضرر فرضی روی موارد بسته شده
        cmd = conn.CreateCommand();
        cmd.CommandText = @"
        SELECT Status, EntryPrice, ResolvePrice
        FROM SignalTracks
        WHERE Status IN ('HitTP', 'HitSL')";

        double sumPnl = 0;
        int pnlCount = 0;
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                string st = r.GetString(0);
                decimal entry = r.GetDecimal(1);
                decimal resolve = r.GetDecimal(2);
                if (entry <= 0) continue;

                double pnl = (double)((resolve - entry) / entry * 100m);
                sumPnl += pnl;
                pnlCount++;
            }
        }

        double avgPnl = pnlCount > 0 ? sumPnl / pnlCount : 0;

        var lines = new List<string>
    {
        "📊 گزارش سیگنال‌های BUY",
        $"کل سیگنال‌ها: {total}",
        $"✅ رسید به TP: {hitTp}",
        $"🛑 خورد به SL: {hitSl}",
        $"⏳ در انتظار: {pending}",
        $"⌛ منقضی (۲۴س): {expired}",
        resolved > 0
            ? $"نرخ موفقیت (TP / تعیین‌تکلیف‌شده): {winRate:N1}%"
            : "هنوز مورد تعیین‌تکلیف‌شده کافی نیست",
        pnlCount > 0
            ? $"میانگین PnL فرضی: {avgPnl:N2}%"
            : "میانگین PnL: -",
        "",
        "آخرین ۱۵ سیگنال:"
    };

        cmd = conn.CreateCommand();
        cmd.CommandText = @"
        SELECT Symbol, EntryPrice, TakeProfit, StopLoss, Status, SignalAt
        FROM SignalTracks
        ORDER BY Id DESC
        LIMIT 15";

        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                lines.Add(
                    $"{r.GetString(0)} | E:{r.GetDecimal(1):N4} | TP:{r.GetDecimal(2):N4} | " +
                    $"SL:{r.GetDecimal(3):N4} | {r.GetString(4)} | {r.GetString(5)}");
            }
        }

        if (total == 0)
            return "📊 هنوز سیگنالی ثبت نشده است.";

        return string.Join("\n", lines);
    }

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
                        var answer = await HandleCallback(chatId, cb.Data ?? "", restClient);
                        await bot.AnswerCallbackQuery(cb.Id, answer.Length > 180 ? answer[..180] : answer);
                        await bot.SendMessage(chatId, answer);
                        continue;
                    }

                    var msg = update.Message;
                    if (msg?.Text is null) continue;

                    long userChatId = msg.Chat.Id;
                    string text = msg.Text.Trim();

                    // عضویت
                    if (text.StartsWith("/start"))
                    {
                        RegisterUser(userChatId, msg.From?.Username, msg.From?.FirstName);
                        await bot.SendMessage(userChatId,
                            "خوش آمدید ✅\nسیگنال‌ها برایتان ارسال می‌شود.\n\nدستورها:\n/start - فعال‌سازی\n/stop - توقف سیگنال\n/open - معاملات باز من\n/stats - آمار من");
                        continue;
                    }

                    if (text.StartsWith("/stop"))
                    {
                        SetUserActive(userChatId, false);
                        await bot.SendMessage(userChatId, "سیگنال‌ها برای شما متوقف شد. برای برگشت /start بزنید.");
                        continue;
                    }

                    var reply = await HandleUserCommand(userChatId, text, restClient);
                    if (!string.IsNullOrEmpty(reply))
                        await bot.SendMessage(userChatId, reply);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Telegram listen error: {ex.Message}");
                await Task.Delay(5000);
            }
        }
    }

    static async Task<string> HandleCallback(long chatId, string data, BinanceRestClient client)
    {
        var parts = data.Split('|');
        if (parts.Length == 0) return "دستور نامعتبر";

        if (parts[0] == "bought" && parts.Length >= 5)
        {
            string symbol = parts[1];
            decimal entry = decimal.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
            decimal sl = decimal.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture);
            decimal tp = decimal.Parse(parts[4], System.Globalization.CultureInfo.InvariantCulture);

            if (HasOpenTrade(chatId, symbol))
                return $"قبلاً معامله باز روی {symbol} داری.";

            OpenTrade(chatId, symbol, entry, sl, tp);
            return $"✅ ثبت شد: خرید {symbol}\nEntry: {entry:N4}\nSL: {sl:N4}\nTP: {tp:N4}";
        }

        if (parts[0] == "sold" && parts.Length >= 2)
        {
            string symbol = parts[1];
            var trades = GetOpenTrades(chatId).Where(t => t.Symbol == symbol).ToList();
            if (trades.Count == 0)
                return $"معامله بازی برای {symbol} نیست.";

            var ticker = await client.SpotApi.ExchangeData.GetTickerAsync(symbol);
            decimal exitPrice = ticker.Success ? ticker.Data.LastPrice : trades[0].EntryPrice;

            foreach (var t in trades)
                CloseTrade(t.Id, exitPrice, "ManualSell");

            decimal pnl = (exitPrice - trades[0].EntryPrice) / trades[0].EntryPrice * 100;
            return $"✅ بسته شد: {symbol}\nExit: {exitPrice:N4}\nPnL: {pnl:N2}%";
        }

        if (parts[0] == "skip")
            return "لغو شد. معامله‌ای ثبت نشد.";

        return "دستور نامعتبر";
    }

    static async Task<string> HandleUserCommand(long chatId, string text, BinanceRestClient client)
    {
        text = text.Trim();
        if (text.StartsWith("/")) text = text[1..];

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "";

        string action = parts[0].ToLower();

        if (action == "open")
        {
            var trades = GetOpenTrades(chatId);
            if (trades.Count == 0) return "معامله بازی نداری.";
            return "معاملات باز شما:\n" + string.Join("\n",
                trades.Select(t => $"{t.Symbol} | Entry {t.EntryPrice:N4} | SL {t.StopLoss:N4} | TP {t.TakeProfit:N4}"));
        }

        if (action == "report")
            return GetSignalReport();

        if (action == "stats")
            return GetPerformanceStats(chatId);

        if (parts.Length < 2)
            return "فرمت:\n/bought BTCUSDT\n/sold BTCUSDT\n/open\n/stats";

        string symbol = parts[1].ToUpper();
        if (!symbol.EndsWith("USDT")) symbol += "USDT";

        if (action == "bought")
        {
            if (HasOpenTrade(chatId, symbol))
                return $"قبلاً معامله باز روی {symbol} داری.";

            decimal entry;
            if (parts.Length >= 3 && decimal.TryParse(parts[2],
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var manualPrice))
                entry = manualPrice;
            else
            {
                var ticker = await client.SpotApi.ExchangeData.GetTickerAsync(symbol);
                if (!ticker.Success) return $"قیمت {symbol} گرفته نشد.";
                entry = ticker.Data.LastPrice;
            }

            decimal sl = entry * 0.985m;
            decimal tp = entry * 1.03m;
            OpenTrade(chatId, symbol, entry, sl, tp);
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
                var ticker = await client.SpotApi.ExchangeData.GetTickerAsync(symbol);
                exitPrice = ticker.Success ? ticker.Data.LastPrice : trades[0].EntryPrice;
            }

            foreach (var t in trades)
                CloseTrade(t.Id, exitPrice, "ManualSell");

            decimal pnl = (exitPrice - trades[0].EntryPrice) / trades[0].EntryPrice * 100;
            return $"✅ بسته شد: {symbol}\nExit: {exitPrice:N4}\nPnL: {pnl:N2}%";
        }

        return "";
    }

    // ==================== تحلیل ====================

    static async Task<(string Text, InlineKeyboardMarkup? Keyboard)?> AnalyzeSymbol(
    BinanceRestClient client, string symbol)
    {
        try
        {
            var data15m = await GetNobitexQuotesAsync(symbol, "15", 100);
            await Task.Delay(200);
            var data1h = await GetNobitexQuotesAsync(symbol, "60", 100);
            await Task.Delay(200);

            if (data15m == null || data1h == null || data15m.Count < 50 || data1h.Count < 50)
                return null;

            // قیمت ورود = آخرین قیمت نوبیتکس
            decimal? live = await GetNobitexLastPriceAsync(symbol);
            decimal price = live ?? data15m.Last().Close;
            if (price <= 0) return null;

            var a15 = AnalyzeTimeframe(data15m);
            var a1h = AnalyzeTimeframe(data1h);

            var (support, resistance) = FindSupportResistance(data1h);
            double distSup = support > 0 ? (double)((price - support) / price * 100) : 100;
            double distRes = resistance > 0 ? (double)((resistance - price) / price * 100) : 100;
            bool nearSupport = distSup <= 1.0;
            bool nearResistance = distRes <= 1.0;

            int buyScore = a15.BuyScore + a1h.BuyScore;
            int sellScore = a15.SellScore + a1h.SellScore;

            bool bothBullish = a15.BuyScore > a15.SellScore && a1h.BuyScore > a1h.SellScore;
            bool bothBearish = a15.SellScore > a15.BuyScore && a1h.SellScore > a1h.BuyScore;

            if (bothBullish) buyScore += 2;
            if (bothBearish) sellScore += 2;
            if (nearSupport && buyScore > sellScore) buyScore += 2;
            if (nearResistance && sellScore > buyScore) sellScore += 2;

            // بعد از محاسبه buyScore / sellScore و bothBullish / bothBearish

            var (aiScore, aiReason, newsList) = await GetNewsAiScoreAsync(
                symbol, buyScore, sellScore);

            if (aiScore > 0) buyScore += aiScore;
            if (aiScore < 0) sellScore += (-aiScore);

            bool newsBlocksBuy = aiScore <= -2;
            bool newsBlocksSell = aiScore >= 2;

            string decision = "WAIT";

            if (!newsBlocksBuy &&
                bothBullish && buyScore >= 7 && buyScore >= sellScore + 3 &&
                a15.Rsi < 60 && a1h.Rsi < 65)
            {
                decision = "BUY";
            }
            else if (!newsBlocksSell &&
                     bothBearish && sellScore >= 7 && sellScore >= buyScore + 3 &&
                     a15.Rsi > 40 && a1h.Rsi > 35)
            {
                decision = "SELL";
            }

            if (decision == "WAIT") return null;

            Console.WriteLine($"{symbol,-12} | {price,12:N4} | Buy:{buyScore} Sell:{sellScore} | {decision}");
            SaveAnalysis(symbol, price, a15.Rsi, buyScore, sellScore, decision);

            if (decision == "BUY")
            {
                decimal stopLoss;
                if (support > 0 && support < price)
                {
                    stopLoss = support * 0.997m;
                    decimal maxRiskSl = price * 0.98m;
                    if (stopLoss < maxRiskSl) stopLoss = maxRiskSl;
                }
                else stopLoss = price * 0.985m;

                decimal takeProfit;
                if (resistance > 0 && resistance > price)
                {
                    takeProfit = resistance * 0.997m;
                    decimal maxTp = price * 1.04m;
                    if (takeProfit > maxTp) takeProfit = maxTp;
                }
                else takeProfit = price * 1.03m;

                decimal risk = price - stopLoss;
                decimal reward = takeProfit - price;
                if (risk <= 0 || reward / risk < 1.2m)
                {
                    Console.WriteLine($"  Skip BUY {symbol}: bad R/R");
                    return null;
                }

                decimal slPercent = (price - stopLoss) / price * 100;
                decimal tpPercent = (takeProfit - price) / price * 100;

                if (IsStablePair(symbol, price)) return null;

                // جلوگیری از تکرار
                if (HasPendingSignal(symbol)) return null;

                TrackSignal(symbol, price, stopLoss, takeProfit);

                var nobitexPrice = await GetNobitexLastPriceAsync(symbol);
                decimal entryForUser = nobitexPrice ?? price; // اگر نوبیتکس نگرفت، بایننس

                string text =
                    $"🟢 BUY پیشنهاد {symbol}\n" +
                    $"Entry (Nobitex): {entryForUser:N4}\n" +
                    $"Ref Binance: {price:N4}\n" +
                    $"Stop Loss: {stopLoss:N4} (-{slPercent:N2}%)\n" +
                    $"Take Profit: {takeProfit:N4} (+{tpPercent:N2}%)\n" +
                    $"R/R: 1:{(reward / risk):N2}\n" +
                    $"Score B/S: {buyScore}/{sellScore}\n" +
                    $"RSI 15m/1h: {a15.Rsi:N1}/{a1h.Rsi:N1}\n\n" +
                    $"AI/News Score: {aiScore} ({aiReason})\n" +
                    $"اگر خریدی دکمه زیر را بزن:";

                var keyboard = new InlineKeyboardMarkup(new[]
                {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("✅ خریدم",
                        $"bought|{symbol}|{price}|{stopLoss}|{takeProfit}"),
                    InlineKeyboardButton.WithCallbackData("❌ بی‌خیال",
                        $"skip|{symbol}")
                }
            });

                return (text, keyboard);
            }
        }
        catch (Exception ex)
        {
            if (ex.Message.Contains("rate") || ex.Message.Contains("429") || ex.Message.Contains("Too many"))
            {
                Console.WriteLine($"Rate limit on {symbol} → 60s wait");
                await Task.Delay(60000);
            }
        }

        return null;
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

    static async Task<string?> AnalyzeSellForOpenTrade(BinanceRestClient client, string symbol)
    {
        var data15m = await GetQuotes(client, symbol, KlineInterval.FifteenMinutes, 100);
        await Task.Delay(150);
        var data1h = await GetQuotes(client, symbol, KlineInterval.OneHour, 100);
        if (data15m == null || data1h == null || data15m.Count < 50) return null;

        var ticker = await client.SpotApi.ExchangeData.GetTickerAsync(symbol);
        if (!ticker.Success) return null;
        decimal price = ticker.Data.LastPrice;

        var a15 = AnalyzeTimeframe(data15m);
        var a1h = AnalyzeTimeframe(data1h);

        int buyScore = a15.BuyScore + a1h.BuyScore;
        int sellScore = a15.SellScore + a1h.SellScore;

        bool bothBearish = a15.SellScore > a15.BuyScore && a1h.SellScore > a1h.BuyScore;
        if (bothBearish) sellScore += 2;

        // خبر/AI اختیاری (اگر اضافه کرده‌ای)
        // var (aiScore, aiReason, _) = await GetNewsAiScoreAsync(symbol, buyScore, sellScore);
        // if (aiScore < 0) sellScore += (-aiScore);

        if (!(bothBearish && sellScore >= 7 && sellScore >= buyScore + 3 &&
              a15.Rsi > 40 && a1h.Rsi > 35))
            return null;

        return $"🔴 SELL (معامله باز شما)\n" +
               $"{symbol}\n" +
               $"Price: {price:N4}\n" +
               $"Score B/S: {buyScore}/{sellScore}\n" +
               $"RSI 15m/1h: {a15.Rsi:N1}/{a1h.Rsi:N1}\n\n" +
               $"اگر فروختی دکمه زیر را بزن:";
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
        using var conn = new SqliteConnection("Data Source=crypto_analyzer.db");
        conn.Open();

        var cmd = conn.CreateCommand();
        cmd.CommandText = @"

        CREATE TABLE IF NOT EXISTS SignalTracks (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Symbol TEXT NOT NULL,
            EntryPrice REAL NOT NULL,
            StopLoss REAL NOT NULL,
            TakeProfit REAL NOT NULL,
            Status TEXT DEFAULT 'Pending',
            SignalAt TEXT DEFAULT CURRENT_TIMESTAMP,
            ResolvedAt TEXT,
            ResolvePrice REAL
        );

        CREATE TABLE IF NOT EXISTS Users (
            ChatId INTEGER PRIMARY KEY,
            Username TEXT,
            FirstName TEXT,
            IsActive INTEGER DEFAULT 1,
            CreatedAt TEXT DEFAULT CURRENT_TIMESTAMP
        );

        CREATE TABLE IF NOT EXISTS AnalysisResults (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Symbol TEXT,
            Timeframe TEXT DEFAULT '15m+1h',
            Price REAL,
            RSI REAL,
            BuyScore INTEGER,
            SellScore INTEGER,
            Decision TEXT,
            CreatedAt TEXT DEFAULT CURRENT_TIMESTAMP
        );

        CREATE TABLE IF NOT EXISTS OpenTrades (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            ChatId INTEGER NOT NULL,
            Symbol TEXT NOT NULL,
            EntryPrice REAL NOT NULL,
            StopLoss REAL NOT NULL,
            TakeProfit REAL NOT NULL,
            Status TEXT DEFAULT 'Open',
            ExitPrice REAL,
            ExitReason TEXT,
            OpenedAt TEXT DEFAULT CURRENT_TIMESTAMP,
            ClosedAt TEXT
        );";
        cmd.ExecuteNonQuery();

        Console.WriteLine("Multi-user database ready.");
    }

    static void RegisterUser(long chatId, string? username, string? firstName)
    {
        using var conn = new SqliteConnection("Data Source=crypto_analyzer.db");
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

    static bool HasPendingSignal(string symbol)
    {
        using var conn = new SqliteConnection("Data Source=crypto_analyzer.db");
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM SignalTracks WHERE Symbol=$s AND Status='Pending'";
        cmd.Parameters.AddWithValue("$s", symbol);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    static void SetUserActive(long chatId, bool active)
    {
        using var conn = new SqliteConnection("Data Source=crypto_analyzer.db");
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
        using var conn = new SqliteConnection("Data Source=crypto_analyzer.db");
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ChatId FROM Users WHERE IsActive=1";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(reader.GetInt64(0));
        return list;
    }

    static void SaveAnalysis(string symbol, decimal price, double rsi, int buy, int sell, string decision)
    {
        using var conn = new SqliteConnection("Data Source=crypto_analyzer.db");
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO AnalysisResults (Symbol, Timeframe, Price, RSI, BuyScore, SellScore, Decision)
            VALUES ($s, '15m+1h', $p, $r, $b, $sell, $d)";
        cmd.Parameters.AddWithValue("$s", symbol);
        cmd.Parameters.AddWithValue("$p", price);
        cmd.Parameters.AddWithValue("$r", rsi);
        cmd.Parameters.AddWithValue("$b", buy);
        cmd.Parameters.AddWithValue("$sell", sell);
        cmd.Parameters.AddWithValue("$d", decision);
        cmd.ExecuteNonQuery();
    }

    static bool HasOpenTrade(long chatId, string symbol)
    {
        using var conn = new SqliteConnection("Data Source=crypto_analyzer.db");
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM OpenTrades WHERE ChatId=$c AND Symbol=$s AND Status='Open'";
        cmd.Parameters.AddWithValue("$c", chatId);
        cmd.Parameters.AddWithValue("$s", symbol);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    static void OpenTrade(long chatId, string symbol, decimal entry, decimal sl, decimal tp)
    {
        using var conn = new SqliteConnection("Data Source=crypto_analyzer.db");
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
        INSERT INTO OpenTrades (ChatId, Symbol, EntryPrice, StopLoss, TakeProfit, Status)
        VALUES ($c, $s, $e, $sl, $tp, 'Open')";
        cmd.Parameters.AddWithValue("$c", chatId);
        cmd.Parameters.AddWithValue("$s", symbol);
        cmd.Parameters.AddWithValue("$e", entry);
        cmd.Parameters.AddWithValue("$sl", sl);
        cmd.Parameters.AddWithValue("$tp", tp);
        cmd.ExecuteNonQuery();
    }

    static List<OpenTradeRow> GetOpenTrades(long? chatId = null)
    {
        var list = new List<OpenTradeRow>();
        using var conn = new SqliteConnection("Data Source=crypto_analyzer.db");
        conn.Open();
        var cmd = conn.CreateCommand();

        if (chatId.HasValue)
        {
            cmd.CommandText = @"SELECT Id, ChatId, Symbol, EntryPrice, StopLoss, TakeProfit
                            FROM OpenTrades WHERE Status='Open' AND ChatId=$c";
            cmd.Parameters.AddWithValue("$c", chatId.Value);
        }
        else
        {
            cmd.CommandText = @"SELECT Id, ChatId, Symbol, EntryPrice, StopLoss, TakeProfit
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
                TakeProfit = reader.GetDecimal(5)
            });
        }
        return list;
    }

    static void CloseTrade(int id, decimal exitPrice, string reason)
    {
        using var conn = new SqliteConnection("Data Source=crypto_analyzer.db");
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

    static string GetPerformanceStats(long chatId)
    {
        using var conn = new SqliteConnection("Data Source=crypto_analyzer.db");
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
        SELECT ExitReason, COUNT(1),
               AVG((ExitPrice - EntryPrice) / EntryPrice * 100)
        FROM OpenTrades
        WHERE Status='Closed' AND ChatId=$c
        GROUP BY ExitReason";
        cmd.Parameters.AddWithValue("$c", chatId);

        var lines = new List<string> { "📊 آمار شما:" };
        int total = 0, wins = 0;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string reason = reader.GetString(0);
            int cnt = reader.GetInt32(1);
            double avg = reader.IsDBNull(2) ? 0 : reader.GetDouble(2);
            total += cnt;
            if (reason == "TakeProfit") wins += cnt;
            lines.Add($"  {reason}: {cnt} | میانگین: {avg:N2}%");
        }

        if (total == 0) return "📊 هنوز معامله بسته‌شده‌ای ندارید.";
        lines.Add($"  Win Rate: {(double)wins / total * 100:N1}% از {total} معامله");
        return string.Join("\n", lines);
    }

    static async Task CheckOpenTrades(BinanceRestClient client, TelegramBotClient bot)
    {
        var trades = GetOpenTrades(); // همه کاربران

        foreach (var t in trades)
        {
            try
            {
                var ticker = await client.SpotApi.ExchangeData.GetTickerAsync(t.Symbol);
                await Task.Delay(150);
                if (!ticker.Success) continue;

                decimal price = ticker.Data.LastPrice;
                decimal pnlPercent = (price - t.EntryPrice) / t.EntryPrice * 100;

                if (price <= t.StopLoss)
                {
                    CloseTrade(t.Id, price, "StopLoss");
                    await bot.SendMessage(t.ChatId,
                        $"🛑 STOP LOSS {t.Symbol}\nEntry: {t.EntryPrice:N4} → {price:N4}\nPnL: {pnlPercent:N2}%");
                }
                else if (price >= t.TakeProfit)
                {
                    CloseTrade(t.Id, price, "TakeProfit");
                    await bot.SendMessage(t.ChatId,
                        $"🎯 TAKE PROFIT {t.Symbol}\nEntry: {t.EntryPrice:N4} → {price:N4}\nPnL: {pnlPercent:N2}%");
                }
                else if (pnlPercent <= -1.0m)
                {
                    await bot.SendMessage(t.ChatId,
                        $"⚠️ نزدیک حد ضرر {t.Symbol}\nNow: {price:N4} | PnL: {pnlPercent:N2}%");
                }
                else if (pnlPercent >= 2.0m)
                {
                    var keyboard = new InlineKeyboardMarkup(new[]
                    {
                    new[]
                    {
                        InlineKeyboardButton.WithCallbackData("✅ فروختم", $"sold|{t.Symbol}")
                    }
                });
                    await bot.SendMessage(t.ChatId,
                        $"📈 در سود {t.Symbol}\nNow: {price:N4} | PnL: {pnlPercent:N2}%",
                        replyMarkup: keyboard);
                }
            }
            catch { }
        }
    }

    // ==================== داده و اندیکاتور ====================

    static async Task<List<string>> GetTopVolumeSymbols(BinanceRestClient client)
    {
        var nobitex = await GetNobitexUsdtSymbolsAsync();

        var tickersResult = await client.SpotApi.ExchangeData.GetTickersAsync();
        if (!tickersResult.Success)
            return [.. nobitex.Take(40)];

        var stablecoins = new HashSet<string>
    {
        "USDCUSDT", "TUSDUSDT", "BUSDUSDT", "DAIUSDT", "FDUSDUSDT",
        "USDPUSDT", "USDDUSDT", "USD1USDT", "EURUSDT", "AEURUSDT", "RLUSDUSDT"
    };

        var list = tickersResult.Data
            .Where(t => t.Symbol.EndsWith("USDT"))
            .Where(t => nobitex.Contains(t.Symbol))                 // فقط نوبیتکس
            .Where(t => !stablecoins.Contains(t.Symbol))
            .Where(t => !t.Symbol.Contains("UP") && !t.Symbol.Contains("DOWN")
                     && !t.Symbol.Contains("BULL") && !t.Symbol.Contains("BEAR"))
            .OrderByDescending(t => t.QuoteVolume)
            .Take(40)
            .Select(t => t.Symbol)
            .ToList();

        Console.WriteLine("Symbols (Binance ∩ Nobitex):");
        foreach (var s in list)
            Console.WriteLine("  " + s);

        return list;
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

            return quotes.OrderBy(q => q.Date).ToList();
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

    static (int BuyScore, int SellScore, double Rsi) AnalyzeTimeframe(List<Quote> quotes)
    {
        int buy = 0, sell = 0;
        var rsi = quotes.GetRsi(14).Last();
        var macd = quotes.GetMacd(12, 26, 9).Last();
        var sma20 = quotes.GetSma(20).Last();
        var sma50 = quotes.GetSma(50).Last();
        var last = quotes.Last();

        if (rsi.Rsi <= 30) buy += 2;
        else if (rsi.Rsi <= 40) buy += 1;
        else if (rsi.Rsi >= 70) sell += 2;
        else if (rsi.Rsi >= 60) sell += 1;

        if (macd.Macd > macd.Signal && macd.Histogram > 0) buy += 1;
        else if (macd.Macd < macd.Signal && macd.Histogram < 0) sell += 1;

        if (Convert.ToDecimal(last.Close) > Convert.ToDecimal(sma20.Sma) && Convert.ToDecimal(sma20.Sma) > Convert.ToDecimal(sma50.Sma)) buy += 1;
        else if (Convert.ToDecimal(last.Close) < Convert.ToDecimal(sma20.Sma) && Convert.ToDecimal(sma20.Sma) < Convert.ToDecimal(sma50.Sma)) sell += 1;

        var avgVol = quotes.TakeLast(20).Average(q => q.Volume);
        if (last.Volume > avgVol * 1.3m)
        {
            if (buy > sell) buy += 1;
            if (sell > buy) sell += 1;
        }

        var rsiList = quotes.GetRsi(14).ToList();
        if (quotes.Count >= 6)
        {
            if (quotes[^1].Close < quotes[^6].Close && rsiList[^1].Rsi > rsiList[^6].Rsi) buy += 2;
            if (quotes[^1].Close > quotes[^6].Close && rsiList[^1].Rsi < rsiList[^6].Rsi) sell += 2;
        }

        return (buy, sell, rsi.Rsi ?? 0);
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

    class OpenTradeRow
    {
        public int Id { get; set; }
        public long ChatId { get; set; }
        public string Symbol { get; set; } = "";
        public decimal EntryPrice { get; set; }
        public decimal StopLoss { get; set; }
        public decimal TakeProfit { get; set; }
    }

    class SignalTrack
    {
        public int Id { get; set; }
        public string Symbol { get; set; } = "";
        public decimal EntryPrice { get; set; }
        public decimal StopLoss { get; set; }
        public decimal TakeProfit { get; set; }
        public DateTime SignalAt { get; set; }
    }
}