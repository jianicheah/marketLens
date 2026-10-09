using MarketLens.Models;
namespace MarketLens.Services;

public static class DemoData
{
    public static MarketData Create(DateTimeOffset now)
    {
        var dates = Enumerable.Range(1, 260).Select(i => now.Date.AddDays(-i)).Where(d => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            .Take(150).Reverse().ToArray();
        var bars = dates.Select((d, i) =>
        {
            var close = Math.Round(100m + i * 0.12m + (decimal)Math.Sin(i * 0.6) * 2, 2);
            return new PriceBar(new DateTimeOffset(d.AddHours(16), TimeSpan.Zero), close - 0.3m, close + 0.8m,
                close - 0.9m, close, 100000 + i * 100);
        }).ToArray();
        return new("US.DEMO", "Fictional learning company", "USD", "Synthetic demonstration", now, bars, [],
            IsSample: true, SplitAdjusted: true);
    }
}
