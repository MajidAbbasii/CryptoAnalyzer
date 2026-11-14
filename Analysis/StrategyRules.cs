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

    public static bool IsLongTakeProfitHit(decimal price, decimal takeProfit) => price >= takeProfit;
    public static bool IsLongStopLossHit(decimal price, decimal stopLoss) => price <= stopLoss;
    public static bool IsShortTakeProfitHit(decimal price, decimal takeProfit) => price <= takeProfit;
    public static bool IsShortStopLossHit(decimal price, decimal stopLoss) => price >= stopLoss;

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
