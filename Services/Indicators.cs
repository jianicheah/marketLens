using MarketLens.Models;
namespace MarketLens.Services;

public static class Indicators
{
    public static decimal Sma(IReadOnlyList<PriceBar> bars, int period) => bars.TakeLast(period).Average(b => b.Close);
    public static decimal Rsi(IReadOnlyList<PriceBar> bars, int period = 14)
    {
        decimal gain = 0, loss = 0;
        for (var i = 1; i <= period; i++)
        {
            var change = bars[i].Close - bars[i - 1].Close;
            gain += Math.Max(0, change); loss += Math.Max(0, -change);
        }
        gain /= period; loss /= period;
        for (var i = period + 1; i < bars.Count; i++)
        {
            var change = bars[i].Close - bars[i - 1].Close;
            gain = (gain * (period - 1) + Math.Max(0, change)) / period;
            loss = (loss * (period - 1) + Math.Max(0, -change)) / period;
        }
        return loss == 0 ? (gain == 0 ? 50 : 100) : 100 - 100 / (1 + gain / loss);
    }
    public static decimal Atr(IReadOnlyList<PriceBar> bars, int period = 14) =>
        Enumerable.Range(bars.Count - period, period).Average(i => Math.Max(bars[i].High - bars[i].Low,
            Math.Max(Math.Abs(bars[i].High - bars[i - 1].Close), Math.Abs(bars[i].Low - bars[i - 1].Close))));
    public static decimal VolumeRatio(IReadOnlyList<PriceBar> bars)
    {
        var average = bars.Skip(bars.Count - 21).Take(20).Average(b => b.Volume);
        return average > 0 ? bars[^1].Volume / average : 0;
    }
    // Deliberately explicit baseline strategy. Backtesting uses this exact same rule.
    public static string TrendSignal(IReadOnlyList<PriceBar> bars)
    {
        var price = bars[^1].Close; var fast = Sma(bars, 20); var slow = Sma(bars, 50);
        var rsi = Rsi(bars); var volume = VolumeRatio(bars);
        if (price > fast && fast > slow && rsi is >= 45 and <= 70 && volume >= 1) return "Buy";
        if (price < fast && fast < slow && rsi < 45) return "Sell";
        return "Hold";
    }
}
