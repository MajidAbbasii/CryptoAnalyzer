namespace CryptoAnalyzer.Analysis;

public enum TradeMode
{
    Spot,
    Margin,
    Futures
}

public enum TradeSide
{
    Buy,
    Long,
    Short
}

public readonly record struct TradePlan(decimal Entry, decimal StopLoss, decimal TakeProfit,
    decimal RiskPercent, decimal RewardRiskRatio);

public enum SignalOutcome
{
    Pending,
    HitStopLoss,
    HitTakeProfit,
    Expired
}

public static class StrategyRules
{
    public static bool IsCandleClosed(DateTime candleOpenUtc, TimeSpan interval, DateTime nowUtc)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        DateTime openUtc = candleOpenUtc.Kind == DateTimeKind.Utc
            ? candleOpenUtc
            : DateTime.SpecifyKind(candleOpenUtc, DateTimeKind.Utc);
        DateTime currentUtc = nowUtc.Kind == DateTimeKind.Utc
            ? nowUtc
            : nowUtc.ToUniversalTime();
        return openUtc + interval <= currentUtc.AddSeconds(-1);
    }

    public static bool IsNearSupport(decimal price, decimal support, decimal maxDistancePercent = 2m) =>
        price > 0 && support > 0 && support < price &&
        (price - support) / price * 100m <= maxDistancePercent;

    public static bool IsNearResistance(decimal price, decimal resistance, decimal maxDistancePercent = 2m) =>
        price > 0 && resistance > price &&
        (resistance - price) / price * 100m <= maxDistancePercent;

    public static bool IsConfirmedLong(bool bothTimeframesBullish, bool nearSupport, bool aboveSma50) =>
        bothTimeframesBullish && (nearSupport || aboveSma50);

    public static bool IsConfirmedShort(bool bothTimeframesBearish, bool nearResistance, bool belowSma50) =>
        bothTimeframesBearish && (nearResistance || belowSma50);

    public static bool IsLongTakeProfitHit(decimal price, decimal takeProfit) => price >= takeProfit;
    public static bool IsLongStopLossHit(decimal price, decimal stopLoss) => price <= stopLoss;
    public static bool IsShortTakeProfitHit(decimal price, decimal takeProfit) => price <= takeProfit;
    public static bool IsShortStopLossHit(decimal price, decimal stopLoss) => price >= stopLoss;

    public static SignalOutcome EvaluateSignal(string side, decimal price, decimal stopLoss,
        decimal takeProfit, DateTime signalAt, DateTime now, TimeSpan maxAge)
    {
        if (maxAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maxAge));

        DateTime signalUtc = signalAt.Kind == DateTimeKind.Utc
            ? signalAt
            : signalAt.ToUniversalTime();
        DateTime nowUtc = now.Kind == DateTimeKind.Utc
            ? now
            : now.ToUniversalTime();

        // Expiration takes precedence: a stale signal must not become a win/loss
        // merely because price eventually crossed a threshold after its lifetime.
        if (nowUtc - signalUtc >= maxAge)
            return SignalOutcome.Expired;

        if (price <= 0 || stopLoss <= 0 || takeProfit <= 0)
            return SignalOutcome.Pending;

        bool isShort = string.Equals(side, "SHORT", StringComparison.OrdinalIgnoreCase);
        bool hitStopLoss = isShort
            ? IsShortStopLossHit(price, stopLoss)
            : IsLongStopLossHit(price, stopLoss);
        if (hitStopLoss)
            return SignalOutcome.HitStopLoss;

        bool hitTakeProfit = isShort
            ? IsShortTakeProfitHit(price, takeProfit)
            : IsLongTakeProfitHit(price, takeProfit);
        return hitTakeProfit ? SignalOutcome.HitTakeProfit : SignalOutcome.Pending;
    }

    public static decimal CalculatePnlPercent(TradeSide side, decimal entry, decimal exit)
    {
        if (entry <= 0) throw new ArgumentOutOfRangeException(nameof(entry));
        return side == TradeSide.Short
            ? (entry - exit) / entry * 100m
            : (exit - entry) / entry * 100m;
    }

    public static bool TryCreatePlan(TradeSide side, decimal entry, decimal support,
        decimal resistance, out TradePlan plan, decimal minRewardRisk = 1.2m,
        decimal maxRiskPercent = 4m)
    {
        plan = default;
        if (entry <= 0) return false;

        decimal stopLoss;
        decimal takeProfit;
        if (side is TradeSide.Long or TradeSide.Buy)
        {
            stopLoss = support > 0 && support < entry ? support * 0.995m : entry * 0.985m;
            takeProfit = resistance > entry
                ? Math.Min(resistance * 0.997m, entry * 1.03m)
                : entry * 1.025m;
        }
        else
        {
            stopLoss = resistance > entry ? resistance * 1.005m : entry * 1.015m;
            takeProfit = support > 0 && support < entry
                ? Math.Max(support * 1.003m, entry * 0.975m)
                : entry * 0.975m;
        }

        decimal risk = side is TradeSide.Long or TradeSide.Buy
            ? entry - stopLoss : stopLoss - entry;
        decimal reward = side is TradeSide.Long or TradeSide.Buy
            ? takeProfit - entry : entry - takeProfit;
        if (risk <= 0 || reward <= 0) return false;

        decimal riskPercent = risk / entry * 100m;
        decimal rewardRisk = reward / risk;
        if (riskPercent > maxRiskPercent || rewardRisk < minRewardRisk) return false;

        plan = new TradePlan(entry, stopLoss, takeProfit, riskPercent, rewardRisk);
        return true;
    }
}
