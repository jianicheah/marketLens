using System.Globalization;
using System.Text.Json;
using MarketLens.Models;
using Microsoft.Extensions.Caching.Memory;
namespace MarketLens.Services;

public sealed record CompanySearchResult(string Symbol, string ProviderSymbol, string Name, string Exchange,
    string? Sector = null, string? Industry = null);

// Public Yahoo responses were checked directly. This is an unofficial personal-use adapter.
// No brokerage login, cookies, token, API key or user portfolio is sent to the provider.
public sealed class PublicMarketDataProvider(IHttpClientFactory clients, IMemoryCache cache, TimeProvider clock)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTimeOffset lastRequest;
    private static readonly Dictionary<string, string> Suffixes = new()
    {
        ["MY"] = "KL", ["HK"] = "HK", ["SG"] = "SI", ["JP"] = "T", ["SH"] = "SS", ["SZ"] = "SZ", ["BJ"] = "BJ",
        ["AU"] = "AX", ["CA"] = "TO", ["KR"] = "KS", ["UK"] = "L", ["DE"] = "DE", ["FR"] = "PA", ["IN"] = "NS",
        ["TW"] = "TW", ["ID"] = "JK", ["TH"] = "BK", ["NZ"] = "NZ", ["BR"] = "SA", ["MX"] = "MX", ["ZA"] = "JO",
        ["NL"] = "AS", ["IT"] = "MI", ["CH"] = "SW", ["ES"] = "MC"
    };
    public static string ToProviderSymbol(string symbol)
    {
        if (!DataValidation.ValidSymbol(symbol)) throw new InvalidDataException("Choose a supported company from search or enter market.symbol, such as MY.1155.");
        var split = symbol.IndexOf('.'); var market = symbol[..split]; var code = symbol[(split + 1)..];
        if (market == "US") return code.Replace('.', '-');
        if (market == "HK" && int.TryParse(code, out var number)) code = number.ToString("D4", CultureInfo.InvariantCulture);
        // Preserve secondary-listing suffixes already returned by the provider.
        if (code.Contains('.')) return code;
        return code + "." + Suffixes[market];
    }
    public static string? FromProviderSymbol(string ticker)
    {
        ticker = ticker.ToUpperInvariant();
        var split = ticker.LastIndexOf('.');
        if (split < 0) return DataValidation.ValidSymbol("US." + ticker) ? "US." + ticker : null;
        var suffix = ticker[(split + 1)..]; var code = ticker[..split];
        var market = Suffixes.FirstOrDefault(p => p.Value == suffix).Key;
        if (market is null)
        {
            market = suffix switch { "BO" => "IN", "TWO" => "TW", "KQ" => "KR", "V" => "CA", "F" or "MU" or "SG" or "BE" or "DU" or "HM" or "HA" => "DE", _ => null };
            if (market is null) return null;
            code = ticker;
        }
        if (market == "HK" && int.TryParse(code, out var number)) code = number.ToString("D5", CultureInfo.InvariantCulture);
        var result = market + "." + code;
        return DataValidation.ValidSymbol(result) ? result : null;
    }
    public async Task<CompanySearchResult[]> SearchAsync(string query, CancellationToken cancellation)
    {
        query = query.Trim();
        if (query.Length is < 1 or > 80) return [];
        var key = "public-search:" + query.ToUpperInvariant();
        if (cache.TryGetValue<CompanySearchResult[]>(key, out var cached)) return cached!;
        var data = await RequestAsync("v1/finance/search?q=" + Uri.EscapeDataString(query) + "&quotesCount=12&newsCount=0", cancellation);
        if (!data.TryGetProperty("quotes", out var quotes) || quotes.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Company search returned an unexpected format.");
        var results = quotes.EnumerateArray().Where(q => Text(q, "quoteType") == "EQUITY")
            .Select(q => (Record: q, Symbol: FromProviderSymbol(Text(q, "symbol") ?? "")))
            .Where(q => q.Symbol is not null).Select(q => new CompanySearchResult(q.Symbol!, Text(q.Record, "symbol")!,
                Text(q.Record, "longname") ?? Text(q.Record, "shortname") ?? q.Symbol!,
                Text(q.Record, "exchDisp") ?? Text(q.Record, "exchange") ?? "", Text(q.Record, "sector"), Text(q.Record, "industry"))).ToArray();
        cache.Set(key, results, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10) }); return results;
    }
    public async Task<MarketData> FetchAsync(string symbol, bool includeIntraday, CancellationToken cancellation)
    {
        var ticker = ToProviderSymbol(symbol);
        var key = "public-data:" + symbol + ":" + includeIntraday;
        if (cache.TryGetValue<MarketData>(key, out var cached)) return cached!;
        var chart = await RequestAsync($"v8/finance/chart/{Uri.EscapeDataString(ticker)}?range=2y&interval=1d&events=div%2Csplits", cancellation);
        var data = ParseChart(chart, symbol, clock.GetUtcNow());
        var warnings = new List<string>(data.Warnings ?? []);
        PriceBar[] intraday = [];
        if (includeIntraday)
        {
            try
            {
                var minuteChart = await RequestAsync($"v8/finance/chart/{Uri.EscapeDataString(ticker)}?range=5d&interval=5m&includePrePost=false", cancellation);
                intraday = ParseChart(minuteChart, symbol, clock.GetUtcNow(), true).Intraday;
            }
            catch (Exception e) when (e is InvalidDataException or HttpRequestException or JsonException or TaskCanceledException && !cancellation.IsCancellationRequested)
            { warnings.Add("Intraday bars are unavailable from the public provider."); }
        }
        Fundamentals? fundamentals = null;
        try
        {
            var classification = (await SearchAsync(ticker, cancellation)).FirstOrDefault(c => c.ProviderSymbol.Equals(ticker, StringComparison.OrdinalIgnoreCase));
            if (classification?.Sector is not null && data.Quote is { } quote)
            {
                var start = clock.GetUtcNow().AddYears(-4).ToUnixTimeSeconds(); var end = clock.GetUtcNow().ToUnixTimeSeconds();
                var fields = "annualTotalRevenue,annualNetIncome,annualFreeCashFlow,annualTotalDebt,annualStockholdersEquity,trailingDilutedEPS";
                var reports = await RequestAsync($"ws/fundamentals-timeseries/v1/finance/timeseries/{Uri.EscapeDataString(ticker)}?type={fields}&period1={start}&period2={end}", cancellation);
                fundamentals = ParseFundamentals(reports, quote, data.Currency, classification.Sector == "Financial Services", clock.GetUtcNow());
            }
        }
        catch (Exception e) when (e is InvalidDataException or HttpRequestException or JsonException or TaskCanceledException && !cancellation.IsCancellationRequested)
        { warnings.Add("Company financial data is unavailable; the financial model will abstain."); }
        if (fundamentals is null) warnings.Add("No complete matching annual statements, current trailing EPS, or verified sector classification is available. Long-term analysis will abstain.");
        data = data with { Intraday = intraday, Fundamentals = fundamentals, Warnings = warnings.ToArray() };
        DataValidation.Validate(data, clock.GetUtcNow());
        cache.Set(key, data, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) }); return data;
    }
    private async Task<JsonElement> RequestAsync(string path, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation);
        try
        {
            var delay = TimeSpan.FromMilliseconds(600) - (clock.GetUtcNow() - lastRequest);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellation);
            lastRequest = clock.GetUtcNow();
            using var response = await clients.CreateClient("PublicMarketData").GetAsync(path, cancellation);
            if ((int)response.StatusCode == 429) throw new InvalidDataException("The public data provider is rate-limiting requests. Wait before trying again; saved data is unchanged.");
            if (!response.IsSuccessStatusCode) throw new InvalidDataException($"Public market data is unavailable (HTTP {(int)response.StatusCode}). No login is needed; this source may be unavailable or this listing unsupported.");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation)); return document.RootElement.Clone();
        }
        finally { gate.Release(); }
    }
    private static string? Text(JsonElement e, string field) => e.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static decimal? Number(JsonElement e, string field) => e.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDecimal() : null;
    public static MarketData ParseChart(JsonElement root, string symbol, DateTimeOffset now, bool intraday = false)
    {
        if (!root.TryGetProperty("chart", out var chart) || !chart.TryGetProperty("result", out var results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
            throw new InvalidDataException("No price history exists for this listing, or the provider could not serve it.");
        var result = results[0]; var meta = result.GetProperty("meta");
        if (!string.Equals(Text(meta, "symbol"), ToProviderSymbol(symbol), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Returned symbol does not match the requested listing.");
        if (Text(meta, "instrumentType") != "EQUITY") throw new InvalidDataException("Choose a listed company share. ETFs, indices, funds and derivatives are outside this model.");
        var currency = Text(meta, "currency") ?? throw new InvalidDataException("Quote currency is missing.");
        var times = result.GetProperty("timestamp"); var indicators = result.GetProperty("indicators"); var prices = indicators.GetProperty("quote")[0];
        JsonElement? adjusted = !intraday && indicators.TryGetProperty("adjclose", out var adj) && adj.GetArrayLength() > 0 ? adj[0].GetProperty("adjclose") : null;
        decimal? At(JsonElement values, string name, int i) => values.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array && a.GetArrayLength() > i && a[i].ValueKind == JsonValueKind.Number ? a[i].GetDecimal() : null;
        var bars = new List<PriceBar>(); var adjustmentComplete = !intraday && adjusted is not null; var skipped = 0;
        for (var i = 0; i < times.GetArrayLength(); i++)
        {
            var open = At(prices, "open", i); var high = At(prices, "high", i); var low = At(prices, "low", i); var close = At(prices, "close", i); var volume = At(prices, "volume", i);
            if (open is not > 0 || high is not > 0 || low is not > 0 || close is not > 0 || volume is null) { skipped++; continue; }
            decimal factor = 1;
            if (!intraday)
            {
                if (adjusted is { } a && a.GetArrayLength() > i && a[i].ValueKind == JsonValueKind.Number && a[i].GetDecimal() > 0) factor = a[i].GetDecimal() / close.Value;
                else adjustmentComplete = false;
            }
            var bar = new PriceBar(DateTimeOffset.FromUnixTimeSeconds(times[i].GetInt64()), open.Value * factor,
                high.Value * factor, low.Value * factor, close.Value * factor, volume.Value);
            bars.Add(bar);
        }
        if (bars.Count == 0) throw new InvalidDataException("The provider returned no usable price observations.");
        var warnings = new List<string> { "Yahoo Finance data may be delayed or incomplete; exchange coverage and availability vary. See Data sources for connection details.",
            "Bid/ask data and confirmed live session status are unavailable; this feed cannot support actionable day-trading recommendations." };
        if (!intraday) warnings.Add("Historical OHLC uses the provider's adjusted-close factors (which may include dividends); volumes remain provider-reported. The backtest does not separately credit cash dividends.");
        if (skipped > 0) warnings.Add($"Skipped {skipped} incomplete price observations; gaps are not filled with invented prices.");
        var quotePrice = Number(meta, "regularMarketPrice");
        MarketQuote? quote = quotePrice is > 0 && meta.TryGetProperty("regularMarketTime", out var quoteTime) ? new(quotePrice.Value, null, null,
            DateTimeOffset.FromUnixTimeSeconds(quoteTime.GetInt64()), "UNVERIFIED_PUBLIC_FEED") : null;
        return new(symbol, Text(meta, "longName") ?? Text(meta, "shortName") ?? ToProviderSymbol(symbol), currency,
            "Market data via Yahoo Finance", now, intraday ? [] : bars.OrderBy(b => b.Time).ToArray(),
            intraday ? bars.OrderBy(b => b.Time).ToArray() : [], quote, SplitAdjusted: adjustmentComplete, Warnings: warnings.ToArray());
    }
    public static Fundamentals? ParseFundamentals(JsonElement root, MarketQuote quote, string quoteCurrency, bool financialCompany, DateTimeOffset now)
    {
        if (!root.TryGetProperty("timeseries", out var series) || !series.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array) return null;
        JsonElement[] Values(string field) => result.EnumerateArray().Where(g => g.TryGetProperty(field, out _))
            .SelectMany(g => g.GetProperty(field).EnumerateArray()).Where(v => Text(v, "asOfDate") is not null && v.TryGetProperty("reportedValue", out var r) && Number(r, "raw") is not null)
            .OrderBy(v => Text(v, "asOfDate"), StringComparer.Ordinal).ToArray();
        var revenue = Values("annualTotalRevenue"); var income = Values("annualNetIncome");
        var fcf = Values("annualFreeCashFlow"); var debt = Values("annualTotalDebt"); var equity = Values("annualStockholdersEquity"); var eps = Values("trailingDilutedEPS");
        if (revenue.Length < 2 || income.Length < 2 || fcf.Length == 0 || debt.Length == 0 || equity.Length < 2 || eps.Length == 0) return null;
        var date = Text(revenue[^1], "asOfDate"); var previousDate = Text(revenue[^2], "asOfDate");
        JsonElement? Match(JsonElement[] rows, string? period) => rows.Where(r => Text(r, "asOfDate") == period).Select(r => (JsonElement?)r).FirstOrDefault();
        var net = Match(income, date); var priorNet = Match(income, previousDate); var cash = Match(fcf, date); var totalDebt = Match(debt, date);
        var sharesEquity = Match(equity, date); var priorEquity = Match(equity, previousDate);
        if (net is null || priorNet is null || cash is null || totalDebt is null || sharesEquity is null || priorEquity is null) return null;
        decimal Value(JsonElement e) => e.GetProperty("reportedValue").GetProperty("raw").GetDecimal();
        var records = new[] { revenue[^1], revenue[^2], net.Value, priorNet.Value, cash.Value, totalDebt.Value, sharesEquity.Value, priorEquity.Value, eps[^1] };
        var statementCurrency = Text(revenue[^1], "currencyCode");
        if (records.Any(r => Text(r, "currencyCode") != statementCurrency)) return null;
        decimal currencyFactor = quoteCurrency == statementCurrency ? 1 : quoteCurrency == "GBp" && statementCurrency == "GBP" || quoteCurrency == "ZAc" && statementCurrency == "ZAR" ? 100 : 0;
        if (currencyFactor == 0 || Value(revenue[^2]) <= 0 || Value(priorNet.Value) <= 0 || Value(sharesEquity.Value) <= 0 || Value(priorEquity.Value) <= 0 || Value(totalDebt.Value) < 0) return null;
        if (!DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var period) ||
            !DateTimeOffset.TryParse(Text(eps[^1], "asOfDate"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var epsPeriod) || now - epsPeriod > TimeSpan.FromDays(200) || epsPeriod > now) return null;
        var trailingEps = Value(eps[^1]) * currencyFactor;
        return new(period, (Value(revenue[^1]) / Value(revenue[^2]) - 1) * 100, (Value(net.Value) / Value(priorNet.Value) - 1) * 100,
            Value(cash.Value), Value(totalDebt.Value) / Value(sharesEquity.Value), Value(net.Value) / ((Value(sharesEquity.Value) + Value(priorEquity.Value)) / 2) * 100,
            trailingEps > 0 ? quote.Price / trailingEps : 0, trailingEps, financialCompany, quote.UpdatedAt);
    }
}
