namespace MarketLens.Models;

public sealed record PriceBar(DateTimeOffset Time, decimal Open, decimal High, decimal Low,
    decimal Close, decimal Volume);
public sealed record MarketQuote(decimal Price, decimal? Bid, decimal? Ask,
    DateTimeOffset UpdatedAt, string MarketState, decimal? PeTtm = null,
    decimal? Eps = null, bool IsEquity = true)
{
    public bool IsOpen => MarketState is "MORNING" or "AFTERNOON";
}
public sealed record Fundamentals(DateTimeOffset PeriodEnd, decimal RevenueGrowthPercent,
    decimal EarningsGrowthPercent, decimal FreeCashFlow, decimal DebtToEquity,
    decimal ReturnOnEquityPercent, decimal PeTtm, decimal Eps, bool IsFinancialCompany = false,
    DateTimeOffset? ValuationAsOf = null);
public sealed record MarketData(string Symbol, string Name, string Currency, string Source,
    DateTimeOffset RetrievedAt, PriceBar[] Daily, PriceBar[] Intraday,
    MarketQuote? Quote = null, Fundamentals? Fundamentals = null,
    bool IsSample = false, bool IsImported = false, bool SplitAdjusted = false,
    string[]? Warnings = null)
{
    public decimal? Price => Quote?.Price ?? Daily.LastOrDefault()?.Close;
}
public sealed record Metric(string Name, string Value);
public sealed record Recommendation(string Signal, string Summary, string Basis,
    DateTimeOffset? AsOf, Metric[] Metrics, string[] Reasons, string[] Risks,
    decimal? StopReference = null, decimal? TargetReference = null);
public sealed record BacktestResult(decimal ReturnPercent, decimal BuyHoldPercent,
    decimal MaxDrawdownPercent, int ClosedTrades, decimal? WinRatePercent,
    DateTimeOffset Start, DateTimeOffset End, decimal CostBps);
public sealed record AiExplanation(string Summary, string[] Risks);
