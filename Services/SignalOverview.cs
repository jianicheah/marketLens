using MarketLens.Models;
namespace MarketLens.Services;

public sealed record StockSignal(string Symbol, string Name, string Signal, decimal? TrendPercent, DateTimeOffset? AsOf, string Summary);
public static class SignalOverview
{
    public static StockSignal[] Build(IEnumerable<MarketData> datasets, AnalysisService analysis, TimeProvider clock, bool ownsStock) =>
        datasets.Where(x => !x.IsSample && !x.IsImported).DistinctBy(x => x.Symbol).Select(data =>
        {
            var result = analysis.Evaluate(data, "swing", ownsStock);
            var bars = DataValidation.CompletedDaily(data, clock.GetUtcNow());
            decimal? trend = bars.Length >= 50 && bars.TakeLast(50).Average(x => x.Close) is > 0
                ? (bars[^1].Close / bars.TakeLast(50).Average(x => x.Close) - 1) * 100 : null;
            return new StockSignal(data.Symbol, data.Name, result.Signal, trend, result.AsOf, result.Summary);
        }).OrderBy(x => x.Signal == "Buy" ? 0 : x.Signal == "Sell" ? 1 : x.Signal == "Hold" ? 2 : 3)
          .ThenBy(x => x.Name).ToArray();
}
