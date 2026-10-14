namespace MarketLens.Models;

// Separate from daily-analysis inputs: a one-minute provider bar, not a verified last trade.
public sealed record StockQuoteSnapshot(string Symbol, decimal Price, string Currency,
    DateTimeOffset BarTime, DateTimeOffset RetrievedAt, bool? MarketOpen);
public sealed record StockQuoteResult(StockQuoteSnapshot? Quote, string? Error, int RefreshAfterSeconds);
