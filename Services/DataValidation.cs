using System.Text.RegularExpressions;
using MarketLens.Models;
namespace MarketLens.Services;

public static partial class DataValidation
{
    [GeneratedRegex(@"^(US|HK|MY|SG|JP|SH|SZ|BJ|AU|CA|KR|UK|DE|FR|IN|TW|ID|TH|NZ|BR|MX|ZA|NL|IT|CH|ES)\.[A-Z0-9][A-Z0-9.\-]{0,24}$")]
    private static partial Regex SymbolPattern();
    public static bool ValidSymbol(string? value) => value is not null && SymbolPattern().IsMatch(value);
    public static DateTimeOffset LocalTime(string symbol, DateTimeOffset time)
    {
        var zone = symbol.Split('.')[0] switch
        {
            "US" or "CA" => "Eastern Standard Time", "JP" or "KR" => "Tokyo Standard Time",
            "AU" => "AUS Eastern Standard Time", "UK" => "GMT Standard Time", "DE" or "FR" or "NL" or "IT" or "CH" or "ES" => "W. Europe Standard Time",
            "IN" => "India Standard Time", "ID" or "TH" => "SE Asia Standard Time", "NZ" => "New Zealand Standard Time",
            "BR" => "E. South America Standard Time", "MX" => "Central Standard Time (Mexico)", "ZA" => "South Africa Standard Time", _ => "Singapore Standard Time"
        };
        return TimeZoneInfo.ConvertTime(time, TimeZoneInfo.FindSystemTimeZoneById(zone));
    }
    public static string Currency(string symbol) => symbol.Split('.')[0] switch
    {
        "US" => "USD", "HK" => "HKD", "MY" => "MYR", "SG" => "SGD", "JP" => "JPY",
        "CA" => "CAD", "AU" => "AUD", "KR" => "KRW", "UK" => "GBp", "DE" or "FR" or "NL" or "IT" or "ES" => "EUR",
        "CH" => "CHF", "IN" => "INR", "TW" => "TWD", "ID" => "IDR", "TH" => "THB", "NZ" => "NZD", "BR" => "BRL", "MX" => "MXN", "ZA" => "ZAc", _ => "CNY"
    };
    public static void Validate(MarketData data, DateTimeOffset now)
    {
        if (!ValidSymbol(data.Symbol) || string.IsNullOrWhiteSpace(data.Name) || data.Name.Length > 150)
            throw new InvalidDataException("A valid market.symbol and company name are required.");
        if (data.Currency is not ("USD" or "HKD" or "MYR" or "SGD" or "JPY" or "CNY" or "CAD" or "AUD" or "KRW" or "GBP" or "GBp" or "EUR" or "CHF" or "INR" or "TWD" or "IDR" or "THB" or "NZD" or "BRL" or "MXN" or "ZAR" or "ZAc"))
            throw new InvalidDataException("The quote currency/unit is missing or unsupported.");
        if (data.Daily is null || data.Intraday is null || data.Daily.Length > 5000 || data.Intraday.Length > 5000)
            throw new InvalidDataException("Price arrays are required and each is limited to 5,000 bars.");
        foreach (var bars in new[] { data.Daily, data.Intraday })
        {
            DateTimeOffset? previous = null;
            foreach (var bar in bars)
            {
                if (bar is null || bar.Time == default || bar.Time > now.AddMinutes(1) || bar.Open <= 0 || bar.Close <= 0 || bar.Low <= 0 ||
                    bar.High < Math.Max(bar.Open, bar.Close) || bar.Low > Math.Min(bar.Open, bar.Close) ||
                    bar.High < bar.Low || bar.High > 1000000000m || bar.Volume is < 0 or > 1000000000000000m || (previous is not null && bar.Time <= previous))
                    throw new InvalidDataException("Bars must be chronological, unique, non-future, and have valid OHLC/volume values.");
                previous = bar.Time;
            }
        }
        if (data.Daily.GroupBy(b => DataValidation.LocalTime(data.Symbol, b.Time).Date).Any(g => g.Count() > 1))
            throw new InvalidDataException("Daily data contains duplicate market dates.");
        if (data.Quote is { } q && (q.Price is <= 0 or > 1000000000m || q.UpdatedAt == default || q.UpdatedAt > now.AddMinutes(1)))
            throw new InvalidDataException("Quote price/timestamp is invalid.");
        if (data.Fundamentals is { } f && (f.PeriodEnd == default || f.PeriodEnd > now || f.DebtToEquity < 0 || f.ValuationAsOf > now.AddMinutes(1)))
            throw new InvalidDataException("Fundamental period or debt/equity value is invalid.");
    }
    public static PriceBar[] CompletedDaily(MarketData data, DateTimeOffset now) => data.Daily
        .Where(b => LocalTime(data.Symbol, b.Time).Date < LocalTime(data.Symbol, now).Date).ToArray();
}
