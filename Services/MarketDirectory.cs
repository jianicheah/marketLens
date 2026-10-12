namespace MarketLens.Services;

public sealed record MarketChoice(string Mic, string Market, string Name);
public static class MarketDirectory
{
    // These are browsing filters, not a claim of plan entitlement or successful price access.
    public static readonly MarketChoice[] Choices =
    [
        new("XNAS", "US", "US · NASDAQ"), new("XNYS", "US", "US · NYSE"),
        new("XKLS", "MY", "Malaysia · Bursa"), new("XHKG", "HK", "Hong Kong"), new("XSES", "SG", "Singapore"),
        new("XTKS", "JP", "Japan · Tokyo"), new("XLON", "UK", "United Kingdom"), new("XASX", "AU", "Australia"),
        new("XTSE", "CA", "Canada · Toronto"), new("XSHG", "SH", "China · Shanghai"), new("XSHE", "SZ", "China · Shenzhen"),
        new("XETR", "DE", "Germany · Xetra"), new("XPAR", "FR", "France"), new("XAMS", "NL", "Netherlands"),
        new("XMIL", "IT", "Italy"), new("XSWX", "CH", "Switzerland"), new("XMAD", "ES", "Spain"),
        new("XNSE", "IN", "India · NSE"), new("XBOM", "IN", "India · BSE"), new("XKRX", "KR", "South Korea"),
        new("XTAI", "TW", "Taiwan"), new("XIDX", "ID", "Indonesia"), new("XBKK", "TH", "Thailand"),
        new("XNZE", "NZ", "New Zealand"), new("BVMF", "BR", "Brazil"), new("XMEX", "MX", "Mexico"), new("XJSE", "ZA", "South Africa")
    ];
    public static MarketChoice? Find(string? mic) => Choices.FirstOrDefault(x => x.Mic == mic);
    public static string? MarketFor(string mic, string? country) => Find(mic)?.Market ?? country switch
    {
        "US" => "US", "MY" => "MY", "HK" => "HK", "SG" => "SG", "JP" => "JP", "AU" => "AU", "CA" => "CA",
        "GB" => "UK", "DE" => "DE", "FR" => "FR", "NL" => "NL", "IT" => "IT", "CH" => "CH", "ES" => "ES",
        "IN" => "IN", "KR" => "KR", "TW" => "TW", "ID" => "ID", "TH" => "TH", "NZ" => "NZ", "BR" => "BR", "MX" => "MX", "ZA" => "ZA", _ => null
    };
}
