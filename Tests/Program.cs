using Microsoft.Extensions.Configuration;
using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using MarketLens.Models;
using MarketLens.Services;
using Microsoft.Extensions.Caching.Memory;



var now = new DateTimeOffset(2026, 10, 9, 18, 0, 0, TimeSpan.Zero);
var clock = new FixedClock(now);
var engine = new AnalysisService(clock);
var backtest = new BacktestService();
var passed = 0;
void Check(string name, Action test)
{
    test(); passed++; Console.WriteLine($"PASS {name}");
}
void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Expected validation rejection."); }
PriceBar[] Bars(int count, Func<int, decimal> price, DateTimeOffset? end = null)
{
    var last = end ?? now.AddDays(-1);
    return Enumerable.Range(0, count).Select(i =>
    {
        var close = price(i);
        return new PriceBar(last.AddDays(i - count + 1), close, close + 0.5m, close - 0.5m, close, 1000);
    }).ToArray();
}
MarketData Dataset(PriceBar[] bars) => new("US.TEST", "Test", "USD", "Fixture", now, bars, [], SplitAdjusted: true);
var flat = Dataset(Bars(120, _ => 100));
Check("Flat market yields Hold and neutral RSI", () =>
{
    Assert(Indicators.Rsi(flat.Daily) == 50, "Flat RSI must be 50.");
    Assert(engine.Evaluate(flat, "swing", false).Signal == "Hold", "Flat series must not trigger a trade.");
});
Check("Falling series yields Sell with avoid-entry meaning for non-owner", () =>
{
    var result = engine.Evaluate(Dataset(Bars(100, i => 200 - i)), "swing", false);
    Assert(result.Signal == "Sell" && result.Summary.Contains("avoiding"), "Sell semantics incorrect.");
});
Check("Overbought monotonic rise abstains from Buy", () =>
    Assert(engine.Evaluate(Dataset(Bars(100, i => 100 + i)), "swing", false).Signal == "Hold", "RSI filter must reject overbought entry."));
Check("Balanced rising pattern can produce Buy", () =>
{
    var bars = Bars(120, i => 100m + i * 0.15m + (i % 2 == 0 ? 0.8m : -0.8m));
    Assert(engine.Evaluate(Dataset(bars), "swing", false).Signal == "Buy", "Qualified uptrend must enter.");
});
Check("Stale and short histories cannot generate actionable signals", () =>
{
    Assert(engine.Evaluate(flat with { Daily = Bars(120, _ => 100, now.AddDays(-10)) }, "swing", false).Signal == "Insufficient data", "Stale data allowed.");
    Assert(engine.Evaluate(flat with { Daily = Bars(20, _ => 100) }, "swing", false).Signal == "Insufficient data", "Short history allowed.");
});
Check("Today's unfinished daily candle is excluded", () =>
{
    var unfinished = new PriceBar(now.AddHours(-1), 1, 2, 0.5m, 1, 1000);
    var result = engine.Evaluate(flat with { Daily = [.. flat.Daily, unfinished] }, "swing", false);
    Assert(result.Signal == "Hold" && result.AsOf == flat.Daily[^1].Time, "Incomplete bar leaked into result.");
});
Check("Unadjusted prices abstain", () =>
    Assert(engine.Evaluate(flat with { SplitAdjusted = false }, "swing", false).Signal == "Insufficient data", "Unadjusted series used."));
Check("Bad OHLC and duplicate times are rejected", () =>
{
    Reject(() => DataValidation.Validate(flat with { Daily = [flat.Daily[0] with { Low = 200 }] }, now));
    Reject(() => DataValidation.Validate(flat with { Daily = [flat.Daily[0], flat.Daily[0]] }, now));
});
Check("Future timestamps and path traversal symbols are rejected", () =>
{
    Reject(() => DataValidation.Validate(flat with { Daily = [flat.Daily[0] with { Time = now.AddDays(1) }] }, now));
    Assert(!DataValidation.ValidSymbol("../../tokens") && !DataValidation.ValidSymbol("US.AAPL/.."), "Unsafe symbol accepted.");
});
Check("Intraday closed, imported, stale, and wide-spread feeds abstain", () =>
{
    var intraday = Enumerable.Range(0, 65).Select(i => new PriceBar(now.AddMinutes(-5 * (65 - i)), 100, 101, 99, 100, 1000)).ToArray();
    var data = flat with { Intraday = intraday, Quote = new(100, 99.9m, 100.1m, now, "AFTERNOON") };
    Assert(engine.Evaluate(data, "day", false).Signal == "Hold", "Valid intraday flat feed rejected.");
    foreach (var bad in new[] { data with { IsImported = true }, data with { Quote = data.Quote! with { MarketState = "CLOSED" } },
        data with { Quote = data.Quote! with { UpdatedAt = now.AddMinutes(-3) } }, data with { Quote = data.Quote! with { Ask = 102 } } })
        Assert(engine.Evaluate(bad, "day", false).Signal == "Insufficient data", "Invalid intraday feed generated a signal.");
});
Check("Financial rules require current valuation and exclude bank model", () =>
{
    var fundamentals = new Fundamentals(now.AddMonths(-4), 10, 12, 1000, 0.5m, 15, 20, 2, ValuationAsOf: now);
    var data = flat with { Fundamentals = fundamentals };
    Assert(engine.Evaluate(data, "long", false).Signal == "Buy", "Good screening candidate not recognised.");
    Assert(engine.Evaluate(data with { Fundamentals = fundamentals with { IsFinancialCompany = true } }, "long", false).Signal == "Insufficient data", "Bank evaluated with generic rules.");
    Assert(engine.Evaluate(data with { Fundamentals = fundamentals with { ValuationAsOf = now.AddDays(-10) } }, "long", false).Signal == "Insufficient data", "Stale valuation accepted.");
});
Check("Financial deterioration yields Sell", () =>
{
    var f = new Fundamentals(now.AddMonths(-2), -10, -20, -1000, 3, -5, -1, -2, ValuationAsOf: now);
    Assert(engine.Evaluate(flat with { Fundamentals = f }, "long", true).Signal == "Sell", "Deteriorating fundamentals missed.");
});
Check("No trades means cash return zero and win rate N/A", () =>
{
    var result = backtest.Run(flat.Daily)!;
    Assert(result.ReturnPercent == 0 && result.ClosedTrades == 0 && result.WinRatePercent is null, "No-trade accounting incorrect.");
});
Check("Benchmark includes both sides of transaction costs", () =>
{
    var result = backtest.Run(flat.Daily, 10)!;
    var expected = ((1m / 1.001m) * 0.999m - 1) * 100;
    Assert(Math.Abs(result.BuyHoldPercent - expected) < 0.0000001m, "Benchmark costs incorrect.");
});
Check("Higher trading costs reduce strategy return", () =>
{
    var bars = Bars(150, i => 100 + i * 0.15m + (i % 2 == 0 ? 0.8m : -0.8m));
    var free = backtest.Run(bars, 0)!; var paid = backtest.Run(bars, 20)!;
    Assert(free.ClosedTrades > 0 && paid.ReturnPercent < free.ReturnPercent, "Strategy ignored costs.");
});
Check("Entries use the next opening price rather than the signal close", () =>
{
    var bars = Bars(120, i => 100 + i * 0.15m + (i % 2 == 0 ? 0.8m : -0.8m));
    Assert(Indicators.TrendSignal(bars.Take(60).ToArray()) == "Buy", "Fixture does not trigger the first eligible entry.");
    bars[60] = bars[60] with { Open = bars[60].Close + 5, High = bars[60].Close + 6 };
    var result = backtest.Run(bars, 0)!;
    var expected = (bars[^1].Close / bars[60].Open - 1) * 100;
    Assert(result.ClosedTrades == 1 && Math.Abs(result.ReturnPercent - expected) < 0.0000001m, "Execution used an incorrect bar or close price.");
});
Check("Recommendation for a prefix does not depend on future bars", () =>
{
    var bars = Bars(150, i => 100 + i * 0.15m + (i % 2 == 0 ? 0.8m : -0.8m));
    var before = Indicators.TrendSignal(bars.Take(90).ToArray());
    var modified = bars.Select((b, i) => i < 90 ? b : b with { Close = 1 }).ToArray();
    Assert(before == Indicators.TrendSignal(modified.Take(90).ToArray()), "Future data leaked into signal.");
});
Check("CSV import validates ordering, marks imported data, and preserves adjustment choice", () =>
{
    var parser = new ImportService(clock);
    var data = parser.Parse("date,open,high,low,close,volume\n2026-10-07,100,102,99,101,1000\n2026-10-08,101,103,100,102,1200", ".csv", "US.TEST", "Test", true);
    Assert(data.IsImported && data.SplitAdjusted && data.Daily.Length == 2, "Import metadata lost.");
    Reject(() => parser.Parse("date,open,high,low,close,volume\n2026-10-08,100,102,99,101,1000\n2026-10-07,100,102,99,101,1000", ".csv", "US.TEST", "Test", true));
});

string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
Check("Public tickers round-trip Malaysia, HK and global listings", () =>
{
    Assert(PublicMarketDataProvider.ToProviderSymbol("MY.1155") == "1155.KL", "Malaysia mapping incorrect.");
    Assert(PublicMarketDataProvider.ToProviderSymbol("HK.00700") == "0700.HK", "HK zero padding incorrect.");
    foreach (var ticker in new[] { "AAPL", "1155.KL", "0700.HK", "D05.SI", "7974.T", "600519.SS", "TSCO.L", "SAP.DE", "BNII.JK", "RELIANCE.NS", "BOZA.F" })
    {
        var mapped = PublicMarketDataProvider.FromProviderSymbol(ticker);
        Assert(mapped is not null && PublicMarketDataProvider.ToProviderSymbol(mapped) == ticker, "Listing mapping did not round-trip: " + ticker);
    }
    Assert(PublicMarketDataProvider.FromProviderSymbol("^GSPC") is null, "Unsupported index treated as a stock.");
});
Check("Real captured price responses validate US, Malaysia and HK", () =>
{
    foreach (var item in new[] { ("yahoo-aapl.json", "US.AAPL"), ("yahoo-maybank.json", "MY.1155"), ("yahoo-tencent.json", "HK.00700") })
    {
        using var document = JsonDocument.Parse(Fixture(item.Item1));
        var data = PublicMarketDataProvider.ParseChart(document.RootElement, item.Item2, now);
        DataValidation.Validate(data, now);
        Assert(data.Daily.Length >= 200 && data.SplitAdjusted && !data.IsSample, "Real price history metadata lost.");
        Assert(engine.Evaluate(data, "swing", false).Basis == data.Source, "Data source label is incorrect.");
    }
});
Check("Public feed refuses actionable day-trading signals", () =>
{
    using var document = JsonDocument.Parse(Fixture("yahoo-maybank.json"));
    var data = PublicMarketDataProvider.ParseChart(document.RootElement, "MY.1155", now);
    var result = engine.Evaluate(data, "day", false);
    Assert(result.Signal == "Insufficient data" && result.Summary.Contains("public feed"), "Unverified public quotes used for intraday trading.");
});
Check("Annual financial statements yield matched ratios and current P/E", () =>
{
    using var document = JsonDocument.Parse(Fixture("yahoo-fundamentals.json"));
    var quote = new MarketQuote(340.42m, null, null, now.AddDays(-1), "UNVERIFIED_PUBLIC_FEED");
    var fundamentals = PublicMarketDataProvider.ParseFundamentals(document.RootElement, quote, "USD", false, now);
    Assert(fundamentals is not null, "Complete captured financial statements were not mapped.");
    Assert(Math.Abs(fundamentals!.PeTtm - 340.42m / 8.72m) < 0.00001m, "P/E denominator incorrect.");
    Assert(fundamentals.FreeCashFlow == 98767000000m && fundamentals.DebtToEquity > 1, "Financial figures incorrect.");
    Assert(PublicMarketDataProvider.ParseFundamentals(document.RootElement, quote, "EUR", false, now) is null, "Currency mismatch accepted.");
});
Check("Missing fundamental fields produce no invented result", () =>
{
    using var document = JsonDocument.Parse(Fixture("yahoo-fundamentals.json").Replace("annualFreeCashFlow", "missingCashFlow"));
    Assert(PublicMarketDataProvider.ParseFundamentals(document.RootElement, new(100, null, null, now, "UNVERIFIED_PUBLIC_FEED"), "USD", false, now) is null, "Missing FCF was fabricated.");
});
Check("Empty/error chart response is handled without a demo fallback", () =>
{
    using var document = JsonDocument.Parse("{\"chart\":{\"result\":null,\"error\":{\"code\":\"Not Found\"}}}");
    Reject(() => PublicMarketDataProvider.ParseChart(document.RootElement, "US.AAPL", now));
});
var handler = new FakePublicHandler(Fixture);
var factory = new PublicClients(handler);
var provider = new PublicMarketDataProvider(factory, new MemoryCache(new MemoryCacheOptions()), clock);
var fetched = await provider.FetchAsync("US.AAPL", false, CancellationToken.None);
var repeated = await provider.FetchAsync("US.AAPL", false, CancellationToken.None);
Check("Direct public fetch requires no credentials and caches repeated requests", () =>
{
    Assert(fetched.Fundamentals is not null && fetched.Source.Contains("Yahoo"), "Direct data fetch incomplete.");
    Assert(handler.Requests == 3 && !handler.SentCredentials && ReferenceEquals(fetched, repeated), "Cache/auth behaviour incorrect.");
});
Check("Local hosting accepts loopback only", () =>
{
    var settings = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
    var hosting = new RenderHosting(settings);
    Assert(hosting.Allows("localhost", IPAddress.Loopback), "Local request was blocked.");
    Assert(!hosting.Allows("localhost", IPAddress.Parse("192.168.1.20")), "LAN request escaped the local guard.");
    Assert(!hosting.Allows("evil.example", IPAddress.Loopback), "Unexpected Host was allowed.");
});
Check("Public hosting accepts only the configured Render hostname", () =>
{
    var settings = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        { ["Hosting:PublicMode"] = "true", ["RENDER_EXTERNAL_HOSTNAME"] = "marketlens.onrender.com" }).Build();
    var hosting = new RenderHosting(settings);
    Assert(hosting.Allows("marketlens.onrender.com", IPAddress.Parse("10.0.0.2")), "Render proxy was blocked.");
    Assert(!hosting.Allows("other.onrender.com", IPAddress.Loopback), "Wrong public host was allowed.");
});
Check("Public dataset cache writes no files and rejects visitor imports", () =>
{
    var settings = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        { ["Hosting:PublicMode"] = "true", ["RENDER_EXTERNAL_HOSTNAME"] = "marketlens.onrender.com" }).Build();
    var folder = Path.Combine(Path.GetTempPath(), "MarketLens-check-" + Guid.NewGuid());
    var env = new CheckEnvironment { ContentRootPath = folder };
    var store = new DatasetStore(env, clock, new RenderHosting(settings));
    var data = Dataset(Bars(100, _ => 10));
    store.Save(data);
    Assert(store.Read(data.Symbol)?.Symbol == data.Symbol, "Public cache lost the dataset.");
    Assert(store.Symbols().Length == 0 && !Directory.Exists(folder), "Shared data exposed defaults or wrote private files.");
    Reject(() => store.Save(data with { IsImported = true }));
    var restarted = new DatasetStore(env, clock, new RenderHosting(settings));
    Assert(restarted.Read(data.Symbol) is null, "Restart unexpectedly depended on persistent data.");
});

if (args.Contains("--live"))
{
    var live = new PublicMarketDataProvider(new LivePublicClients(), new MemoryCache(new MemoryCacheOptions()), TimeProvider.System);
    foreach (var symbol in new[] { "US.AAPL", "MY.1155", "HK.00700" })
    {
        var data = await live.FetchAsync(symbol, false, CancellationToken.None);
        Assert(data.Daily.Length >= 60, "Live history is too short.");
        var result = new AnalysisService(TimeProvider.System).Evaluate(data, "swing", false);
        Console.WriteLine($"LIVE {symbol}: {data.Daily.Length} bars; {result.Signal}; quote {data.Quote?.Price}; fundamentals {(data.Fundamentals is null ? "unavailable" : "available")}");
    }
    var companies = await live.SearchAsync("Maybank", CancellationToken.None);
    Assert(companies.Any(c => c.Symbol == "MY.1155"), "Live company search did not find Maybank.");
    Console.WriteLine("Live public fetch and search passed without login.");
}
Console.WriteLine($"All {passed} checks passed.");
sealed class FixedClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
sealed class PublicClients(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("https://query1.finance.yahoo.com/") };
}
sealed class FakePublicHandler(Func<string, string> fixture) : HttpMessageHandler
{
    public int Requests { get; private set; }
    public bool SentCredentials { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++; SentCredentials |= request.Headers.Authorization is not null || request.Headers.Contains("Cookie");
        var path = request.RequestUri!.AbsolutePath;
        var file = path.Contains("chart") ? "yahoo-aapl.json" : path.Contains("search") ? "yahoo-search-aapl.json" : "yahoo-fundamentals.json";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(fixture(file)) });
    }
}
sealed class LivePublicClients : IHttpClientFactory
{
    public HttpClient CreateClient(string name)
    {
        var client = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false }) { BaseAddress = new Uri("https://query1.finance.yahoo.com/"), Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MarketLens/1.0 personal-research"); return client;
    }
}

sealed class CheckEnvironment : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "MarketLens";
    public string EnvironmentName { get; set; } = "Testing";
    public string ContentRootPath { get; set; } = "";
    public string WebRootPath { get; set; } = "";
    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
}
