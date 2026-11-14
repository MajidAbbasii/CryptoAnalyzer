using CryptoAnalyzer.Analysis;
using CryptoAnalyzer.Persistence;
using CryptoAnalyzer.Exchanges;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using NSec.Cryptography;

var passed = 0;
Run("support proximity rejects a crossed level", () =>
{
    Assert(StrategyRules.IsNearSupport(101m, 100m), "valid support distance should pass");
    Assert(!StrategyRules.IsNearSupport(99m, 100m), "support above price must not pass");
    Assert(!StrategyRules.IsNearSupport(0m, 1m), "zero price must not pass");
});

Run("resistance proximity rejects a crossed level", () =>
{
    Assert(StrategyRules.IsNearResistance(99m, 100m), "valid resistance distance should pass");
    Assert(!StrategyRules.IsNearResistance(101m, 100m), "resistance below price must not pass");
});

Run("only fully closed candles are eligible for indicators", () =>
{
    var now = new DateTime(2026, 11, 12, 12, 30, 2, DateTimeKind.Utc);
    Assert(StrategyRules.IsCandleClosed(new DateTime(2026, 11, 12, 12, 15, 0, DateTimeKind.Utc), TimeSpan.FromMinutes(15), now), "previous 15m candle should be closed");
    Assert(!StrategyRules.IsCandleClosed(now, TimeSpan.FromMinutes(15), now), "current 15m candle must remain open");
    Assert(!StrategyRules.IsCandleClosed(now.AddMinutes(-30), TimeSpan.FromHours(1), now), "current 1h candle must remain open");
});

Run("long and short stop/target triggers are directional", () =>
{
    Assert(StrategyRules.IsLongStopLossHit(94m, 95m), "long SL trigger incorrect");
    Assert(StrategyRules.IsLongTakeProfitHit(111m, 110m), "long TP trigger incorrect");
    Assert(StrategyRules.IsShortStopLossHit(106m, 105m), "short SL trigger incorrect");
    Assert(StrategyRules.IsShortTakeProfitHit(89m, 90m), "short TP trigger incorrect");
});

Run("long and short plans have correct threshold direction", () =>
{
    Assert(StrategyRules.TryCreatePlan(TradeSide.Long, 100m, 98m, 105m, out var longPlan), "long plan expected");
    Assert(longPlan.StopLoss < longPlan.Entry && longPlan.TakeProfit > longPlan.Entry, "long SL/TP direction invalid");
    Assert(StrategyRules.TryCreatePlan(TradeSide.Short, 100m, 95m, 101m, out var shortPlan), "short plan expected");
    Assert(shortPlan.TakeProfit < shortPlan.Entry && shortPlan.StopLoss > shortPlan.Entry, "short SL/TP direction invalid");
});

Run("risk and reward constraints reject invalid plans", () =>
{
    Assert(!StrategyRules.TryCreatePlan(TradeSide.Long, 100m, 90m, 101m, out _), "bad reward/risk should be rejected");
    Assert(!StrategyRules.TryCreatePlan(TradeSide.Long, 100m, 90m, 130m, out _, maxRiskPercent: 4m), "excessive risk should be rejected");
});

Run("PnL signs are correct for long, short and spot buy", () =>
{
    Assert(StrategyRules.CalculatePnlPercent(TradeSide.Long, 100m, 110m) == 10m, "long profit incorrect");
    Assert(StrategyRules.CalculatePnlPercent(TradeSide.Short, 100m, 90m) == 10m, "short profit incorrect");
    Assert(StrategyRules.CalculatePnlPercent(TradeSide.Buy, 100m, 90m) == -10m, "spot loss incorrect");
});

Run("AES-GCM credential encryption authenticates ciphertext", () =>
{
    Environment.SetEnvironmentVariable("DATA_PROTECTION_KEY", Convert.ToBase64String(Enumerable.Range(0, 32).Select(x => (byte)x).ToArray()));
    string encrypted = TokenEncryption.Encrypt("api-secret-value");
    Assert(encrypted.StartsWith("v2:", StringComparison.Ordinal), "new ciphertext must be versioned AES-GCM");
    Assert(TokenEncryption.Decrypt(encrypted) == "api-secret-value", "credential round-trip failed");
    string[] parts = encrypted.Split(':');
    byte[] tag = Convert.FromBase64String(parts[2]);
    tag[0] ^= 0x01;
    parts[2] = Convert.ToBase64String(tag);
    bool rejected = false;
    try { TokenEncryption.Decrypt(string.Join(':', parts)); }
    catch (CryptographicException) { rejected = true; }
    Assert(rejected, "tampered ciphertext must fail authentication");

    byte[] legacyIv = RandomNumberGenerator.GetBytes(16);
    byte[] legacyPlain = Encoding.UTF8.GetBytes("legacy-secret");
    byte[] legacyCipher;
    using (var aes = Aes.Create())
    {
        aes.Key = Enumerable.Range(0, 32).Select(x => (byte)x).ToArray();
        aes.IV = legacyIv;
        using var encryptor = aes.CreateEncryptor();
        legacyCipher = encryptor.TransformFinalBlock(legacyPlain, 0, legacyPlain.Length);
    }
    string legacyPayload = Convert.ToBase64String(legacyIv) + ":" + Convert.ToBase64String(legacyCipher);
    Assert(TokenEncryption.Decrypt(legacyPayload) == "legacy-secret", "legacy AES-CBC data must remain readable for migration");
});

Run("exchange accounts and approvals are isolated per user", () =>
{
    string path = Path.Combine(Path.GetTempPath(), $"crypto-analyzer-accounts-{Guid.NewGuid():N}.db");
    string cs = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, ForeignKeys = true }.ToString();
    try
    {
        DatabaseInitializer.Initialize(cs);
        using (var connection = new SqliteConnection(cs))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO Users(ChatId) VALUES (101),(202);";
            command.ExecuteNonQuery();
        }

        var accounts = new UserExchangeAccountRepository(cs);
        long accountId = accounts.Add(101, "NOBITEX", "primary", "key-101", "secret-101", "pass-101");
        Assert(accounts.List(101).Count == 1, "owner should see own account");
        Assert(accounts.List(202).Count == 0, "other user must not see account");
        using (var stored = new SqliteConnection(cs))
        {
            stored.Open();
            using var query = stored.CreateCommand();
            query.CommandText = "SELECT ApiKeyEncrypted, ApiSecretEncrypted FROM UserExchangeAccounts WHERE Id=$id";
            query.Parameters.AddWithValue("$id", accountId);
            using var reader = query.ExecuteReader();
            Assert(reader.Read() && !reader.GetString(0).Contains("key-101") && !reader.GetString(1).Contains("secret-101"),
                "exchange credentials must not be stored as plaintext");
        }
        var credentials = accounts.GetCredentials(101, accountId);
        Assert(credentials.ApiKey == "key-101" && credentials.ApiSecret == "secret-101" && credentials.Passphrase == "pass-101", "credentials decrypt incorrectly");
        Assert(accounts.UpdateCredentials(101, accountId, "nobitex-public-v2", "nobitex-private-v2"),
            "owner should be able to replace legacy credentials with a signed API-key pair");
        var updatedCredentials = accounts.GetCredentials(101, accountId);
        Assert(updatedCredentials.ApiKey == "nobitex-public-v2" && updatedCredentials.ApiSecret == "nobitex-private-v2",
            "updated public/private credentials did not round-trip");
        Assert(!accounts.UpdateCredentials(202, accountId, "other-public", "other-private"),
            "another user must not replace this account's credentials");
        long tokenOnlyAccountId = accounts.Add(202, "NOBITEX", "token-only", "nobitex-token");
        var tokenOnlyCredentials = accounts.GetCredentials(202, tokenOnlyAccountId);
        Assert(tokenOnlyCredentials.ApiKey == "nobitex-token" && tokenOnlyCredentials.ApiSecret == "",
            "single-token exchange credentials should not require an API secret");

        bool crossUserReadRejected = false;
        try { accounts.GetCredentials(202, accountId); }
        catch (KeyNotFoundException) { crossUserReadRejected = true; }
        Assert(crossUserReadRejected, "cross-user credential access must be rejected");
        Assert(!accounts.Deactivate(202, accountId), "another user must not deactivate this account");

        long binanceAccountId = accounts.Add(101, "BINANCE_TESTNET", "binance-test", "binance-key", "binance-secret");

        var approvals = new ManualOrderApprovalRepository(cs);
        var draft = new ManualOrderDraft(101, accountId, "NOBITEX", "BTCUSDT", ExchangeTradeMode.Spot,
            ExchangeOrderSide.Buy, ExchangeOrderType.Limit, 0.001m, 65000m, 64000m, 68000m);
        string approvalId = approvals.Create(draft);
        Assert(approvals.GetStatus(101, approvalId) == "AwaitingApproval", "approval must start pending");
        Assert(approvals.ApproveForManualExecution(202, approvalId) == ApprovalTransitionResult.NotFound, "other user must not approve");
        Assert(approvals.ApproveForManualExecution(101, approvalId) == ApprovalTransitionResult.Updated, "owner approval should transition");
        Assert(approvals.GetStatus(101, approvalId) == "ApprovedForManualExecution", "approval state not saved");
        Assert(approvals.ApproveForManualExecution(101, approvalId) == ApprovalTransitionResult.InvalidState, "approval must not be replayed");
        Assert(approvals.CanRegisterAsOpenPosition(101, approvalId), "approved protected Spot buy should be eligible for position tracking");
        Assert(!approvals.CanRegisterAsOpenPosition(202, approvalId), "another user must not register this approval as a position");
        Assert(approvals.RegisterAsOpenPosition(101, approvalId, 65125m), "owner should register manually executed protected order");
        Assert(!approvals.RegisterAsOpenPosition(101, approvalId, 65125m), "same approval must not create duplicate open positions");
        using (var positionConnection = new SqliteConnection(cs))
        {
            positionConnection.Open();
            using var positionQuery = positionConnection.CreateCommand();
            positionQuery.CommandText = "SELECT EntryPrice, StopLoss, TakeProfit, Side, ExchangeAccountId, Quantity, OcoStatus FROM OpenTrades WHERE ChatId=101 AND Symbol='BTCUSDT'";
            using var positionReader = positionQuery.ExecuteReader();
            Assert(positionReader.Read() && positionReader.GetDecimal(0) == 65125m &&
                positionReader.GetDecimal(1) == 64000m && positionReader.GetDecimal(2) == 68000m &&
                positionReader.GetString(3) == "BUY" && positionReader.GetInt64(4) == accountId &&
                positionReader.GetString(5) == "0.001" && positionReader.GetString(6) == "NotSubmitted",
                "registered position must preserve actual fill, quantity, SL/TP and user-owned exchange account");
        }
        Assert(approvals.TryBeginSpotOcoSubmission(101, 1), "owner should be able to claim Spot OCO submission once");
        Assert(!approvals.TryBeginSpotOcoSubmission(101, 1), "duplicate OCO submission must be blocked while in progress");
        Assert(approvals.CompleteSpotOcoSubmission(101, 1, "Accepted", new[] { "oco-tp-1", "oco-sl-1" }, "accepted"),
            "OCO result should be persisted");
        Assert(!approvals.TryBeginSpotOcoSubmission(101, 1), "accepted OCO must not be submitted twice");

        string rejectedId = approvals.Create(draft);
        Assert(approvals.Reject(101, rejectedId) == ApprovalTransitionResult.Updated, "owner should reject pending request");
        Assert(approvals.GetStatus(101, rejectedId) == "Rejected", "rejection state not saved");

        string expiredId = approvals.Create(draft);
        using (var expire = new SqliteConnection(cs))
        {
            expire.Open();
            using var command = expire.CreateCommand();
            command.CommandText = "UPDATE ManualOrderApprovals SET ExpiresAt=$past WHERE Id=$id";
            command.Parameters.AddWithValue("$past", DateTime.UtcNow.AddMinutes(-1).ToString("O"));
            command.Parameters.AddWithValue("$id", expiredId);
            command.ExecuteNonQuery();
        }
        Assert(approvals.ApproveForManualExecution(101, expiredId) == ApprovalTransitionResult.Expired,
            "expired approval must not transition");

        string marginId = approvals.Create(new ManualOrderDraft(101, accountId, "NOBITEX", "BTCUSDT",
            ExchangeTradeMode.Margin, ExchangeOrderSide.Buy, ExchangeOrderType.Limit,
            0.001m, 65000m, 64000m, 67000m, 1));
        Assert(approvals.GetStatus(101, marginId) == "AwaitingApproval", "valid protected margin preview should be saved");
        string fractionalLeverageId = approvals.Create(new ManualOrderDraft(101, accountId, "NOBITEX", "BTCUSDT",
            ExchangeTradeMode.Margin, ExchangeOrderSide.Buy, ExchangeOrderType.Limit,
            0.001m, 65000m, 64000m, 67000m, 1.5m));
        Assert(approvals.GetStatus(101, fractionalLeverageId) == "AwaitingApproval", "0.5-step leverage should be accepted for market-level validation");
        string guardedMarketId = approvals.Create(new ManualOrderDraft(101, accountId, "NOBITEX", "BTCUSDT",
            ExchangeTradeMode.Spot, ExchangeOrderSide.Buy, ExchangeOrderType.Market, 0.001m,
            StopLoss: 64000m, TakeProfit: 68000m, PriceGuard: 65100m));
        Assert(approvals.GetStatus(101, guardedMarketId) == "AwaitingApproval", "protected Spot market buy with price guard should be accepted");
        bool unprotectedSpotBuyRejected = false;
        try
        {
            approvals.Create(new ManualOrderDraft(101, accountId, "NOBITEX", "BTCUSDT",
                ExchangeTradeMode.Spot, ExchangeOrderSide.Buy, ExchangeOrderType.Limit, 0.001m, 65000m));
        }
        catch (ArgumentException) { unprotectedSpotBuyRejected = true; }
        Assert(unprotectedSpotBuyRejected, "Spot buy without stop-loss/take-profit must be rejected");

        bool spotSellWithProtectionRejected = false;
        try
        {
            approvals.Create(new ManualOrderDraft(101, accountId, "NOBITEX", "BTCUSDT",
                ExchangeTradeMode.Spot, ExchangeOrderSide.Sell, ExchangeOrderType.Limit, 0.001m,
                65000m, 64000m, 68000m));
        }
        catch (ArgumentException) { spotSellWithProtectionRejected = true; }
        Assert(spotSellWithProtectionRejected, "Spot sell must not be mistaken for a short position with SL/TP");
        bool unguardedSpotMarketRejected = false;
        try
        {
            approvals.Create(new ManualOrderDraft(101, accountId, "NOBITEX", "BTCUSDT",
                ExchangeTradeMode.Spot, ExchangeOrderSide.Buy, ExchangeOrderType.Market, 0.001m));
        }
        catch (ArgumentException) { unguardedSpotMarketRejected = true; }
        Assert(unguardedSpotMarketRejected, "Spot market order without price guard must be rejected");

        bool binanceOrderRejected = false;
        try
        {
            approvals.Create(new ManualOrderDraft(101, binanceAccountId, "BINANCE_TESTNET", "BTCUSDT",
                ExchangeTradeMode.Spot, ExchangeOrderSide.Buy, ExchangeOrderType.Limit, 0.001m, 65000m));
        }
        catch (ArgumentException) { binanceOrderRejected = true; }
        Assert(binanceOrderRejected, "order approvals must be restricted to Nobitex");
        bool futuresRejected = false;
        try
        {
            approvals.Create(new ManualOrderDraft(101, accountId, "NOBITEX", "BTCUSDT",
                ExchangeTradeMode.Futures, ExchangeOrderSide.Buy, ExchangeOrderType.Limit, 0.001m, 65000m));
        }
        catch (ArgumentException) { futuresRejected = true; }
        Assert(futuresRejected, "Futures must not be exposed as a Nobitex order mode");
        bool unprotectedMarginRejected = false;
        try
        {
            approvals.Create(new ManualOrderDraft(101, accountId, "NOBITEX", "BTCUSDT",
                ExchangeTradeMode.Margin, ExchangeOrderSide.Buy, ExchangeOrderType.Market, 0.001m));
        }
        catch (ArgumentException) { unprotectedMarginRejected = true; }
        Assert(unprotectedMarginRejected, "unprotected market margin order must be rejected");

        Assert(OrderExecutionPolicy.IsLiveExecutionEnabled == false, "live execution must remain disabled");

        var adapter = new DisabledExchangeAdapter(new ExchangeCapabilities("NOBITEX", true, true, false, true, true));
        var result = adapter.PlaceOrderAsync(credentials, new ExchangeOrderRequest("BTCUSDT", ExchangeTradeMode.Spot,
            ExchangeOrderSide.Buy, ExchangeOrderType.Market, 0.001m)).GetAwaiter().GetResult();
        Assert(!result.Accepted, "placeholder adapter must reject all orders");

        Assert(accounts.Deactivate(101, accountId), "owner should deactivate account");
        bool inactiveRejected = false;
        try { accounts.GetCredentials(101, accountId); }
        catch (KeyNotFoundException) { inactiveRejected = true; }
        Assert(inactiveRejected, "inactive account credentials must not be retrievable");
    }
    finally
    {
        if (File.Exists(path)) File.Delete(path);
    }
});

Run("exchange registry rejects duplicates and unknown exchanges", () =>
{
    var capabilities = new ExchangeCapabilities("TEST", true, false, false, true, true);
    var registry = new ExchangeAdapterRegistry(new[] { new DisabledExchangeAdapter(capabilities) });
    Assert(registry.GetSupportedExchanges().Single().ExchangeCode == "TEST", "registered adapter missing");
    bool unknownRejected = false;
    try { registry.GetRequired("UNKNOWN"); }
    catch (KeyNotFoundException) { unknownRejected = true; }
    Assert(unknownRejected, "unknown exchange must not be guessed");
    bool duplicateRejected = false;
    try { _ = new ExchangeAdapterRegistry(new[] { new DisabledExchangeAdapter(capabilities), new DisabledExchangeAdapter(capabilities) }); }
    catch (ArgumentException) { duplicateRejected = true; }
    Assert(duplicateRejected, "duplicate exchange adapters must be rejected");
});

Run("Nobitex read-only adapter signs every GET with Ed25519 and reads balances", () =>
{
    var handler = new FakeHttpMessageHandler(request =>
    {
        if (request.RequestUri!.AbsolutePath.EndsWith("/users/profile", StringComparison.Ordinal))
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new StringContent("{\"status\":\"ok\",\"profile\":{}}") };
        if (request.RequestUri.AbsolutePath.EndsWith("/users/wallets/list", StringComparison.Ordinal))
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"status":"ok","wallets":[{"currency":"btc","balance":"1.250","blockedBalance":"0.2","activeBalance":"1.05"},{"currency":"usdt","balance":"50","blockedBalance":"0","activeBalance":"50"}]}""")
            };
        return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
    });
    using var http = new HttpClient(handler);
    var adapter = new NobitexReadOnlyAdapter(http);
    var credentials = CreateNobitexCredentials();

    Assert(adapter.ValidateCredentialsAsync(credentials).GetAwaiter().GetResult(), "valid API key pair should pass profile check");
    var snapshot = adapter.GetAccountSnapshotAsync(credentials).GetAwaiter().GetResult();
    Assert(snapshot.Authenticated && snapshot.Balances.Count == 2, "wallet snapshot was not parsed");
    var btc = snapshot.Balances.Single(x => x.Asset == "BTC");
    Assert(btc.Total == 1.250m && btc.Available == 1.05m && btc.Locked == 0.2m, "BTC balance fields incorrect");
    Assert(handler.Requests.Count == 3, "unexpected number of HTTP requests");
    Assert(handler.Requests.All(x => x.Method == HttpMethod.Get), "read-only adapter must only use GET");
    Assert(handler.Requests.All(x => x.Uri!.Host == "apiv2.nobitex.ir"), "Nobitex adapter must use official API host only");
    Assert(handler.Requests.All(x => x.Authorization is null && x.NobitexKey == credentials.ApiKey &&
        VerifyNobitexSignature(x)), "Nobitex GET requests must carry valid Ed25519 API-key headers and no Token header");

    var order = adapter.PlaceOrderAsync(credentials, new ExchangeOrderRequest("BTCUSDT", ExchangeTradeMode.Spot,
        ExchangeOrderSide.Buy, ExchangeOrderType.Market, 0.01m)).GetAwaiter().GetResult();
    Assert(!order.Accepted && handler.Requests.Count == 3, "PlaceOrder must reject without network access");
    Assert(!adapter.CanPlaceOrders, "read-only adapter must advertise disabled order placement");
});

Run("Nobitex adapter rejects invalid key material and malformed responses", () =>
{
    bool untrustedHostRejected = false;
    try
    {
        using var untrustedHttp = new HttpClient { BaseAddress = new Uri("https://attacker.example/") };
        _ = new NobitexReadOnlyAdapter(untrustedHttp);
    }
    catch (ArgumentException) { untrustedHostRejected = true; }
    Assert(untrustedHostRejected, "Nobitex adapter must not send credentials to an arbitrary host");

    var unauthorizedHandler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized));
    using var unauthorizedHttp = new HttpClient(unauthorizedHandler);
    var adapter = new NobitexReadOnlyAdapter(unauthorizedHttp);
    Assert(!adapter.ValidateCredentialsAsync(CreateNobitexCredentials("bad-key", "bad-private-key")).GetAwaiter().GetResult(),
        "invalid key encoding must fail credential validation");
    Assert(!adapter.ValidateCredentialsAsync(new ExchangeCredentials("legacy-token", "")).GetAwaiter().GetResult(),
        "legacy token-only credentials must not be sent or silently accepted by the API-key adapter");
    Assert(unauthorizedHandler.Requests.Count == 0, "invalid key must fail closed before HTTP");
    var mismatchedCredentials = CreateNobitexCredentials(publicKeyOverride: "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
    Assert(!adapter.ValidateCredentialsAsync(mismatchedCredentials).GetAwaiter().GetResult(),
        "a public key that does not match the private seed must fail validation");
    Assert(unauthorizedHandler.Requests.Count == 0, "mismatched key pair must fail closed before HTTP");

    var failedHandler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
    { Content = new StringContent("{\"status\":\"failed\",\"code\":\"InvalidToken\"}") });
    using var failedHttp = new HttpClient(failedHandler);
    var failedAdapter = new NobitexReadOnlyAdapter(failedHttp);
    Assert(!failedAdapter.ValidateCredentialsAsync(CreateNobitexCredentials()).GetAwaiter().GetResult(),
        "failed API status must not validate credentials");
});

Run("Nobitex trading adapter submits Spot and Margin only to official endpoints", () =>
{
    var handler = new FakeHttpMessageHandler(request =>
    {
        if (request.RequestUri!.AbsolutePath == "/margin/markets/list")
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new StringContent("""{"status":"ok","markets":{"BTCUSDT":{"maxLeverage":"2.5","buyEnabled":true,"sellEnabled":true}}}""") };
        if (request.RequestUri.AbsolutePath is "/market/orders/add" or "/margin/orders/add")
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new StringContent("""{"status":"ok","order":{"id":12345}}""") };
        return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
    });
    using var http = new HttpClient(handler);
    using var readHttp = new HttpClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
    { Content = new StringContent("{\"status\":\"ok\"}") }));
    var adapter = new NobitexTradingAdapter(http, new NobitexReadOnlyAdapter(readHttp), () => true);
    var credentials = CreateNobitexCredentials();

    var spot = adapter.PlaceOrderAsync(credentials, new ExchangeOrderRequest("BTCUSDT", ExchangeTradeMode.Spot,
        ExchangeOrderSide.Buy, ExchangeOrderType.Limit, 0.01m, 65000m, ClientOrderId: "spot-test-1")).GetAwaiter().GetResult();
    Assert(spot.Accepted && spot.ExchangeOrderId == "12345", "Spot order was not accepted from mock response");
    var spotRequest = handler.Requests.Single(x => x.Uri!.AbsolutePath == "/market/orders/add");
    Assert(spotRequest.Method == HttpMethod.Post && spotRequest.Authorization is null && VerifyNobitexSignature(spotRequest),
        "Spot request must use a valid Ed25519 signature without Token auth");
    Assert(spotRequest.Body!.Contains("\"execution\":\"limit\"") && spotRequest.Body.Contains("\"clientOrderId\":\"spot-test-1\""),
        "Spot request payload is incomplete");

    var margin = adapter.PlaceOrderAsync(credentials, new ExchangeOrderRequest("BTCUSDT", ExchangeTradeMode.Margin,
        ExchangeOrderSide.Sell, ExchangeOrderType.Limit, 0.01m, 64000m, 2.5m)).GetAwaiter().GetResult();
    Assert(margin.Accepted, "Margin order was not accepted from mock response");
    Assert(handler.Requests.Any(x => x.Uri!.AbsolutePath == "/margin/markets/list"), "Margin market limits were not checked");
    var marginRequest = handler.Requests.Single(x => x.Uri!.AbsolutePath == "/margin/orders/add");
    Assert(marginRequest.Body!.Contains("\"leverage\":\"2.5\"") && marginRequest.Body.Contains("\"type\":\"sell\""),
        "Margin payload did not preserve decimal leverage and side");
    Assert(handler.Requests.All(x => x.Uri!.Host == "apiv2.nobitex.ir" && VerifyNobitexSignature(x)),
        "Trading requests must use valid Ed25519 signatures and the official host");
});

Run("Nobitex margin OCO close validates long/short price direction and sends two legs", () =>
{
    var handler = new FakeHttpMessageHandler(request =>
    {
        if (request.RequestUri!.AbsolutePath == "/positions/12/status")
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new StringContent("""{"status":"ok","position":{"srcCurrency":"btc","dstCurrency":"usdt","side":"buy","status":"Open","liability":"0.02","liabilityInOrder":"0"}}""") };
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        { Content = new StringContent("""{"status":"ok","orders":[{"id":81},{"id":82}]}""") };
    });
    using var http = new HttpClient(handler);
    using var readHttp = new HttpClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
    { Content = new StringContent("{\"status\":\"ok\"}") }));
    var adapter = new NobitexTradingAdapter(http, new NobitexReadOnlyAdapter(readHttp), () => true);
    var credentials = CreateNobitexCredentials();

    var invalid = adapter.PlaceMarginPositionOcoCloseAsync(credentials,
        new NobitexOcoCloseRequest("BTCUSDT", 12, true, 0.01m, 100m, 90m, 110m, 111m)).GetAwaiter().GetResult();
    Assert(!invalid.Accepted && handler.Requests.Count == 0, "invalid long OCO prices must not reach HTTP");

    var longOco = adapter.PlaceMarginPositionOcoCloseAsync(credentials,
        new NobitexOcoCloseRequest("BTCUSDT", 12, true, 0.01m, 100m, 110m, 90m, 89m)).GetAwaiter().GetResult();
    Assert(longOco.Accepted && longOco.ExchangeOrderIds.SequenceEqual(new[] { "81", "82" }), "long OCO did not return both order ids");
    Assert(handler.Requests.Count == 2 && handler.Requests.Any(x => x.Method == HttpMethod.Get && x.Uri!.AbsolutePath == "/positions/12/status"),
        "OCO must verify the target position before submission");
    var request = handler.Requests.Single(x => x.Method == HttpMethod.Post);
    Assert(request.Uri!.AbsolutePath == "/positions/12/close" && request.Body!.Contains("\"mode\":\"oco\""),
        "OCO close endpoint or mode incorrect");
    string body = request.Body!;
    Assert(body.Contains("\"price\":\"110\"") && body.Contains("\"stopPrice\":\"90\"") &&
           body.Contains("\"stopLimitPrice\":\"89\""), "OCO TP/SL fields missing");
});

Run("Nobitex Spot OCO exit validates market direction and available wallet balance", () =>
{
    var orderHandler = new FakeHttpMessageHandler(request =>
    {
        Assert(request.RequestUri!.AbsolutePath == "/market/orders/add", "Spot OCO must use the market order endpoint");
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("""{"status":"ok","orders":[{"id":901},{"id":902}]}""")
        };
    });
    var walletHandler = new FakeHttpMessageHandler(request => request.RequestUri!.AbsolutePath switch
    {
        "/users/profile" => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new StringContent("""{"status":"ok","profile":{}}""") },
        "/users/wallets/list" => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new StringContent("""{"status":"ok","wallets":[{"currency":"btc","balance":"0.5","blockedBalance":"0.1","activeBalance":"0.4"}]}""") },
        _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
    });
    using var orderHttp = new HttpClient(orderHandler);
    using var walletHttp = new HttpClient(walletHandler);
    var adapter = new NobitexTradingAdapter(orderHttp, new NobitexReadOnlyAdapter(walletHttp), () => true);
    var credentials = CreateNobitexCredentials();

    var invalid = adapter.PlaceSpotOcoExitAsync(credentials,
        new NobitexSpotOcoExitRequest("BTCUSDT", 0.2m, 100m, 90m, 110m, 111m)).GetAwaiter().GetResult();
    Assert(!invalid.Accepted && orderHandler.Requests.Count == 0 && walletHandler.Requests.Count == 0,
        "invalid Spot OCO prices must be rejected before network access");

    var insufficient = adapter.PlaceSpotOcoExitAsync(credentials,
        new NobitexSpotOcoExitRequest("BTCUSDT", 0.5m, 100m, 110m, 90m, 89m)).GetAwaiter().GetResult();
    Assert(!insufficient.Accepted && orderHandler.Requests.Count == 0,
        "Spot OCO must not exceed the free wallet balance");

    var result = adapter.PlaceSpotOcoExitAsync(credentials,
        new NobitexSpotOcoExitRequest("BTCUSDT", 0.3m, 100m, 110m, 90m, 89m, "spot-oco-test"))
        .GetAwaiter().GetResult();
    Assert(result.Accepted && result.ExchangeOrderIds.SequenceEqual(new[] { "901", "902" }),
        "Spot OCO must confirm both exchange order ids");
    Assert(walletHandler.Requests.Count == 4 && walletHandler.Requests.All(x => x.Method == HttpMethod.Get),
        "wallet verification must use profile and wallet GET only");
    var post = orderHandler.Requests.Single();
    Assert(post.Method == HttpMethod.Post && post.Authorization is null && VerifyNobitexSignature(post),
        "Spot OCO must submit an Ed25519-signed POST without Token auth");
    Assert(post.Body!.Contains("\"type\":\"sell\"") && post.Body.Contains("\"mode\":\"oco\"") &&
           post.Body.Contains("\"price\":\"110\"") && post.Body.Contains("\"stopPrice\":\"90\"") &&
           post.Body.Contains("\"stopLimitPrice\":\"89\"") && post.Body.Contains("\"clientOrderId\":\"spot-oco-test\""),
        "Spot OCO payload is missing one or more required fields");
});

Run("Nobitex Spot OCO remains fail-closed by default", () =>
{
    var orderHandler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    var walletHandler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    using var orderHttp = new HttpClient(orderHandler);
    using var walletHttp = new HttpClient(walletHandler);
    var adapter = new NobitexTradingAdapter(orderHttp, new NobitexReadOnlyAdapter(walletHttp));
    var result = adapter.PlaceSpotOcoExitAsync(CreateNobitexCredentials(),
        new NobitexSpotOcoExitRequest("BTCUSDT", 0.01m, 100m, 110m, 90m, 89m)).GetAwaiter().GetResult();
    Assert(!result.Accepted && orderHandler.Requests.Count == 0 && walletHandler.Requests.Count == 0,
        "default production gate must block Spot OCO before any network access");
});

Run("Nobitex trading adapter defaults to fail-closed and requires market price guard", () =>
{
    var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    using var http = new HttpClient(handler);
    using var readHttp = new HttpClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)));
    var adapter = new NobitexTradingAdapter(http, new NobitexReadOnlyAdapter(readHttp));
    var credentials = CreateNobitexCredentials();
    var blocked = adapter.PlaceOrderAsync(credentials, new ExchangeOrderRequest("BTCUSDT", ExchangeTradeMode.Spot,
        ExchangeOrderSide.Buy, ExchangeOrderType.Limit, 0.01m, 65000m)).GetAwaiter().GetResult();
    Assert(!blocked.Accepted && handler.Requests.Count == 0, "default execution gate must block every submission");

    var enabled = new NobitexTradingAdapter(http, new NobitexReadOnlyAdapter(readHttp), () => true);
    var missingGuard = enabled.PlaceOrderAsync(credentials, new ExchangeOrderRequest("BTCUSDT", ExchangeTradeMode.Spot,
        ExchangeOrderSide.Buy, ExchangeOrderType.Market, 0.01m)).GetAwaiter().GetResult();
    Assert(!missingGuard.Accepted && handler.Requests.Count == 0, "market order without price guard must be rejected locally");
    Assert(!OrderExecutionPolicy.IsLiveExecutionEnabled, "production live execution policy must remain disabled");
});

Run("Nobitex API-key validation uses official HTTPS profile GET only", () =>
{
    var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent("""{"status":"ok"}""")
    });
    using var http = new HttpClient(handler);
    var adapter = new NobitexReadOnlyAdapter(http);
    var credentials = CreateNobitexCredentials();
    Assert(adapter.ValidateCredentialsAsync(credentials).GetAwaiter().GetResult(),
        "valid API key pair should pass profile validation");
    Assert(handler.Requests.Count == 1 && handler.Requests[0].Method == HttpMethod.Get,
        "API-key validation must issue exactly one GET request");
    Assert(handler.Requests[0].Uri!.AbsoluteUri == "https://apiv2.nobitex.ir/users/profile" &&
        handler.Requests[0].Authorization is null && handler.Requests[0].NobitexKey == credentials.ApiKey &&
        VerifyNobitexSignature(handler.Requests[0]),
        "profile validation must use the official HTTPS host and a valid signed API key");

    var invalidHandler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent("""{"status":"failed"}""")
    });
    using var invalidHttp = new HttpClient(invalidHandler);
    Assert(!new NobitexReadOnlyAdapter(invalidHttp)
        .ValidateCredentialsAsync(CreateNobitexCredentials()).GetAwaiter().GetResult(),
        "rejected profile response must fail validation");
});

Run("Nobitex API key header preserves the exact issued Base64 value", () =>
{
    var urlSafeCredentials = CreateNobitexCredentials();
    byte[] publicKeyBytes = Convert.FromBase64String(urlSafeCredentials.ApiKey
        .Replace('-', '+').Replace('_', '/')
        .PadRight(urlSafeCredentials.ApiKey.Length + (4 - urlSafeCredentials.ApiKey.Length % 4) % 4, '='));
    string issuedApiKey = Convert.ToBase64String(publicKeyBytes); // standard Base64, including '=' padding
    var credentials = new ExchangeCredentials(issuedApiKey, urlSafeCredentials.ApiSecret);
    var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent("""{"status":"ok"}""")
    });
    using var http = new HttpClient(handler);
    var adapter = new NobitexReadOnlyAdapter(http);

    Assert(adapter.ValidateCredentialsAsync(credentials).GetAwaiter().GetResult(),
        "standard Base64 API key should validate in the signed request flow");
    Assert(handler.Requests.Single().NobitexKey == issuedApiKey,
        "Nobitex-Key must preserve the exact API key string and its padding");
    Assert(VerifyNobitexSignature(handler.Requests.Single()),
        "preserving API key representation must not break Ed25519 signature");
});

Run("Binance adapter is pinned to Spot Testnet and performs signed GET only", () =>
{
    const long serverTime = 1_730_000_000_000;
    var handler = new FakeHttpMessageHandler(request =>
    {
        if (request.RequestUri!.AbsolutePath == "/api/v3/time")
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new StringContent($"{{\"serverTime\":{serverTime}}}") };
        if (request.RequestUri.AbsolutePath == "/api/v3/account")
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"canTrade":true,"balances":[{"asset":"BTC","free":"0.5","locked":"0.1"},{"asset":"USDT","free":"25.0","locked":"0.0"}]}""")
            };
        return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
    });
    using var http = new HttpClient(handler);
    var adapter = new BinanceSpotTestnetReadOnlyAdapter(http);
    var credentials = new ExchangeCredentials("test-api-key", "test-api-secret");

    Assert(adapter.ValidateCredentialsAsync(credentials).GetAwaiter().GetResult(), "valid Testnet credentials should pass");
    var snapshot = adapter.GetAccountSnapshotAsync(credentials).GetAwaiter().GetResult();
    var btc = snapshot.Balances.Single(x => x.Asset == "BTC");
    Assert(btc.Total == 0.6m && btc.Available == 0.5m && btc.Locked == 0.1m, "Binance balance parsing incorrect");
    Assert(handler.Requests.Count == 4, "expected time+account GET pairs");
    Assert(handler.Requests.All(x => x.Method == HttpMethod.Get), "Binance read-only adapter must only use GET");
    Assert(handler.Requests.All(x => x.Uri!.Host == "testnet.binance.vision"), "Binance adapter must use Testnet host only");
    Assert(handler.Requests.Where(x => x.Uri!.AbsolutePath.EndsWith("/account", StringComparison.Ordinal))
        .All(x => x.ApiKey == "test-api-key" && x.Query!.Contains("signature=") && x.Query.Contains("timestamp=")),
        "signed account request headers/query are missing");

    var order = adapter.PlaceOrderAsync(credentials, new ExchangeOrderRequest("BTCUSDT", ExchangeTradeMode.Spot,
        ExchangeOrderSide.Buy, ExchangeOrderType.Market, 0.01m)).GetAwaiter().GetResult();
    Assert(!order.Accepted && handler.Requests.Count == 4, "Binance PlaceOrder must reject without network access");
    Assert(!adapter.CanPlaceOrders && !adapter.Capabilities.SupportsFutures, "Testnet adapter capabilities incorrect");
});

Run("legacy SQLite database upgrades safely and idempotently", () =>
{
    string path = Path.Combine(Path.GetTempPath(), $"crypto-analyzer-migration-{Guid.NewGuid():N}.db");
    string cs = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, ForeignKeys = true }.ToString();
    try
    {
        using (var connection = new SqliteConnection(cs))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE Users (ChatId INTEGER PRIMARY KEY, Username TEXT, FirstName TEXT, IsActive INTEGER DEFAULT 1, CreatedAt TEXT DEFAULT CURRENT_TIMESTAMP);
                CREATE TABLE SignalTracks (Id INTEGER PRIMARY KEY AUTOINCREMENT, Symbol TEXT NOT NULL, EntryPrice REAL NOT NULL, StopLoss REAL NOT NULL, TakeProfit REAL NOT NULL, Status TEXT DEFAULT 'Pending', SignalAt TEXT DEFAULT CURRENT_TIMESTAMP, ResolvedAt TEXT, ResolvePrice REAL);
                CREATE TABLE OpenTrades (Id INTEGER PRIMARY KEY AUTOINCREMENT, ChatId INTEGER NOT NULL, Symbol TEXT NOT NULL, EntryPrice REAL NOT NULL, StopLoss REAL NOT NULL, TakeProfit REAL NOT NULL, Status TEXT DEFAULT 'Open', ExitPrice REAL, ExitReason TEXT, OpenedAt TEXT DEFAULT CURRENT_TIMESTAMP, ClosedAt TEXT);
                INSERT INTO Users(ChatId) VALUES (123);
                INSERT INTO SignalTracks(Symbol, EntryPrice, StopLoss, TakeProfit) VALUES ('BTCUSDT', 100, 95, 110);
                INSERT INTO OpenTrades(ChatId, Symbol, EntryPrice, StopLoss, TakeProfit) VALUES (123, 'BTCUSDT', 100, 95, 110);
                """;
            command.ExecuteNonQuery();
        }

        DatabaseInitializer.Initialize(cs);
        DatabaseInitializer.Initialize(cs);

        using (var verify = new SqliteConnection(cs))
        {
            verify.Open();
            Assert(HasColumn(verify, "OpenTrades", "Side"), "OpenTrades.Side missing");
            Assert(HasColumn(verify, "OpenTrades", "Leverage"), "OpenTrades.Leverage missing");
            Assert(HasColumn(verify, "OpenTrades", "TradeMode"), "OpenTrades.TradeMode missing");
            Assert(HasColumn(verify, "OpenTrades", "Quantity"), "OpenTrades.Quantity missing");
            Assert(HasColumn(verify, "OpenTrades", "OcoStatus"), "OpenTrades.OcoStatus missing");
            Assert(HasColumn(verify, "OpenTrades", "OcoOrderIds"), "OpenTrades.OcoOrderIds missing");
            Assert(HasColumn(verify, "SignalTracks", "TradeMode"), "SignalTracks.TradeMode missing");
            Assert(TableExists(verify, "UserExchangeAccounts"), "user exchange account table missing");
            Assert(TableExists(verify, "ManualOrderApprovals"), "manual approvals table missing");
            Assert(HasColumn(verify, "ManualOrderApprovals", "Leverage"), "manual approval leverage column missing");
            Assert(HasColumn(verify, "ManualOrderApprovals", "PriceGuard"), "manual approval price guard column missing");
            Assert(HasColumn(verify, "ManualOrderApprovals", "TradeRegisteredAt"), "manual approval trade-registration column missing");

            bool orphanRejected = false;
            try
            {
                using var orphan = verify.CreateCommand();
                orphan.CommandText = "INSERT INTO UserExchangeAccounts(ChatId, ExchangeCode, AccountLabel, ApiKeyEncrypted, ApiSecretEncrypted) VALUES (999, 'TEST', 'orphan', 'enc', 'enc')";
                orphan.ExecuteNonQuery();
            }
            catch (SqliteException)
            {
                orphanRejected = true;
            }
            Assert(orphanRejected, "exchange account must belong to a registered user");

            using var data = verify.CreateCommand();
            data.CommandText = "SELECT Side, Leverage, TradeMode, ExchangeCode FROM OpenTrades WHERE ChatId=123";
            using var reader = data.ExecuteReader();
            Assert(reader.Read(), "legacy trade row was lost");
            Assert(reader.GetString(0) == "LONG", "legacy side default incorrect");
            Assert(reader.GetInt32(1) == 1, "legacy leverage default incorrect");
            Assert(reader.GetString(2) == "MARGIN", "legacy mode default incorrect");
            Assert(reader.GetString(3) == "NOBITEX", "legacy exchange default incorrect");
        }
    }
    finally
    {
        if (File.Exists(path)) File.Delete(path);
    }
});

Console.WriteLine($"\n{passed} tests passed.");

void Run(string name, Action test)
{
    test();
    passed++;
    Console.WriteLine($"PASS {name}");
}

void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

bool HasColumn(SqliteConnection connection, string table, string column)
{
    using var command = connection.CreateCommand();
    command.CommandText = $"PRAGMA table_info({table});";
    using var reader = command.ExecuteReader();
    while (reader.Read())
        if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase)) return true;
    return false;
}

bool TableExists(SqliteConnection connection, string table)
{
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type='table' AND name=$name";
    command.Parameters.AddWithValue("$name", table);
    return Convert.ToInt32(command.ExecuteScalar()) == 1;
}

ExchangeCredentials CreateNobitexCredentials(string? publicKeyOverride = null, string? privateKeyOverride = null)
{
    byte[] seed = Enumerable.Range(1, 32).Select(x => (byte)x).ToArray();
    using Key key = Key.Import(SignatureAlgorithm.Ed25519, seed, KeyBlobFormat.RawPrivateKey);
    string publicKey = Convert.ToBase64String(key.PublicKey.Export(KeyBlobFormat.RawPublicKey))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    string privateKey = Convert.ToBase64String(seed).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    return new ExchangeCredentials(publicKeyOverride ?? publicKey, privateKeyOverride ?? privateKey);
}

bool VerifyNobitexSignature((HttpMethod Method, string? Authorization, string? ApiKey, Uri? Uri,
    string? Query, string? Body, string? NobitexKey, string? NobitexTimestamp, string? NobitexSignature) request)
{
    if (request.NobitexKey is null || request.NobitexTimestamp is null || request.NobitexSignature is null || request.Uri is null)
        return false;
    try
    {
        static byte[] DecodeUrlSafe(string value) => Convert.FromBase64String(
            value.Replace('-', '+').Replace('_', '/').PadRight(value.Length + (4 - value.Length % 4) % 4, '='));
        byte[] publicKey = DecodeUrlSafe(request.NobitexKey);
        byte[] signature = DecodeUrlSafe(request.NobitexSignature);
        string pathAndQuery = request.Uri.AbsolutePath + request.Uri.Query;
        byte[] message = Encoding.UTF8.GetBytes(request.NobitexTimestamp + request.Method.Method.ToUpperInvariant() +
            pathAndQuery + (request.Body ?? ""));
        PublicKey key = PublicKey.Import(SignatureAlgorithm.Ed25519, publicKey, KeyBlobFormat.RawPublicKey);
        return long.TryParse(request.NobitexTimestamp, out long timestamp) &&
            Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - timestamp) <= 30 &&
            SignatureAlgorithm.Ed25519.Verify(key, message, signature);
    }
    catch (FormatException) { return false; }
    catch (CryptographicException) { return false; }
}

sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
{
    public List<(HttpMethod Method, string? Authorization, string? ApiKey, Uri? Uri, string? Query, string? Body,
        string? NobitexKey, string? NobitexTimestamp, string? NobitexSignature)> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add((request.Method, request.Headers.Authorization?.ToString(),
            request.Headers.TryGetValues("X-MBX-APIKEY", out var apiKeys) ? apiKeys.Single() : null,
            request.RequestUri, request.RequestUri?.Query, request.Content?.ReadAsStringAsync().GetAwaiter().GetResult(),
            request.Headers.TryGetValues("Nobitex-Key", out var nobitexKeys) ? nobitexKeys.Single() : null,
            request.Headers.TryGetValues("Nobitex-Timestamp", out var timestamps) ? timestamps.Single() : null,
            request.Headers.TryGetValues("Nobitex-Signature", out var signatures) ? signatures.Single() : null));
        return Task.FromResult(responseFactory(request));
    }
}
