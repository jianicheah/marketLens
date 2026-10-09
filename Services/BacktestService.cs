using MarketLens.Models;
namespace MarketLens.Services;

public sealed class BacktestService
{
    // Signal at prior close; execution at next open. No future bars enter the decision.
    public BacktestResult? Run(IReadOnlyList<PriceBar> bars, decimal costBps = 10)
    {
        if (costBps is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(costBps));
        if (bars.Count < 90) return null;
        var cost = costBps / 10000; decimal cash = 10000, shares = 0, entryCost = 0, peak = cash, drawdown = 0;
        var trades = 0; var wins = 0;
        for (var i = 60; i < bars.Count; i++)
        {
            var signal = Indicators.TrendSignal(bars.Take(i).ToArray());
            if (signal == "Buy" && shares == 0)
            {
                entryCost = cash; shares = cash / (bars[i].Open * (1 + cost)); cash = 0;
            }
            else if (signal == "Sell" && shares > 0)
            {
                cash = shares * bars[i].Open * (1 - cost); shares = 0; trades++; if (cash > entryCost) wins++;
            }
            var equity = cash + shares * bars[i].Close;
            peak = Math.Max(peak, equity); drawdown = Math.Max(drawdown, (peak - equity) / peak);
        }
        if (shares > 0) { cash = shares * bars[^1].Close * (1 - cost); trades++; if (cash > entryCost) wins++; }
        drawdown = Math.Max(drawdown, (peak - cash) / peak);
        var benchmark = bars[^1].Close / (bars[60].Open * (1 + cost)) * (1 - cost) - 1;
        return new((cash / 10000 - 1) * 100, benchmark * 100, drawdown * 100, trades,
            trades > 0 ? wins * 100m / trades : null, bars[60].Time, bars[^1].Time, costBps);
    }
}
