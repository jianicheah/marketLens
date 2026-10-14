using System.Text.Json;
using MarketLens.Models;

namespace MarketLens.Services;

public sealed class TwelveDataQuoteService(TwelveDataProvider provider, TimeProvider clock)
{
    private sealed record Entry(StockQuoteSnapshot? Quote, string? Error, DateTimeOffset Until);
    private readonly Dictionary<string, Entry> entries = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<StockQuoteResult> GetAsync(CompanySearchResult listing, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation);
        try
        {
            var now = clock.GetUtcNow();
            entries.TryGetValue(listing.Symbol, out var prior);
            if (prior is not null && now < prior.Until)
                return new(prior.Quote, prior.Error, Math.Max(1, (int)Math.Ceiling((prior.Until - now).TotalSeconds)));
            Entry next;
            try
            {
                var quote = await provider.FetchQuoteAsync(listing, cancellation);
                next = new(quote, null, clock.GetUtcNow().AddSeconds(quote.MarketOpen == false ? 300 : 60));
            }
            catch (Exception e) when (e is InvalidDataException or HttpRequestException or JsonException or TaskCanceledException)
            {
                if (cancellation.IsCancellationRequested) throw;
                var reason = e is InvalidDataException ? e.Message : "The intraday quote request failed. Please try again later.";
                next = new(prior?.Quote, reason, clock.GetUtcNow().AddMinutes(5));
            }
            if (entries.Count >= 128 && !entries.ContainsKey(listing.Symbol)) entries.Remove(entries.Keys.First());
            entries[listing.Symbol] = next;
            return new(next.Quote, next.Error, Math.Max(1, (int)Math.Ceiling((next.Until - clock.GetUtcNow()).TotalSeconds)));
        }
        finally { gate.Release(); }
    }
}
