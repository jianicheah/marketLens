using MarketLens.Models;
namespace MarketLens.Services;

public sealed class AnalysisService(TimeProvider clock)
{
    private static readonly string[] BaseRisks =
        ["Rules are a baseline, not a proven predictor; past results do not establish future returns.",
         "News, earnings events, taxes, liquidity, and your financial situation are not assessed."];
    public Recommendation Evaluate(MarketData data, string horizon, bool ownsStock)
    {
        var now = clock.GetUtcNow();
        var basis = data.IsSample ? "Fictional demonstration" : data.IsImported ? "User-imported data; source not independently verified" : data.Source;
        Recommendation Missing(string reason) => new("Insufficient data", reason, basis, null, [], [],
            [.. BaseRisks, .. data.Warnings ?? [], data.IsSample ? "Fictional data cannot inform a real investment." : data.IsImported ? "Imported source and timestamps are not independently verified." : "Unavailable fields are not guessed."]);
        try { DataValidation.Validate(data, now); } catch (InvalidDataException e) { return Missing(e.Message); }
        if (horizon is not ("day" or "swing" or "long")) return Missing("Choose a supported investment timeframe.");
        if (data.Quote is { IsEquity: false }) return Missing("This engine supports common company shares only.");
        if (data.Quote?.MarketState == "SUSPENDED") return Missing("The security is suspended; an actionable current signal is withheld.");
        var risks = new List<string>(BaseRisks);
        risks.AddRange(data.Warnings ?? []);
        if (data.IsSample) risks.Add("All values are fictional; this signal must not inform a real investment.");
        if (data.IsImported) risks.Add("Import timestamps and source accuracy are supplied by you; this is not a verified live feed.");
        string Describe(string signal) => signal switch
        {
            "Buy" => ownsStock ? "Rules favour adding exposure; review concentration and risks first." : "Rules identify a candidate to consider buying after your own review.",
            "Sell" => ownsStock ? "Rules favour reviewing a reduction or exit of your existing holding." : "Rules favour avoiding a new purchase. This is not a short-sale signal.",
            _ => ownsStock ? "Rules do not support a change in your current holding." : "Wait: the rules do not show a clear entry."
        };
        if (horizon == "long")
        {
            if (data.Fundamentals is not { } f) return Missing("Long-term analysis needs verified-period revenue/earnings growth, free cash flow, debt/equity, ROE, P/E and EPS. Import those fields when API mapping is unavailable.");
            if (now - f.PeriodEnd > TimeSpan.FromDays(460)) return Missing("Financial statements are older than 460 days.");
            if (f.ValuationAsOf is null || now - f.ValuationAsOf > TimeSpan.FromDays(5))
                return Missing("A valuation timestamp within five days is required for a current long-term screening result.");
            if (f.IsFinancialCompany) return Missing("Banks and insurers require sector-specific valuation models; the generic company rules are not applicable.");
            if (f.Eps > 0 && f.PeTtm <= 0) return Missing("Positive earnings require a meaningful positive P/E for these rules.");
            var checks = new[] { f.RevenueGrowthPercent > 0, f.EarningsGrowthPercent > 0, f.FreeCashFlow > 0,
                f.DebtToEquity <= 1, f.ReturnOnEquityPercent >= 12, f.PeTtm > 0 && f.PeTtm <= 25 && f.Eps > 0 };
            var score = checks.Count(x => x);
            var sell = f.Eps <= 0 && f.FreeCashFlow <= 0 || f.DebtToEquity > 2 && f.EarningsGrowthPercent < 0;
            var signal = sell ? "Sell" : score >= 5 && f.Eps > 0 && f.DebtToEquity <= 1 ? "Buy" : "Hold";
            risks.Add("Generic thresholds are not sector-normalised and do not estimate intrinsic value.");
            risks.Add("Long-term rules have no point-in-time fundamentals backtest in this version.");
            return new(signal, Describe(signal), basis, f.PeriodEnd,
                [new("Revenue growth YoY", $"{f.RevenueGrowthPercent:N2}%"), new("Earnings growth YoY", $"{f.EarningsGrowthPercent:N2}%"),
                 new("Free cash flow", $"{f.FreeCashFlow:N2}"), new("Debt / equity", $"{f.DebtToEquity:N2}"),
                 new("ROE", $"{f.ReturnOnEquityPercent:N2}%"), new("P/E TTM", $"{f.PeTtm:N2}")],
                [$"{score}/6 disclosed screening criteria passed (a checklist count, not a probability).",
                 "Criteria: positive revenue/earnings growth and FCF; debt/equity ≤1; ROE ≥12%; positive EPS and P/E ≤25.",
                 "Exit rule: nonpositive EPS and FCF, or debt/equity >2 with declining earnings."], risks.ToArray());
        }
        PriceBar[] bars;
        if (horizon == "day")
        {
            if (data.IsImported && !data.IsSample) return Missing("Day-trading signals require a verified live feed; imported files cannot establish live quote freshness.");
            if (data.Quote?.MarketState == "UNVERIFIED_PUBLIC_FEED") return Missing("This public feed has no verified live bid/ask or regular-session status. It supports daily research, not actionable day-trading signals.");
            if (data.Quote is not { } q || !q.IsOpen) return Missing("Day-trading analysis requires a confirmed open regular market session.");
            if (now - q.UpdatedAt > TimeSpan.FromMinutes(2)) return Missing("Latest quote is older than two minutes; a signal for today is withheld.");
            if (q.Bid is not > 0 || q.Ask is not > 0 || q.Ask < q.Bid || (q.Ask.Value - q.Bid.Value) / q.Price > 0.005m)
                return Missing("Bid/ask data is missing, crossed, or wider than 0.5%; intraday liquidity cannot be accepted.");
            var today = DataValidation.LocalTime(data.Symbol, now).Date;
            bars = data.Intraday.Where(b => DataValidation.LocalTime(data.Symbol, b.Time).Date == today && b.Time.AddMinutes(5) <= now).ToArray();
            if (bars.Length < 60) return Missing("At least 60 completed regular-session five-minute bars from today are required; early sessions will abstain.");
            if (now - bars[^1].Time > TimeSpan.FromMinutes(12)) return Missing("Five-minute bars are stale or their interval is unsupported.");
            if (bars.Zip(bars.Skip(1), (a, b) => b.Time - a.Time).Any(g => g != TimeSpan.FromMinutes(5)))
                return Missing("Intraday bars are not consecutive five-minute intervals; gaps/lunch breaks need a session-aware model.");
            risks.Add("Intraday rules use a conservative five-hour warm-up and can abstain during short or split sessions.");
        }
        else
        {
            bars = DataValidation.CompletedDaily(data, now);
            if (bars.Length < 60) return Missing("At least 60 completed daily bars are required; today's incomplete bar is excluded.");
            if (now - bars[^1].Time > TimeSpan.FromDays(5)) return Missing("Latest completed daily bar is older than five days. Refresh data before using a current signal.");
            if (!data.SplitAdjusted) return Missing("Confirm split-adjusted prices before comparing trends across dates.");
            if (bars[^1].Time - bars[^60].Time > TimeSpan.FromDays(180)) return Missing("Daily history is too sparse for the 20/50-session model. Import regular daily observations.");
            risks.Add("Swing signal uses the latest completed daily close, not a forecast of today's price movement.");
            if (data.Quote is null || now - data.Quote.UpdatedAt > TimeSpan.FromMinutes(2)) risks.Add("No fresh live quote is available; check today's price and market session before acting.");
        }
        var finalSignal = Indicators.TrendSignal(bars);
        if (bars[^1].Volume <= 0 || bars.Skip(bars.Length - 21).Take(20).Average(b => b.Volume) <= 0)
            return Missing("Usable current and historical volume is required for the technical screening rule.");
        var price = bars[^1].Close; var atr = Indicators.Atr(bars);
        if (atr / price > 0.08m) return Missing("ATR exceeds 8% of price; this baseline refuses an unusually volatile instrument.");
        var stop = Math.Max(price * 0.01m, price - 2 * atr); var target = price + 4 * atr;
        risks.Add("ATR references are illustrative levels, not executable orders or guaranteed exit prices.");
        return new(finalSignal, Describe(finalSignal), basis, bars[^1].Time,
            [new("Reference close", $"{data.Currency} {price:N2}"), new("SMA 20", $"{Indicators.Sma(bars, 20):N2}"),
             new("SMA 50", $"{Indicators.Sma(bars, 50):N2}"), new("RSI 14 (Wilder)", $"{Indicators.Rsi(bars):N2}"),
             new("ATR 14", $"{atr:N2}"), new("Volume / prior 20 bars", $"{Indicators.VolumeRatio(bars):N2}×")],
            ["Buy: close > SMA20 > SMA50, RSI 45–70, and latest volume ≥ prior 20-bar average.",
             "Sell: close < SMA20 < SMA50 and RSI <45. Otherwise Hold.",
             horizon == "day" ? "Indicators use completed five-minute bars from the current regular session." : "Indicators use completed daily bars; the same rule is evaluated in the historical backtest."],
            risks.ToArray(), finalSignal == "Buy" ? stop : null, finalSignal == "Buy" ? target : null);
    }
}
