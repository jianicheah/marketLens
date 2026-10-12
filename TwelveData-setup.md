# Twelve Data trial setup

The application selects this connector only when `MarketData:Provider` is `TwelveData`. It reads `MarketData:TwelveDataApiKey` from .NET User Secrets locally and `MarketData__TwelveDataApiKey` in Render. Neither key belongs in `appsettings.json` or Git.

## Local setup

From the project folder:

```powershell
dotnet user-secrets set "MarketData:Provider" "TwelveData"
dotnet user-secrets set "MarketData:TwelveDataApiKey" "YOUR_TWELVE_DATA_KEY"
```

Run `dotnet run`, then open Data sources. Search by the provider's ticker where a common-name search is ambiguous: `1155` for Malayan Banking Berhad and `0700` for Tencent.

## Render

Set these Environment values:

| Key | Value |
|---|---|
| `MarketData__Provider` | `TwelveData` |
| `MarketData__TwelveDataApiKey` | Your Twelve Data key |
| `Hosting__OwnerPassword` | Existing private owner password |
| `Hosting__PublicMode` | `true` |

## Verified on 12 October 2026

The configured local key successfully searched Apple and returned 100 daily split-adjusted Apple bars. It also found exact directory records for Malayan Banking Berhad (`1155`, XKLS, MYR) and Tencent Holdings (`0700`, XHKG, HKD). Price-history calls for those two symbols returned a Twelve Data plan-upgrade error, so this account's free tier cannot currently provide Malaysia or Hong Kong prices. The website will display this as a plan limitation.

The connector uses `/symbol_search` for owner data management and `/time_series?interval=1day&adjust=splits` for daily OHLCV. Daily values are split-adjusted by Twelve Data. See [Twelve Data's adjustment guidance](https://support.twelvedata.com/en/articles/5179064-are-the-prices-adjusted) and [historical API guide](https://support.twelvedata.com/en/articles/5656039-how-to-get-historical-prices).
