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
IConfigurationBuilder TestSettings() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["MarketData:CompanyCatalogPath"] = Path.Combine(Path.GetTempPath(), "MarketLens-empty-test-catalogue.json") });
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

Check("Popular suggestions work without a key and missing owner key sends no requests", () =>
{
    var handler = new FakeMarketstackHandler();
    var provider = new MarketstackDataProvider(new PublicClients(handler), new MemoryCache(new MemoryCacheOptions()), clock,
        TestSettings().Build());
    Assert(provider.SearchAsync("Apple", default).GetAwaiter().GetResult().Single().Symbol == "US.AAPL", "Catalogue search failed.");
    Reject(() => provider.FetchAsync("US.AAPL", default).GetAwaiter().GetResult());
    Reject(() => provider.FetchAsync("MY.1155", default).GetAwaiter().GetResult());
    Assert(handler.Requests == 0, "Unconfigured or unsupported fetch sent a request.");
});
Check("Marketstack adjusted daily prices preserve US trading dates", () =>
{
    using var doc = JsonDocument.Parse(FakeMarketstackHandler.Payload());
    var company = new CompanySearchResult("US.AAPL", "AAPL", "Apple", "NASDAQ");
    var data = MarketstackDataProvider.Parse(doc.RootElement, company, now);
    Assert(data.SplitAdjusted && data.Daily.Length == 120 && data.Fundamentals is null && data.Quote is null, "Daily-only mapping is wrong.");
    Assert(DataValidation.LocalTime(data.Symbol, data.Daily[^1].Time).Date == now.AddDays(-1).Date, "UTC midnight shifted the trading day.");
    Assert(engine.Evaluate(data, "swing", false).Signal == "Hold", "Adjusted daily history failed analysis.");
    Assert(engine.Evaluate(data, "day", false).Signal == "Insufficient data", "Daily data generated an intraday signal.");
});
Check("Twelve Data daily prices map to split-adjusted chronological bars", () =>
{
    var values = Enumerable.Range(0, 100).Select(i => new
    {
        datetime = now.AddDays(-2 - i).ToString("yyyy-MM-dd"), open = "10.00", high = "10.50", low = "9.50", close = "10.00", volume = "1000"
    });
    var payload = JsonSerializer.Serialize(new { meta = new { symbol = "AAPL", name = "Apple Inc.", currency = "USD" }, values });
    using var document = JsonDocument.Parse(payload);
    var data = TwelveDataProvider.Parse(document.RootElement, new("US.AAPL", "AAPL", "Apple", "NASDAQ", ExchangeMic: "XNAS", Currency: "USD"), now);
    Assert(data.Symbol == "US.AAPL" && data.Currency == "USD" && data.SplitAdjusted && data.Daily.Length == 100, "Twelve Data daily history did not map correctly.");
    Assert(DataValidation.LocalTime(data.Symbol, data.Daily[^1].Time).Date == now.AddDays(-2).Date, "Twelve Data daily date shifted markets.");
    Assert(data.Warnings!.Any(x => x.Contains("split adjustment", StringComparison.OrdinalIgnoreCase)), "Twelve Data split-adjustment provenance is missing.");
    Reject(() => TwelveDataProvider.Parse(document.RootElement, new("MY.AAPL", "DIFFERENT", "Wrong stock", "MYX", ExchangeMic: "XKLS"), now));
});
Check("Provider settings accept case and whitespace without losing the starter list", () =>
{
    var factory = new PublicClients(new FakeMarketstackHandler());
    using var catalogue = new AvailableCompanyCatalog(clock);
    using var cache = new MemoryCache(new MemoryCacheOptions());
    foreach (var name in new[] { "twelvedata", "TwelveData", " TwelveData ", "MARKETSTACK" })
    {
        var config = TestSettings().AddInMemoryCollection(new Dictionary<string,string?> { ["MarketData:Provider"] = name }).Build();
        var provider = new MarketDataProvider(new PublicMarketDataProvider(factory, cache, clock), new MarketstackDataProvider(factory, cache, clock, config), new TwelveDataProvider(factory, cache, clock, config), config, catalogue);
        Assert(provider.AvailableCompanies().Length == 10 && provider.DailyOnly, "Provider spelling silently selected an empty legacy catalogue.");
    }
});
Check("Shipped worldwide catalogue is visible immediately and opens an exact exchange without search", () =>
{
    var snapshotPath = Path.Combine(AppContext.BaseDirectory, "Catalogue", "company-catalogue-twelvedata.json");
    var handler = new FakeTwelveCatalogueHandler();
    var factory = new PublicClients(handler);
    using var catalogue = new AvailableCompanyCatalog(clock);
    using var cache = new MemoryCache(new MemoryCacheOptions());
    var config = TestSettings().AddInMemoryCollection(new Dictionary<string,string?> {
        ["MarketData:Provider"] = " twelvedata ", ["MarketData:TwelveDataApiKey"] = "fixture-test-key",
        ["MarketData:CompanyCatalogPath"] = snapshotPath,
        ["Hosting:PublicMode"] = "true", ["RENDER_EXTERNAL_HOSTNAME"] = "marketlens.onrender.com" }).Build();
    var provider = new MarketDataProvider(new PublicMarketDataProvider(factory, cache, clock), new MarketstackDataProvider(factory, cache, clock, config), new TwelveDataProvider(factory, cache, clock, config), config, catalogue);
    var all = provider.AvailableCompanies();
    Assert(all.Length > 10000, "The deployment did not include the worldwide stock catalogue.");
    foreach (var symbol in new[] { "US.AAPL", "US.NVDA", "MY.1155", "MY.5099", "SG.D05", "HK.0700" })
        Assert(all.Any(x => x.Symbol == symbol), "Missing expected listing: " + symbol);
    var voo = all.Single(x => x.Symbol == "US.VOO");
    Assert(voo.ExchangeMic == "ARCX" && voo.InstrumentType == "ETF", "VOO is missing, incorrectly typed, or mapped to a secondary exchange.");
    Assert(provider.AvailableCompanies(query: "S&P 500").Any(x => x.Symbol == "US.VOO"), "S&P 500 filtering did not find VOO.");
    var store = new DatasetStore(new CheckEnvironment(), clock, new RenderHosting(config));
    var page = new MarketLens.Pages.IndexModel(store, provider, engine, backtest, new AiExplanationService(factory, config), clock);
    page.OnGetAsync(default).GetAwaiter().GetResult();
    Assert(page.Companies.Length == 24 && page.TotalCompanyCount == all.Length && page.HasNext && handler.Requests == 0, "Homepage required a search or API call before showing its stocks.");
    page.Query = "Maybank";
    page.OnGetSearchAsync(default).GetAwaiter().GetResult();
    Assert(page.Companies.Any(x => x.Symbol == "MY.1155"), "The Maybank alias did not match its legal name.");
    Assert(provider.AvailableCompanies(query: "AirAsia").Any(x => x.Symbol == "MY.5099"), "The AirAsia alias did not find Capital A.");
    page.Query = "MY.1155";
    page.OnGetSearchAsync(default).GetAwaiter().GetResult();
    Assert(page.Companies.Single().Symbol == "MY.1155" && handler.Requests == 0, "Saved-list filtering spent quota or lost the listing.");
    var data = provider.FetchListedAsync("MY.1155", default).GetAwaiter().GetResult();
    Assert(data.Symbol == "MY.1155" && handler.Requests == 1 && handler.LastQuery.Contains("mic_code=XKLS"), "Opening a catalogue stock searched or requested the wrong exchange.");
});
Check("Marketstack refuses mismatched symbols and abstains on unadjusted history", () =>
{
    using var doc = JsonDocument.Parse(FakeMarketstackHandler.Payload(adjusted: false));
    var company = new CompanySearchResult("US.AAPL", "AAPL", "Apple", "NASDAQ");
    var data = MarketstackDataProvider.Parse(doc.RootElement, company, now);
    Assert(!data.SplitAdjusted && engine.Evaluate(data, "swing", false).Signal == "Insufficient data", "Raw bars generated a signal.");
    Reject(() => MarketstackDataProvider.Parse(doc.RootElement, company with { ProviderSymbol = "MSFT" }, now));
});
Check("Marketstack caches prices and avoids additional metadata requests", () =>
{
    var handler = new FakeMarketstackHandler();
    var provider = new MarketstackDataProvider(new PublicClients(handler), new MemoryCache(new MemoryCacheOptions()), clock,
        TestSettings().AddInMemoryCollection(new Dictionary<string,string?> { ["MarketData:MarketstackApiKey"] = "fixture-test-key" }).Build());
    var first = provider.FetchAsync("US.AAPL", default).GetAwaiter().GetResult();
    provider.FetchAsync("US.AAPL", default).GetAwaiter().GetResult();
    Assert(handler.Requests == 1 && first.Daily.Length == 120, "Cached history used additional quota.");
});
Check("Marketstack quota responses pause requests without revealing a key", () =>
{
    var handler = new FakeMarketstackHandler { Status = HttpStatusCode.TooManyRequests };
    var provider = new MarketstackDataProvider(new PublicClients(handler), new MemoryCache(new MemoryCacheOptions()), clock,
        TestSettings().AddInMemoryCollection(new Dictionary<string,string?> { ["MarketData:MarketstackApiKey"] = "fixture-test-key" }).Build());
    Reject(() => provider.FetchAsync("US.AAPL", default).GetAwaiter().GetResult());
    Reject(() => provider.FetchAsync("US.AAPL", default).GetAwaiter().GetResult());
    Assert(handler.Requests == 1, "Provider retried immediately after a quota response.");
});
Check("Owner-only access rejects missing, malformed and incorrect credentials", () =>
{
    var header = "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("owner:fixture-password-123"));
    Assert(OwnerAccess.Accepts(header, "owner", "fixture-password-123"), "Correct owner login rejected.");
    Assert(!OwnerAccess.Accepts(header, "owner", "wrong-password"), "Incorrect password accepted.");
    Assert(!OwnerAccess.Accepts("Basic %invalid", "owner", "fixture-password-123") && !OwnerAccess.Accepts("", "owner", "fixture-password-123"), "Malformed credentials accepted.");
});

Check("Market directory parses non-starter and international listings without inventing coverage", () =>
{
    using var doc = JsonDocument.Parse(FakeDirectoryHandler.Listings);
    var listings = MarketstackDataProvider.ParseListings(doc.RootElement);
    Assert(listings.Length == 3 && listings.Any(x => x.Symbol == "US.BA") && listings.Any(x => x.Symbol == "MY.1155") && listings.Any(x => x.Symbol == "HK.700"), "International or non-starter listing was omitted.");
    Assert(listings.Single(x => x.Symbol == "MY.1155").ExchangeMic == "XKLS", "Listing exchange was lost.");
});
Check("Directory browsing and searches cache results and fetch a non-starter stock", () =>
{
    var handler = new FakeDirectoryHandler();
    var provider = new MarketstackDataProvider(new PublicClients(handler), new MemoryCache(new MemoryCacheOptions()), clock,
        TestSettings().AddInMemoryCollection(new Dictionary<string,string?> { ["MarketData:MarketstackApiKey"] = "fixture-test-key" }).Build());
    var listings = provider.SearchAsync("Boeing", default).GetAwaiter().GetResult();
    provider.SearchAsync("Boeing", default).GetAwaiter().GetResult();
    var data = provider.FetchAsync("US.BA", default).GetAwaiter().GetResult();
    Assert(handler.Requests == 2 && data.Symbol == "US.BA" && provider.HasPrices("US.BA"), "Search cache or non-starter price access failed.");
    provider.SearchAsync("", default, "XKLS", 24).GetAwaiter().GetResult();
    Assert(handler.LastQuery.Contains("exchange=XKLS") && handler.LastQuery.Contains("offset=24"), "Browse market/page filter was lost.");
});
Check("International daily prices retain exchange, currency and local date", () =>
{
    using var doc = JsonDocument.Parse(FakeMarketstackHandler.Payload().Replace("AAPL", "1155").Replace("XNAS", "XKLS"));
    var listing = new CompanySearchResult("MY.1155", "1155", "Maybank", "Bursa", ExchangeMic: "XKLS", Currency: "MYR");
    var data = MarketstackDataProvider.Parse(doc.RootElement, listing, now);
    Assert(data.Currency == "MYR" && DataValidation.LocalTime(data.Symbol, data.Daily[^1].Time).Date == now.AddDays(-1).Date, "International currency or trading date incorrect.");
    Reject(() => MarketstackDataProvider.Parse(doc.RootElement, listing with { ExchangeMic = "XSES" }, now));
});
Check("Invalid directory filters send no requests", () =>
{
    var handler = new FakeDirectoryHandler();
    var provider = new MarketstackDataProvider(new PublicClients(handler), new MemoryCache(new MemoryCacheOptions()), clock,
        TestSettings().AddInMemoryCollection(new Dictionary<string,string?> { ["MarketData:MarketstackApiKey"] = "fixture-test-key" }).Build());
    Assert(provider.SearchAsync("", default, "invalid", 0).GetAwaiter().GetResult().Length == 0, "Invalid filter accepted.");
    provider.SearchAsync("", default, "XNAS", -1).GetAwaiter().GetResult();
    Assert(handler.Requests == 0, "Invalid browsing input consumed quota.");
});
Check("Public discovery expands the starter list and permits returned listings only", () =>
{
    var handler = new FakeDirectoryHandler();
    var factory = new PublicClients(handler);
    using var catalogue = new AvailableCompanyCatalog(clock);
    using var cache = new MemoryCache(new MemoryCacheOptions());
    var config = TestSettings().AddInMemoryCollection(new Dictionary<string,string?> { ["MarketData:Provider"] = "Marketstack", ["MarketData:MarketstackApiKey"] = "fixture-test-key" }).Build();
    var provider = new MarketDataProvider(new PublicMarketDataProvider(factory, cache, clock), new MarketstackDataProvider(factory, cache, clock, config), new TwelveDataProvider(factory, cache, clock, config), config, catalogue);
    var page = provider.DiscoverAsync(null, null, 0, default).GetAwaiter().GetResult();
    Assert(page.Companies.Length == 3 && provider.AvailableCompanies().Length > 10, "Public browsing stayed limited to starter companies.");
    Assert(!provider.HasPrices("MY.1155") && provider.AvailableCountries.Length == 0, "Discovery claimed verified prices.");
    provider.DiscoverAsync(null, null, 0, default).GetAwaiter().GetResult();
    Assert(handler.Requests == 1, "Repeated browsing spent additional quota.");
    Reject(() => provider.FetchListedAsync("US.UNLISTED", default).GetAwaiter().GetResult());
    Assert(handler.Requests == 1, "Arbitrary visitor symbol spent quota.");
    Assert(provider.FetchListedAsync("US.BA", default).GetAwaiter().GetResult().Symbol == "US.BA", "A discovered company could not be opened.");
});
Check("Directory pagination uses raw rows even when filtering removes listings", () =>
{
    var handler = new FakeDirectoryHandler { DirectoryPayload = "{\"pagination\":{\"total\":48},\"data\":[{\"has_eod\":false}]}" };
    using var cache = new MemoryCache(new MemoryCacheOptions());
    var config = TestSettings().AddInMemoryCollection(new Dictionary<string,string?> { ["MarketData:MarketstackApiKey"] = "fixture-test-key" }).Build();
    var provider = new MarketstackDataProvider(new PublicClients(handler), cache, clock, config);
    var page = provider.SearchPageAsync("", default, "XSES", 0).GetAwaiter().GetResult();
    Assert(page.Companies.Length == 0 && page.HasNext, "An empty filtered page hid subsequent provider pages.");
    handler.DirectoryPayload = "{\"pagination\":{\"total\":25},\"data\":[{\"has_eod\":false}]}";
    Assert(!provider.SearchPageAsync("", default, "XSES", 24).GetAwaiter().GetResult().HasNext, "Last provider page still offers Next.");
});
Check("Homepage lists the saved cross-country catalogue and search spends no quota", () =>
{
    var handler = new FakeDirectoryHandler();
    var factory = new PublicClients(handler);
    using var catalogue = new AvailableCompanyCatalog(clock);
    using var cache = new MemoryCache(new MemoryCacheOptions());
    var snapshotPath = Path.Combine(Path.GetTempPath(), "MarketLens-catalogue-" + Guid.NewGuid() + ".json");
    using var document = JsonDocument.Parse(FakeDirectoryHandler.Listings);
    File.WriteAllText(snapshotPath, JsonSerializer.Serialize(MarketstackDataProvider.ParseListings(document.RootElement), DatasetStore.JsonOptions));
    var config = TestSettings().AddInMemoryCollection(new Dictionary<string,string?> {
        ["MarketData:Provider"] = "Marketstack", ["MarketData:MarketstackApiKey"] = "fixture-test-key",
        ["MarketData:CompanyCatalogPath"] = snapshotPath,
        ["Hosting:PublicMode"] = "true", ["RENDER_EXTERNAL_HOSTNAME"] = "marketlens.onrender.com" }).Build();
    var provider = new MarketDataProvider(new PublicMarketDataProvider(factory, cache, clock), new MarketstackDataProvider(factory, cache, clock, config), new TwelveDataProvider(factory, cache, clock, config), config, catalogue);
    var store = new DatasetStore(new CheckEnvironment(), clock, new RenderHosting(config));
    var page = new MarketLens.Pages.IndexModel(store, provider, engine, backtest, new AiExplanationService(factory, config), clock);
    page.OnGetAsync(default).GetAwaiter().GetResult();
    Assert(page.TotalCompanyCount == 13 && page.Companies.Length == 13, "Full saved catalogue was not visible before searching.");
    page.Query = "MY."; page.BrowseOffset = 24;
    page.OnGetSearchAsync(default).GetAwaiter().GetResult();
    Assert(page.BrowseOffset == 0 && page.Companies.Single().Symbol == "MY.1155" && page.MatchingCount == 1, "Local prefix search failed.");
    Assert(page.TotalCompanyCount == 13 && handler.Requests == 0, "Search changed the total or spent provider quota.");
    Assert(!provider.HasPrices("MY.1155"), "A saved directory listing claimed verified prices.");
    Assert(WebsiteCompanyCatalog.Read(snapshotPath).Length == 3, "Saved catalogue did not survive reloading.");
    File.WriteAllText(snapshotPath, JsonSerializer.Serialize(new[] { new CompanySearchResult("MY.AAPL", "AAPL", "Wrong exchange", "NASDAQ", ExchangeMic: "XNAS") }));
    Reject(() => WebsiteCompanyCatalog.Read(snapshotPath));
    File.Delete(snapshotPath);
});
Check("Country catalogue starts empty and excludes demos and imports", () =>
{
    using var catalogue = new AvailableCompanyCatalog(clock);
    Assert(catalogue.Countries("Marketstack").Length == 0, "Unverified countries appeared before a price response.");
    catalogue.Record("Marketstack", flat with { IsSample = true });
    catalogue.Record("Marketstack", flat with { IsImported = true });
    Assert(catalogue.Countries("Marketstack").Length == 0, "Fictional/imported data enabled a provider country.");
});
Check("Country selection and search use only verified companies in that country", () =>
{
    using var catalogue = new AvailableCompanyCatalog(clock);
    catalogue.Record("Marketstack", flat with { Symbol = "US.AAPL", Name = "Apple" });
    catalogue.Record("Marketstack", flat with { Symbol = "MY.1155", Name = "Maybank", Currency = "MYR" });
    Assert(catalogue.Countries("Marketstack").Length == 2 && catalogue.Countries("Marketstack").All(x => x.Count == 1), "Country counts are wrong.");
    Assert(catalogue.Companies("Marketstack", "MY").Single().Symbol == "MY.1155", "Country selection did not show its company.");
    Assert(catalogue.Companies("Marketstack", "MY", "Apple").Length == 0 && catalogue.Companies("Marketstack", "MY", "may").Length == 1, "Search leaked another country's company.");
    Assert(catalogue.Companies("Yahoo Finance").Length == 0, "One provider enabled another provider's markets.");
});
Check("Directory results cannot enable a country before daily retrieval succeeds", () =>
{
    var handler = new FakeDirectoryHandler();
    var factory = new PublicClients(handler);
    using var catalogue = new AvailableCompanyCatalog(clock);
    using var cache = new MemoryCache(new MemoryCacheOptions());
    var config = TestSettings().AddInMemoryCollection(new Dictionary<string,string?> { ["MarketData:Provider"] = "Marketstack", ["MarketData:MarketstackApiKey"] = "fixture-test-key" }).Build();
    var provider = new MarketDataProvider(new PublicMarketDataProvider(factory, cache, clock), new MarketstackDataProvider(factory, cache, clock, config), new TwelveDataProvider(factory, cache, clock, config), config, catalogue);
    provider.SearchAsync("Boeing", default).GetAwaiter().GetResult();
    Assert(provider.AvailableCountries.Length == 0, "Directory metadata was mistaken for price availability.");
    provider.FetchAsync("US.BA", false, default).GetAwaiter().GetResult();
    Assert(provider.AvailableCountries.Single().Code == "US" && provider.AvailableCompanies("US", "Boeing").Single().Symbol == "US.BA", "Successful retrieval did not add its company.");
    var before = handler.Requests;
    provider.AvailableCompanies("US", "Boeing");
    provider.FetchAsync("US.BA", false, default).GetAwaiter().GetResult();
    Assert(handler.Requests == before, "Browsing or cached retrieval consumed additional requests.");
});
Check("Missing connection and failed price access leave countries unavailable", () =>
{
    var handler = new FakeMarketstackHandler { Status = HttpStatusCode.Forbidden };
    var factory = new PublicClients(handler);
    using var catalogue = new AvailableCompanyCatalog(clock);
    using var cache = new MemoryCache(new MemoryCacheOptions());
    var config = TestSettings().AddInMemoryCollection(new Dictionary<string,string?> { ["MarketData:Provider"] = "Marketstack" }).Build();
    var provider = new MarketDataProvider(new PublicMarketDataProvider(factory, cache, clock), new MarketstackDataProvider(factory, cache, clock, config), new TwelveDataProvider(factory, cache, clock, config), config, catalogue);
    Reject(() => provider.FetchAsync("US.AAPL", false, default).GetAwaiter().GetResult());
    Assert(provider.AvailableCountries.Length == 0 && handler.Requests == 0, "Missing key enabled markets or sent requests.");
    config["MarketData:MarketstackApiKey"] = "fixture-test-key";
    Reject(() => provider.FetchAsync("US.AAPL", false, default).GetAwaiter().GetResult());
    Assert(provider.AvailableCountries.Length == 0, "A rejected price request enabled a country.");
});
Check("Website list includes Apple before fetching and search only filters that list", () =>
{
    var handler = new FakeMarketstackHandler();
    var factory = new PublicClients(handler);
    using var catalogue = new AvailableCompanyCatalog(clock);
    using var cache = new MemoryCache(new MemoryCacheOptions());
    var config = TestSettings().AddInMemoryCollection(new Dictionary<string,string?> { ["MarketData:Provider"] = "Marketstack" }).Build();
    var provider = new MarketDataProvider(new PublicMarketDataProvider(factory, cache, clock), new MarketstackDataProvider(factory, cache, clock, config), new TwelveDataProvider(factory, cache, clock, config), config, catalogue);
    Assert(provider.AvailableCompanies().Length == MarketstackDataProvider.Popular.Length, "Initial website list is empty.");
    Assert(provider.AvailableCompanies(query: " apple ").Single().Symbol == "US.AAPL", "Apple cannot be found before fetching.");
    Assert(provider.AvailableCompanies(query: "aapl").Single().Symbol == "US.AAPL", "Ticker search failed.");
    Assert(provider.AvailableCompanies(query: "unknown company").Length == 0 && handler.Requests == 0, "List filtering sent provider requests or invented a company.");
    Assert(!provider.HasPrices("US.AAPL"), "A directory entry claimed to contain fetched prices.");
});
Check("Visitors can load listed Apple prices once but cannot fetch arbitrary symbols", () =>
{
    var handler = new FakeMarketstackHandler();
    var factory = new PublicClients(handler);
    using var catalogue = new AvailableCompanyCatalog(clock);
    using var cache = new MemoryCache(new MemoryCacheOptions());
    var config = TestSettings().AddInMemoryCollection(new Dictionary<string,string?> { ["MarketData:Provider"] = "Marketstack", ["MarketData:MarketstackApiKey"] = "fixture-test-key" }).Build();
    var provider = new MarketDataProvider(new PublicMarketDataProvider(factory, cache, clock), new MarketstackDataProvider(factory, cache, clock, config), new TwelveDataProvider(factory, cache, clock, config), config, catalogue);
    Reject(() => provider.FetchListedAsync("US.UNLISTED", default).GetAwaiter().GetResult());
    Assert(handler.Requests == 0, "An unlisted company consumed API quota.");
    var data = provider.FetchListedAsync("US.AAPL", default).GetAwaiter().GetResult();
    provider.FetchListedAsync("US.AAPL", default).GetAwaiter().GetResult();
    Assert(data.Symbol == "US.AAPL" && data.Daily.Length > 0 && handler.Requests == 1, "Opening Apple failed or repeat views consumed quota.");
});
Check("Public browsing stays open while provider management requires the owner", () =>
{
    Assert(!OwnerAccess.RequiresOwner("/", null) && !OwnerAccess.RequiresOwner("/", "Search") && !OwnerAccess.RequiresOwner("/", "Browse"), "Visitor browsing requires owner access.");
    Assert(!OwnerAccess.RequiresOwner("/Owner", null) && !OwnerAccess.RequiresOwner("/health", null), "Login or health is protected.");
    Assert(OwnerAccess.RequiresOwner("/Data", null) && OwnerAccess.RequiresOwner("/data", "Search"), "Provider directory management is public.");
    Assert(OwnerAccess.RequiresOwner("/Data/", null) && OwnerAccess.RequiresOwner("/Index/", "Search", "POST"), "Trailing slash or POST bypassed owner access.");
    Assert(OwnerAccess.RequiresOwner("/", "Refresh") && OwnerAccess.RequiresOwner("/Index", "Ai"), "Visitor can invoke a provider/AI mutation.");
});

Check("Incomplete historical rows shorten the recent segment without inventing prices", () =>
{
    var payload = System.Text.Json.Nodes.JsonNode.Parse(FakeMarketstackHandler.Payload())!;
    payload["data"]![84]!["close"] = null;
    payload["data"]![84]!["adj_close"] = null;
    using var document = JsonDocument.Parse(payload.ToJsonString());
    var data = MarketstackDataProvider.Parse(document.RootElement, new("US.AAPL", "AAPL", "Apple", "NASDAQ"), now);
    Assert(data.Daily.Length == 84 && data.SplitAdjusted && data.Warnings!.Any(x => x.Contains("History shortened")), "Incomplete history was joined or fabricated.");
    payload["data"]![0]!["close"] = null; payload["data"]![0]!["adj_close"] = null;
    using var invalid = JsonDocument.Parse(payload.ToJsonString());
    Reject(() => MarketstackDataProvider.Parse(invalid.RootElement, new("US.AAPL", "AAPL", "Apple", "NASDAQ"), now));
});

Check("Mixed currencies retain only the recent compatible segment and withhold short-history signals", () =>
{
    var payload = System.Text.Json.Nodes.JsonNode.Parse(FakeMarketstackHandler.Payload())!;
    foreach (var row in payload["data"]!.AsArray()) row!["price_currency"] = "usd";
    payload["data"]![11]!["price_currency"] = "EUR";
    using var doc = JsonDocument.Parse(payload.ToJsonString());
    var company = new CompanySearchResult("US.MSFT", "MSFT", "Microsoft", "NASDAQ", ExchangeMic: "XNAS", Currency: "USD");
    // Synthetic fixture prices, with identities changed to the listing under test.
    using var matching = JsonDocument.Parse(doc.RootElement.GetRawText().Replace("AAPL", "MSFT"));
    var data = MarketstackDataProvider.Parse(matching.RootElement, company, now);
    Assert(data.Currency == "USD" && data.Daily.Length == 11, "Mixed currencies were joined or valid recent prices discarded.");
    Assert(engine.Evaluate(data, "swing", false).Signal == "Insufficient data", "An unreliable short history generated a signal.");
    payload["data"]![0]!["price_currency"] = "EUR";
    using var wrongLatest = JsonDocument.Parse(payload.ToJsonString().Replace("AAPL", "MSFT"));
    Reject(() => MarketstackDataProvider.Parse(wrongLatest.RootElement, company, now));
});
Check("Every listed company uses the shared fetch/cache path including exchange recovery", () =>
{
    var handler = new CataloguePricesHandler();
    var factory = new PublicClients(handler);
    using var catalogue = new AvailableCompanyCatalog(clock);
    using var cache = new MemoryCache(new MemoryCacheOptions());
    var config = TestSettings().AddInMemoryCollection(new Dictionary<string,string?> { ["MarketData:Provider"] = "Marketstack", ["MarketData:MarketstackApiKey"] = "fixture-test-key" }).Build();
    var provider = new MarketDataProvider(new PublicMarketDataProvider(factory, cache, clock), new MarketstackDataProvider(factory, cache, clock, config), new TwelveDataProvider(factory, cache, clock, config), config, catalogue);
    foreach (var company in provider.AvailableCompanies())
    {
        var data = provider.FetchListedAsync(company.Symbol, default).GetAwaiter().GetResult();
        Assert(data.Symbol == company.Symbol && data.Daily.Length == 120, "Listing failed: " + company.Symbol);
        Assert(engine.Evaluate(data, "swing", false).Signal == "Hold", "Analysis failed: " + company.Symbol);
        var before = handler.Requests;
        provider.FetchListedAsync(company.Symbol, default).GetAwaiter().GetResult();
        Assert(handler.Requests == before, "Repeat view bypassed cache: " + company.Symbol);
    }
    Assert(handler.RecoveredExchange, "Empty price response did not resolve the provider directory exchange.");
});
Check("Failed requests preserve their reason during the retry pause", () =>
{
    var handler = new FakeMarketstackHandler { Status = HttpStatusCode.Forbidden };
    var factory = new PublicClients(handler);
    using var catalogue = new AvailableCompanyCatalog(clock);
    using var cache = new MemoryCache(new MemoryCacheOptions());
    var config = TestSettings().AddInMemoryCollection(new Dictionary<string,string?> { ["MarketData:Provider"] = "Marketstack", ["MarketData:MarketstackApiKey"] = "fixture-test-key" }).Build();
    var provider = new MarketDataProvider(new PublicMarketDataProvider(factory, cache, clock), new MarketstackDataProvider(factory, cache, clock, config), new TwelveDataProvider(factory, cache, clock, config), config, catalogue);
    string Message() { try { provider.FetchListedAsync("US.AAPL", default).GetAwaiter().GetResult(); return ""; } catch (InvalidDataException e) { return e.Message; } }
    var first = Message(); var second = Message();
    Assert(first.Contains("rejected the API key") && second.Contains(first) && second.Contains("Retry available") && handler.Requests == 1, "Retry lost the reason or consumed quota.");
});

Check("Automatic catalogue refresh persists changes, removes old listings, and survives failures", () =>
{
    var folder = Path.Combine(Path.GetTempPath(), "MarketLens-refresh-" + Guid.NewGuid());
    Directory.CreateDirectory(folder);
    try
    {
        var seed = Enumerable.Range(1, 5).Select(i => new CompanySearchResult("US.TEST" + i, "TEST" + i, "Test " + i, "NASDAQ", ExchangeMic: "XNAS", Currency: "USD")).ToArray();
        var seedPath = Path.Combine(folder, "seed.json");
        File.WriteAllText(seedPath, JsonSerializer.Serialize(seed));
        var config = TestSettings().AddInMemoryCollection(new Dictionary<string,string?> {
            ["MarketData:Provider"] = "TwelveData", ["MarketData:CompanyCatalogPath"] = seedPath,
            ["Catalogue:StoragePath"] = Path.Combine(folder, "store"), ["Catalogue:RemoteBaseUrl"] = "https://catalogue.example.invalid/Catalogue" }).Build();
        var handler = new FakeCatalogueRefreshHandler(seed.Take(4).ToArray(), now);
        var factory = new PublicClients(handler);
        var env = new CheckEnvironment { ContentRootPath = folder };
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<RefreshingCompanyCatalog>.Instance;
        var live = new RefreshingCompanyCatalog(factory, config, env, clock, logger);
        Assert(live.Current.Companies.Length == 5, "Seed catalogue unavailable before background refresh.");
        live.RefreshAsync(default).GetAwaiter().GetResult();
        Assert(live.Current.Companies.Length == 4 && !live.RefreshFailed && !live.Current.Companies.Any(x => x.Symbol == "US.TEST5"), "Refresh failed to remove an absent listing.");
        var restarted = new RefreshingCompanyCatalog(factory, config, env, clock, logger);
        Assert(restarted.Current.Companies.Length == 4 && restarted.Current.Manifest == live.Current.Manifest, "Restart lost the persisted refreshed catalogue.");
        var downloads = handler.Downloads;
        handler.Manifest = handler.Manifest with { CheckedAt = now.AddSeconds(30) };
        live.RefreshAsync(default).GetAwaiter().GetResult();
        Assert(handler.Downloads == downloads && live.Current.Manifest!.CheckedAt == now.AddSeconds(30), "Unchanged catalogue was downloaded again.");
        handler.Status = HttpStatusCode.ServiceUnavailable;
        live.RefreshAsync(default).GetAwaiter().GetResult();
        Assert(live.RefreshFailed && live.Current.Companies.Length == 4, "Network failure destroyed the last valid list.");
        handler.Status = HttpStatusCode.OK;
        handler.Manifest = handler.Manifest with { Sha256 = new string('0', 64), CheckedAt = now.AddMinutes(1) };
        live.RefreshAsync(default).GetAwaiter().GetResult();
        Assert(live.RefreshFailed && live.Current.Companies.Length == 4, "Bad checksum replaced the catalogue.");
        handler.SetSnapshot(seed.Take(1).ToArray(), now.AddMinutes(2));
        live.RefreshAsync(default).GetAwaiter().GetResult();
        Assert(live.RefreshFailed && live.Current.Companies.Length == 4, "A truncated directory replaced the valid catalogue.");
        handler.SetSnapshot(seed.Take(4).Select(x => x with { Symbol = x.Symbol.Replace("US.", "MY."), ExchangeMic = "XKLS", Currency = "MYR" }).ToArray(), now.AddMinutes(3));
        live.RefreshAsync(default).GetAwaiter().GetResult();
        Assert(live.RefreshFailed && live.Current.Companies.All(x => x.Symbol.StartsWith("US.")), "A partial directory silently removed an entire market.");
        using var catalogue = new AvailableCompanyCatalog(clock);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var provider = new MarketDataProvider(new PublicMarketDataProvider(factory, cache, clock), new MarketstackDataProvider(factory, cache, clock, config), new TwelveDataProvider(factory, cache, clock, config), config, catalogue, live);
        Assert(provider.AvailableCompanies().Length == 4, "Starter or previously checked entries resurrected removed listings.");
    }
    finally
    {
        if (!Path.GetFullPath(folder).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new Exception("Unexpected temporary test path.");
        Directory.Delete(folder, true);
    }
});
Check("Intraday quotes cache shared requests and preserve an explicitly stale snapshot on failure", () =>
{
    var quoteClock = new MutableClock(now);
    var handler = new FakeIntradayHandler(now);
    var factory = new PublicClients(handler);
    using var cache = new MemoryCache(new MemoryCacheOptions());
    var config = TestSettings().AddInMemoryCollection(new Dictionary<string,string?> { ["MarketData:TwelveDataApiKey"] = "fixture-test-key" }).Build();
    var provider = new TwelveDataProvider(factory, cache, quoteClock, config);
    var quotes = new TwelveDataQuoteService(provider, quoteClock);
    var listing = new CompanySearchResult("MY.1155", "1155", "Maybank", "MYX", ExchangeMic: "XKLS", Currency: "MYR");
    var first = quotes.GetAsync(listing, default).GetAwaiter().GetResult();
    var second = quotes.GetAsync(listing, default).GetAwaiter().GetResult();
    Assert(first.Quote?.Price == 10 && second.Quote == first.Quote && handler.Requests == 1, "Shared quote cache spent duplicate credits.");
    Assert(handler.LastQuery.Contains("interval=1min") && handler.LastQuery.Contains("mic_code=XKLS"), "Intraday quote did not request the exact exchange and minute interval.");
    quoteClock.Now = now.AddSeconds(61); handler.Status = HttpStatusCode.TooManyRequests;
    var failed = quotes.GetAsync(listing, default).GetAwaiter().GetResult();
    Assert(failed.Quote == first.Quote && failed.Error is not null && failed.RefreshAfterSeconds == 300, "Failed refresh lost the snapshot or hid its failure.");
    quotes.GetAsync(listing, default).GetAwaiter().GetResult();
    Assert(handler.Requests == 2, "Failed quote retries bypassed backoff.");
    using var wrongCurrency = JsonDocument.Parse(handler.Payload.Replace("MYR", "USD"));
    Reject(() => TwelveDataProvider.ParseQuote(wrongCurrency.RootElement, listing, now));
    using var wrongExchange = JsonDocument.Parse(handler.Payload.Replace("XKLS", "XNAS"));
    Reject(() => TwelveDataProvider.ParseQuote(wrongExchange.RootElement, listing, now));
    using var noTimestamp = JsonDocument.Parse(handler.Payload.Replace("timestamp", "missing_time"));
    Reject(() => TwelveDataProvider.ParseQuote(noTimestamp.RootElement, listing, now));
});
Check("Twelve Data request budget rejects extra calls before consuming provider credits", () =>
{
    var quoteClock = new MutableClock(now);
    var handler = new FakeIntradayHandler(now);
    using var cache = new MemoryCache(new MemoryCacheOptions());
    var config = TestSettings().AddInMemoryCollection(new Dictionary<string,string?> {
        ["MarketData:TwelveDataApiKey"] = "fixture-test-key", ["MarketData:TwelveDataCreditsPerMinute"] = "1", ["MarketData:TwelveDataCreditsPerDay"] = "1" }).Build();
    var provider = new TwelveDataProvider(new PublicClients(handler), cache, quoteClock, config);
    var listing = new CompanySearchResult("MY.1155", "1155", "Maybank", "MYX", ExchangeMic: "XKLS", Currency: "MYR");
    provider.FetchQuoteAsync(listing, default).GetAwaiter().GetResult();
    Reject(() => provider.FetchQuoteAsync(listing, default).GetAwaiter().GetResult());
    quoteClock.Now = now.AddMinutes(2);
    Reject(() => provider.FetchQuoteAsync(listing, default).GetAwaiter().GetResult());
    Assert(handler.Requests == 1, "Minute/daily website budgets failed to block extra provider calls.");
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
sealed class MutableClock(DateTimeOffset now) : TimeProvider { public DateTimeOffset Now { get; set; } = now; public override DateTimeOffset GetUtcNow() => Now; }
sealed class FakeCatalogueRefreshHandler : HttpMessageHandler
{
    public byte[] Snapshot { get; private set; } = [];
    public CatalogueManifest Manifest { get; set; } = null!;
    public int Downloads { get; private set; }
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    public FakeCatalogueRefreshHandler(CompanySearchResult[] companies, DateTimeOffset now) => SetSnapshot(companies, now);
    public void SetSnapshot(CompanySearchResult[] companies, DateTimeOffset now)
    { Snapshot = JsonSerializer.SerializeToUtf8Bytes(companies); Manifest = new(RefreshingCompanyCatalog.Hash(Snapshot), companies.Length, now); }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.AbsolutePath.EndsWith("manifest.json"))
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(JsonSerializer.Serialize(Manifest)) });
        Downloads++;
        return Task.FromResult(new HttpResponseMessage(Status) { Content = new ByteArrayContent(Snapshot) });
    }
}
sealed class FakeIntradayHandler(DateTimeOffset now) : HttpMessageHandler
{
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    public int Requests { get; private set; }
    public string LastQuery { get; private set; } = "";
    public string Payload => JsonSerializer.Serialize(new { symbol = "1155", mic_code = "XKLS", currency = "MYR", close = "10", timestamp = now.AddMinutes(-1).ToUnixTimeSeconds(), is_market_open = true });
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    { Requests++; LastQuery = request.RequestUri!.Query; return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Payload) }); }
}
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

sealed class FakeMarketstackHandler : HttpMessageHandler
{
    public int Requests { get; private set; }
    public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
    public static string Payload(bool adjusted = true) => JsonSerializer.Serialize(new { data = Enumerable.Range(0, 120).Select(i => new
    {
        symbol = "AAPL", exchange = "XNAS", date = new DateTime(2026, 10, 8).AddDays(-i).ToString("yyyy-MM-dd") + "T00:00:00+0000",
        open = 10m, high = 10.1m, low = 9.9m, close = 10m, volume = 1000m,
        adj_open = adjusted ? (decimal?)10 : null, adj_high = adjusted ? (decimal?)10.1m : null,
        adj_low = adjusted ? (decimal?)9.9m : null, adj_close = adjusted ? (decimal?)10 : null
    }) });
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        if (!request.RequestUri!.AbsolutePath.EndsWith("/v2/eod") || !request.RequestUri.Query.Contains("limit=1000")) throw new Exception("Unexpected Marketstack endpoint.");
        return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Payload()) });
    }
}

sealed class CataloguePricesHandler : HttpMessageHandler
{
    public int Requests { get; private set; }
    public bool RecoveredExchange { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        var uri = request.RequestUri!;
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
        // Use a synthetic stale exchange for a company other than Walmart, to exercise generic recovery.
        if (uri.AbsolutePath.EndsWith("/v1/tickers"))
            return Reply("""{"data":[{"symbol":"MSFT","name":"Microsoft","has_eod":true,"stock_exchange":{"name":"Synthetic alternate US exchange","mic":"XNYS","country_code":"US"}}]}""");
        var ticker = query["symbols"].ToString();
        var mic = query["exchange"].ToString();
        if (ticker == "MSFT" && mic == "XNAS") return Reply("""{"data":[]}""");
        if (ticker == "MSFT" && mic == "XNYS") RecoveredExchange = true;
        return Reply(FakeMarketstackHandler.Payload().Replace("AAPL", ticker).Replace("XNAS", mic));
    }
    private static Task<HttpResponseMessage> Reply(string body) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
}

sealed class FakeTwelveCatalogueHandler : HttpMessageHandler
{
    public int Requests { get; private set; }
    public string LastQuery { get; private set; } = "";
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        if (!request.RequestUri!.AbsolutePath.EndsWith("/time_series")) throw new Exception("Opening a saved stock unexpectedly searched the provider.");
        LastQuery = request.RequestUri.Query;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
            {"meta":{"symbol":"1155","name":"Malayan Banking Berhad","currency":"MYR","mic_code":"XKLS"},"values":[
              {"datetime":"2026-10-07","open":"10","high":"11","low":"9","close":"10","volume":"1000"},
              {"datetime":"2026-10-06","open":"10","high":"11","low":"9","close":"10","volume":"1000"}
            ],"status":"ok"}
            """) });
    }
}

sealed class FakeDirectoryHandler : HttpMessageHandler
{
    public string DirectoryPayload { get; set; } = Listings;
    public int Requests { get; private set; }
    public string LastQuery { get; private set; } = "";
    public const string Listings = """
    {"data":[
      {"name":"Boeing","symbol":"BA","has_eod":true,"stock_exchange":{"name":"NYSE","mic":"XNYS","country_code":"US"}},
      {"name":"Maybank","symbol":"1155","has_eod":true,"currency":"MYR","stock_exchange":{"name":"Bursa","mic":"XKLS","country_code":"MY"}},
      {"name":"Tencent","symbol":"700","has_eod":true,"stock_exchange":{"name":"Hong Kong","mic":"XHKG","country_code":"HK"}},
      {"name":"No daily data","symbol":"NONE","has_eod":false,"stock_exchange":{"mic":"XNAS","country_code":"US"}},
      {"name":"Unknown market","symbol":"UNKNOWN","has_eod":true,"stock_exchange":{"mic":"????","country_code":"??"}}
    ]}
    """;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++; LastQuery = request.RequestUri!.Query;
        var path = request.RequestUri.AbsolutePath;
        var body = path.EndsWith("/v1/tickers") ? DirectoryPayload : FakeMarketstackHandler.Payload().Replace("AAPL", "BA").Replace("XNAS", "XNYS");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}
