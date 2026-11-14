using CryptoAnalyzer.Exchanges;
using Microsoft.Data.Sqlite;

namespace CryptoAnalyzer.Persistence;

public sealed record ManualOrderDraft(
    long ChatId, long ExchangeAccountId, string ExchangeCode, string Symbol,
    ExchangeTradeMode Mode, ExchangeOrderSide Side, ExchangeOrderType OrderType,
    decimal Quantity, decimal? LimitPrice = null, decimal? StopLoss = null,
    decimal? TakeProfit = null, decimal Leverage = 1m, decimal? PriceGuard = null);

public enum ApprovalTransitionResult { Updated, NotFound, Expired, InvalidState, AccountInactive }

/// <summary>Persists manual review only. Approval never calls an exchange adapter.</summary>
public sealed class ManualOrderApprovalRepository(string connectionString)
{
    public string Create(ManualOrderDraft draft, TimeSpan? lifetime = null)
    {
        Validate(draft);
        var ttl = lifetime ?? TimeSpan.FromMinutes(5);
        if (ttl < TimeSpan.FromSeconds(30) || ttl > TimeSpan.FromMinutes(30))
            throw new ArgumentOutOfRangeException(nameof(lifetime), "Approval lifetime must be between 30 seconds and 30 minutes.");

        string id = Guid.NewGuid().ToString("N");
        DateTime expiresAt = DateTime.UtcNow.Add(ttl);
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ManualOrderApprovals
                (Id, ChatId, ExchangeAccountId, ExchangeCode, Symbol, TradeMode, Side,
                 OrderType, Quantity, LimitPrice, StopLoss, TakeProfit, Leverage, PriceGuard, Status, ExpiresAt)
            SELECT $id, $chatId, a.Id, a.ExchangeCode, $symbol, $mode, $side,
                   $orderType, $quantity, $limitPrice, $stopLoss, $takeProfit, $leverage, $priceGuard,
                   'AwaitingApproval', $expiresAt
            FROM UserExchangeAccounts a
            WHERE a.Id=$accountId AND a.ChatId=$chatId AND a.IsActive=1
              AND a.ExchangeCode=$exchangeCode;
            SELECT changes();
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$chatId", draft.ChatId);
        command.Parameters.AddWithValue("$accountId", draft.ExchangeAccountId);
        command.Parameters.AddWithValue("$exchangeCode", draft.ExchangeCode.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("$symbol", draft.Symbol.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("$mode", draft.Mode.ToString().ToUpperInvariant());
        command.Parameters.AddWithValue("$side", draft.Side.ToString().ToUpperInvariant());
        command.Parameters.AddWithValue("$orderType", draft.OrderType.ToString().ToUpperInvariant());
        command.Parameters.AddWithValue("$quantity", Format(draft.Quantity));
        command.Parameters.AddWithValue("$limitPrice", DbValue(draft.LimitPrice));
        command.Parameters.AddWithValue("$stopLoss", DbValue(draft.StopLoss));
        command.Parameters.AddWithValue("$takeProfit", DbValue(draft.TakeProfit));
        command.Parameters.AddWithValue("$leverage", draft.Leverage);
        command.Parameters.AddWithValue("$priceGuard", DbValue(draft.PriceGuard));
        command.Parameters.AddWithValue("$expiresAt", expiresAt.ToString("O"));
        if (Convert.ToInt32(command.ExecuteScalar()) != 1)
            throw new KeyNotFoundException("Active exchange account not found for this user and exchange.");
        return id;
    }

    public ApprovalTransitionResult ApproveForManualExecution(long chatId, string approvalId)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ManualOrderApprovals
            SET Status='ApprovedForManualExecution', ApprovedAt=$now
            WHERE Id=$id AND ChatId=$chatId AND Status='AwaitingApproval'
              AND ExpiresAt > $now
              AND EXISTS (SELECT 1 FROM UserExchangeAccounts a
                          WHERE a.Id=ManualOrderApprovals.ExchangeAccountId
                            AND a.ChatId=ManualOrderApprovals.ChatId AND a.IsActive=1);
            SELECT changes();
            """;
        command.Parameters.AddWithValue("$id", approvalId);
        command.Parameters.AddWithValue("$chatId", chatId);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        if (Convert.ToInt32(command.ExecuteScalar()) == 1) return ApprovalTransitionResult.Updated;
        return GetFailureReason(chatId, approvalId);
    }

    public ApprovalTransitionResult Reject(long chatId, string approvalId)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ManualOrderApprovals SET Status='Rejected', RejectedAt=$now
            WHERE Id=$id AND ChatId=$chatId AND Status='AwaitingApproval';
            SELECT changes();
            """;
        command.Parameters.AddWithValue("$id", approvalId);
        command.Parameters.AddWithValue("$chatId", chatId);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        if (Convert.ToInt32(command.ExecuteScalar()) == 1) return ApprovalTransitionResult.Updated;
        return GetFailureReason(chatId, approvalId);
    }

    public string? GetStatus(long chatId, string approvalId)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Status FROM ManualOrderApprovals WHERE Id=$id AND ChatId=$chatId;";
        command.Parameters.AddWithValue("$id", approvalId);
        command.Parameters.AddWithValue("$chatId", chatId);
        return command.ExecuteScalar() as string;
    }

    public bool CanRegisterAsOpenPosition(long chatId, string approvalId)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM ManualOrderApprovals p
                JOIN UserExchangeAccounts a ON a.Id=p.ExchangeAccountId AND a.ChatId=p.ChatId AND a.IsActive=1
                WHERE p.Id=$id AND p.ChatId=$chatId AND p.Status='ApprovedForManualExecution'
                  AND p.TradeRegisteredAt IS NULL AND p.StopLoss IS NOT NULL AND p.TakeProfit IS NOT NULL
                  AND (p.TradeMode='MARGIN' OR (p.TradeMode='SPOT' AND p.Side='BUY')));
            """;
        command.Parameters.AddWithValue("$id", approvalId);
        command.Parameters.AddWithValue("$chatId", chatId);
        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }

    public bool RegisterAsOpenPosition(long chatId, string approvalId, decimal actualEntryPrice)
    {
        if (actualEntryPrice <= 0) throw new ArgumentOutOfRangeException(nameof(actualEntryPrice));

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO OpenTrades
                (ChatId, Symbol, EntryPrice, StopLoss, TakeProfit, Status, Side, Leverage,
                 TradeMode, ExchangeCode, ExchangeAccountId, Quantity)
            SELECT p.ChatId, p.Symbol, $entryPrice, p.StopLoss, p.TakeProfit, 'Open',
                   CASE WHEN p.TradeMode='MARGIN' AND p.Side='SELL' THEN 'SHORT'
                        WHEN p.TradeMode='MARGIN' THEN 'LONG' ELSE 'BUY' END,
                   CAST(p.Leverage AS INTEGER), p.TradeMode, p.ExchangeCode, p.ExchangeAccountId, p.Quantity
            FROM ManualOrderApprovals p
            JOIN UserExchangeAccounts a ON a.Id=p.ExchangeAccountId AND a.ChatId=p.ChatId AND a.IsActive=1
            WHERE p.Id=$id AND p.ChatId=$chatId AND p.Status='ApprovedForManualExecution'
              AND p.TradeRegisteredAt IS NULL AND p.StopLoss IS NOT NULL AND p.TakeProfit IS NOT NULL
              AND (p.TradeMode='MARGIN' OR (p.TradeMode='SPOT' AND p.Side='BUY'));
            SELECT changes();
            """;
        command.Parameters.AddWithValue("$entryPrice", Format(actualEntryPrice));
        command.Parameters.AddWithValue("$id", approvalId);
        command.Parameters.AddWithValue("$chatId", chatId);
        bool inserted = Convert.ToInt32(command.ExecuteScalar()) == 1;
        if (inserted)
        {
            using var mark = connection.CreateCommand();
            mark.Transaction = transaction;
            mark.CommandText = "UPDATE ManualOrderApprovals SET TradeRegisteredAt=$now WHERE Id=$id AND ChatId=$chatId AND TradeRegisteredAt IS NULL;";
            mark.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            mark.Parameters.AddWithValue("$id", approvalId);
            mark.Parameters.AddWithValue("$chatId", chatId);
            mark.ExecuteNonQuery();
        }
        transaction.Commit();
        return inserted;
    }

    public bool TryBeginSpotOcoSubmission(long chatId, int tradeId)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE OpenTrades
            SET OcoStatus='Submitting', OcoMessage=NULL
            WHERE Id=$id AND ChatId=$chatId AND Status='Open'
              AND TradeMode='SPOT' AND Side='BUY' AND CAST(Quantity AS REAL)>0
              AND OcoStatus IN ('NotSubmitted','Rejected')
              AND EXISTS (SELECT 1 FROM UserExchangeAccounts a
                          WHERE a.Id=OpenTrades.ExchangeAccountId AND a.ChatId=OpenTrades.ChatId AND a.IsActive=1);
            SELECT changes();
            """;
        command.Parameters.AddWithValue("$id", tradeId);
        command.Parameters.AddWithValue("$chatId", chatId);
        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }

    public bool CompleteSpotOcoSubmission(long chatId, int tradeId, string status,
        IReadOnlyList<string> orderIds, string message)
    {
        if (status is not "Accepted" and not "Rejected" and not "OutcomeUnknown")
            throw new ArgumentException("Invalid OCO result status.", nameof(status));
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE OpenTrades
            SET OcoStatus=$status, OcoOrderIds=$ids, OcoMessage=$message
            WHERE Id=$id AND ChatId=$chatId AND Status='Open'
              AND TradeMode='SPOT' AND Side='BUY' AND OcoStatus='Submitting';
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$ids", System.Text.Json.JsonSerializer.Serialize(orderIds));
        command.Parameters.AddWithValue("$message", message.Length > 500 ? message[..500] : message);
        command.Parameters.AddWithValue("$id", tradeId);
        command.Parameters.AddWithValue("$chatId", chatId);
        return command.ExecuteNonQuery() == 1;
    }

    private ApprovalTransitionResult GetFailureReason(long chatId, string id)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.Status, p.ExpiresAt,
                   EXISTS (SELECT 1 FROM UserExchangeAccounts a
                           WHERE a.Id=p.ExchangeAccountId AND a.ChatId=p.ChatId AND a.IsActive=1)
            FROM ManualOrderApprovals p WHERE p.Id=$id AND p.ChatId=$chatId;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$chatId", chatId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return ApprovalTransitionResult.NotFound;
        if (reader.GetString(0) != "AwaitingApproval") return ApprovalTransitionResult.InvalidState;
        if (DateTime.Parse(reader.GetString(1)).ToUniversalTime() <= DateTime.UtcNow)
            return ApprovalTransitionResult.Expired;
        if (reader.GetInt32(2) != 1) return ApprovalTransitionResult.AccountInactive;
        return ApprovalTransitionResult.InvalidState;
    }

    private static void Validate(ManualOrderDraft draft)
    {
        if (draft.ChatId <= 0 || draft.ExchangeAccountId <= 0) throw new ArgumentOutOfRangeException(nameof(draft));
        if (!Enum.IsDefined(draft.Mode) || !Enum.IsDefined(draft.Side) || !Enum.IsDefined(draft.OrderType))
            throw new ArgumentException("Unsupported order mode, side or type.", nameof(draft));
        if (string.IsNullOrWhiteSpace(draft.ExchangeCode) || draft.ExchangeCode.Length > 32)
            throw new ArgumentException("Invalid exchange code.", nameof(draft));
        if (!draft.ExchangeCode.Equals("NOBITEX", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Order execution is restricted to Nobitex. Other exchanges remain read-only.", nameof(draft));
        if (draft.Mode == ExchangeTradeMode.Futures)
            throw new ArgumentException("Nobitex Futures order execution is not supported; use Spot or Margin.", nameof(draft));
        if (string.IsNullOrWhiteSpace(draft.Symbol) || draft.Symbol.Length > 40 ||
            draft.Symbol.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new ArgumentException("Invalid trading symbol.", nameof(draft));
        if (draft.Quantity <= 0) throw new ArgumentOutOfRangeException(nameof(draft.Quantity));
        if (draft.OrderType == ExchangeOrderType.Limit && draft.LimitPrice is not > 0)
            throw new ArgumentException("Limit orders require a positive limit price.", nameof(draft));
        if (draft.OrderType == ExchangeOrderType.Market && draft.LimitPrice is not null)
            throw new ArgumentException("Market orders must not specify a limit price.", nameof(draft));
        if (draft.OrderType == ExchangeOrderType.Market && draft.Mode == ExchangeTradeMode.Spot && draft.PriceGuard is not > 0)
            throw new ArgumentException("Nobitex Spot market orders require a positive price guard.", nameof(draft.PriceGuard));
        if (draft.PriceGuard is <= 0)
            throw new ArgumentException("Price guard must be positive when supplied.", nameof(draft.PriceGuard));
        if (draft.Leverage < 1 || draft.Leverage > 20 || draft.Leverage * 2m % 1m != 0)
            throw new ArgumentOutOfRangeException(nameof(draft.Leverage), "Leverage must be >=1, <=20 and use 0.5 increments; the exchange market/account maximum is checked before submission.");
        if (draft.Mode == ExchangeTradeMode.Spot && draft.Leverage != 1)
            throw new ArgumentException("Spot orders cannot use leverage.", nameof(draft.Leverage));
        if (draft.StopLoss is <= 0 || draft.TakeProfit is <= 0)
            throw new ArgumentException("Stop-loss and take-profit must be positive when supplied.", nameof(draft));

        if (draft.Mode == ExchangeTradeMode.Spot)
        {
            if (draft.Side == ExchangeOrderSide.Buy)
            {
                if (draft.StopLoss is null || draft.TakeProfit is null)
                    throw new ArgumentException("Spot buy previews require both stop-loss and take-profit.", nameof(draft));

                decimal entry = draft.OrderType == ExchangeOrderType.Limit
                    ? draft.LimitPrice!.Value
                    : draft.PriceGuard!.Value;
                if (draft.StopLoss.Value >= entry || draft.TakeProfit.Value <= entry)
                    throw new ArgumentException("For a Spot buy, stop-loss must be below and take-profit above the reference entry price.", nameof(draft));

                decimal riskPercent = (entry - draft.StopLoss.Value) / entry * 100m;
                decimal rewardPercent = (draft.TakeProfit.Value - entry) / entry * 100m;
                if (riskPercent > 10m || rewardPercent < riskPercent * 1.2m)
                    throw new ArgumentException("Spot risk/reward rejected: price risk must be <=10% and reward/risk >=1.2.", nameof(draft));
            }
            else if (draft.StopLoss is not null || draft.TakeProfit is not null)
            {
                throw new ArgumentException("A Spot sell is an exit order; define stop-loss/take-profit on the open Spot buy position instead.", nameof(draft));
            }
        }

        if (draft.Mode == ExchangeTradeMode.Margin)
        {
            if (draft.OrderType != ExchangeOrderType.Limit || draft.LimitPrice is not > 0)
                throw new ArgumentException("Margin/Futures previews require a limit entry until a trusted live-price and risk service is connected.", nameof(draft));
            if (draft.StopLoss is null || draft.TakeProfit is null)
                throw new ArgumentException("Margin/Futures orders require both stop-loss and take-profit.", nameof(draft));

            decimal entry = draft.LimitPrice.Value;
            bool isLong = draft.Side == ExchangeOrderSide.Buy;
            bool directionValid = isLong
                ? draft.StopLoss.Value < entry && draft.TakeProfit.Value > entry
                : draft.TakeProfit.Value < entry && draft.StopLoss.Value > entry;
            if (!directionValid)
                throw new ArgumentException("Stop-loss and take-profit are on the wrong side of the limit entry.", nameof(draft));

            decimal riskPercent = (isLong ? entry - draft.StopLoss.Value : draft.StopLoss.Value - entry) / entry * 100m;
            decimal rewardPercent = (isLong ? draft.TakeProfit.Value - entry : entry - draft.TakeProfit.Value) / entry * 100m;
            if (riskPercent * draft.Leverage > 4m || rewardPercent < riskPercent * 1.2m)
                throw new ArgumentException("Risk/reward rejected: leveraged price risk must be <=4% and reward/risk >=1.2.", nameof(draft));
        }
    }

    private static object DbValue(decimal? value) => value is null ? DBNull.Value : Format(value.Value);
    private static string Format(decimal value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
