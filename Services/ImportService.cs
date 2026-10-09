using System.Globalization;
using System.Text.Json;
using MarketLens.Models;
namespace MarketLens.Services;

public sealed class ImportService(TimeProvider clock)
{
    public MarketData Parse(string text, string extension, string symbol, string name, bool splitAdjusted)
    {
        MarketData data;
        if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            data = JsonSerializer.Deserialize<MarketData>(text, DatasetStore.JsonOptions)
                ?? throw new InvalidDataException("JSON dataset is empty.");
            data = data with { IsImported = true, Source = "User-imported JSON", RetrievedAt = clock.GetUtcNow(),
                // An explicit sample flag stays a sample even when imported.
                Warnings = ["Imported financial/price fields require verification against the original source."] };
        }
        else if (extension.Equals(".csv", StringComparison.OrdinalIgnoreCase))
        {
            var lines = text.TrimStart('\uFEFF').Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 2 || !lines[0].Equals("date,open,high,low,close,volume", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("CSV header must be date,open,high,low,close,volume (simple unquoted comma-separated values).");
            var bars = lines.Skip(1).Select(line =>
            {
                var values = line.Split(',', StringSplitOptions.TrimEntries);
                if (values.Length != 6 || !DateOnly.TryParseExact(values[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    throw new InvalidDataException("Each CSV row needs a YYYY-MM-DD date and five numeric values.");
                var marketTime = DataValidation.LocalTime(symbol, new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
                var timestamp = new DateTimeOffset(date.ToDateTime(new TimeOnly(12, 0)), marketTime.Offset);
                decimal Number(int i) => decimal.Parse(values[i], NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                return new PriceBar(timestamp, Number(1), Number(2), Number(3), Number(4), Number(5));
            }).ToArray();
            data = new(symbol, name, DataValidation.Currency(symbol), "User-imported CSV", clock.GetUtcNow(), bars, [],
                IsImported: true, SplitAdjusted: splitAdjusted);
        }
        else throw new InvalidDataException("Upload a .csv or .json file.");
        DataValidation.Validate(data, clock.GetUtcNow()); return data;
    }
}
