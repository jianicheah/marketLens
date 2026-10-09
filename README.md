# Render Free deployment

See [Deploy-to-Render.md](Deploy-to-Render.md) for hosting steps and the current verification status. The public version uses temporary memory caches, disables imports and local Ollama, and accepts only its configured Render hostname. The local version retains its existing behaviour.

# MarketLens — account-free market research

The website now retrieves public Yahoo Finance data directly. The Moomoo login flow, callback and connector have been removed. Visitors do not supply brokerage credentials, API keys or account details. The feed is unofficial and is suitable only as an availability-limited personal prototype; complete coverage of every listed company is not claimed.

## Start

1. Open `C:\MarketLens\MarketLens.csproj` in Visual Studio Community.
2. If an older version is running, stop it first. Choose the **http** launch profile and press **Ctrl+F5**.
3. Open `http://localhost:5206` and search **Maybank**, **Apple**, or **Tencent**.
4. Choose a listing and click **Fetch**. The backend retrieves daily prices, available annual company financials and a provider quote without redirecting to a login page.
5. Choose Weeks/months or Long-term and press **Analyse**. Select whether you already own the stock to clarify the meaning of Sell/Hold.

You can also enter an internal symbol directly: `MY.1155`, `US.AAPL`, `HK.00700`, `SG.D05`, `JP.7974`, `SH.600519`, `UK.TSCO`. The normalised market prefix maps to the provider's listing symbol, for example `MY.1155 → 1155.KL`. No brokerage connection is needed.

Terminal alternative:

```powershell
cd C:\MarketLens
dotnet restore --configfile NuGet.Config
dotnet run --launch-profile http
```

## Requirements and evidence

| Requirement | Changed behaviour | Code | Verification/status |
| --- | --- | --- | --- |
| No user brokerage login | Removed Moomoo sign-in button, OAuth callback and active connector; public requests send no credentials/cookies | `Program.cs`, `Pages/Data*`, `PublicMarketDataProvider` | Implemented; authenticated brokerage code removed; public C# live requests passed |
| Search listed companies | Name/ticker search finds company shares, filters out funds/indices and maps supported exchange suffixes | `OnGetSearchAsync`, `SearchAsync`, symbol conversion | Implemented; live Maybank search and ticker round-trips passed |
| Obtain international investment data | On-demand daily price history and available matched annual financials; caching and missing-data errors | `FetchAsync`, `ParseChart`, `ParseFundamentals` | Partially fulfils global coverage: 26 market-prefix mappings, not every listing/field; live US/MY/HK fetches passed |
| Visible analysis-only wording | Banner and footer contain **“This is AI analysis only.”** as an AI-feature notice, with information-only and trusted-platform text | `Pages/Shared/_Layout.cshtml` | Implemented; compiled views; browser verification tracked separately |
| Honest analysis results | Rule-based Buy/Hold/Sell, evidence timestamps, risks and insufficient-data gates remain; AI is optional | `AnalysisService`, `AiExplanationService` | Implemented; 25 automated checks passed; no local AI model configured |

## Market coverage

The adapter recognises US, MY, HK, SG, JP, SH, SZ, BJ, AU, CA, KR, UK, DE, FR, IN, TW, ID, TH, NZ, BR, MX, ZA, NL, IT, CH and ES market prefixes. It handles additional provider suffixes for supported secondary listings when returned by search. Recognition of a prefix does not guarantee that a listing or its financial data is available. Search is limited to the provider's returned company results, not a downloaded complete exchange catalogue.

Public responses can be delayed, rate-limited, incomplete or changed without notice. The system uses bounded requests, a five-minute price-data cache and a ten-minute company-search cache. Failures preserve saved datasets; no fictional fallback is used. A future public/paid version needs an appropriately licensed source with adequate exchange coverage and display/redistribution rights, plus a review of the requirements for public investment recommendations.

The provider is contacted only for company lookup, historical market data and public financial fields. Your ownership checkbox, watchlist and portfolio credentials are not sent. HTTP requests use a fixed provider host, no authentication header and no cookie jar. The backend does not log into Yahoo or a broker.

## What the results mean

- **Buy:** The disclosed baseline rules favour considering a purchase or additional exposure.
- **Sell:** Review reducing/exiting an existing holding; for a non-owner, avoid entry. It is not a short-selling signal.
- **Hold:** Retain the current position, or wait if you do not own it.
- **Insufficient data:** Required fields are missing, stale, unsupported or outside the model's assumptions.

These results are calculation-based research, not validated predictions or personalised suitability advice. AI explanations are generated only when an optional local model is configured and requested. The analysis-only wording does not mean the baseline rules are themselves an AI model.

### Weeks/months

Requires at least 60 completed, split-adjusted daily bars, usable volume, and a last completed bar within five calendar days. Today's unfinished candle is excluded; the last 60 observations must fit within 180 days. Exceptional holidays can cause abstention.

Buy requires close > SMA20 > SMA50, Wilder RSI14 between 45 and 70, and latest volume at least the prior 20-bar average. Sell requires close < SMA20 < SMA50 and RSI14 <45; otherwise Hold. ATR14 >8% of price causes abstention. Indicators, rule reasons and illustrative ATR stop/target references are displayed. Those references are not executable orders and are not used as backtest exits.

The public feed provides adjusted-close factors. Historical OHLC is normalised using those factors; adjustments may include dividends. Volume stays provider-reported. Quote prices remain in the provider's original quoted unit, which can include GBp (pence) or ZAc (cents). No currency conversion is implied.

### Long-term

The connector attempts annual revenue, net income, free cash flow, total debt, shareholder equity and trailing diluted EPS. It matches reporting dates/currencies, computes YoY growth and debt/equity, uses average annual equity for ROE, and uses the current provider quote divided by compatible trailing EPS for P/E. Missing periods, negative prior comparison bases, incompatible currencies, stale EPS or missing sector classification cause abstention rather than invented fields.

The generic screen uses positive revenue/earnings growth and FCF, debt/equity ≤1, ROE ≥12%, and positive EPS with P/E ≤25. Buy requires at least five of six criteria plus positive EPS and debt/equity ≤1. Sell triggers on nonpositive EPS with nonpositive FCF, or debt/equity >2 with declining earnings. Otherwise Hold. Reports older than 460 days and valuation timestamps older than five days are rejected.

Financial Services companies, including banks and insurers, are conservatively excluded from this generic model; they need sector-specific rules. The model is not an intrinsic-value estimate and has no point-in-time financial-statement backtest. Public financial data is not equivalent to an independently verified company filing.

### Day trading

Optional five-minute public bars can be fetched, but this public source supplies neither verified live bid/ask nor confirmed current regular-session status. Therefore **actionable day-trading recommendations return Insufficient data**. Real-time exchange-grade data is required to satisfy the existing spread/session/freshness gates. A daily recommendation is not a prediction of today's intraday movement.

## Historical strategy check

At least 90 completed daily bars are needed. The swing backtest makes a decision using data available at the prior close, executes at the next open and includes configured transaction costs per side. It is long-only, invests available cash, leaves the position unchanged on Hold, and liquidates any final position at the last close. Benchmark uses the same starting open/end close and costs. Maximum drawdown uses end-of-bar equity.

Adjusted-price series may embed provider dividend adjustments; separate cash dividends, taxes, interest, detailed slippage and market impact are not modelled. A historical win rate is not a confidence probability for today's result. Testing one series retrospectively does not establish independent predictive performance.

## Imports, favourites and exports

Data sources still supports validated CSV/JSON imports if a listing is unavailable. CSV header is `date,open,high,low,close,volume`. Use ascending YYYY-MM-DD dates, dot decimals, unique rows, valid positive OHLC and nonnegative volume. For swing analysis affirm split adjustment. Invalid imports preserve the prior dataset.

The fictional JSON template is under `wwwroot/templates/dataset.sample.json`; retain `isSample: true` for fictional data. For real data, replace every sample value, timestamp, currency and company identifier. Fundamental percentages use 12 to mean 12%; debt/equity uses a ratio. Financial fields need `periodEnd`, a separate current `valuationAsOf`, and the correct `isFinancialCompany` flag.

Datasets are stored under private `App_Data`. Favourites are stored per browser in localStorage, above market search; the old shared server watchlist is no longer used. No login/database is needed for this version. Clearing browser storage removes favourites; shared browsers share them; cross-device sync requires user accounts and persistent server storage. Analyses can be exported as JSON. The app remains localhost-only, with anti-forgery protection on POSTs. It does not execute trades or contain a multi-user public login system. .NET 9 is the currently installed SDK; use a supported framework before public deployment.

## Optional AI

If Ollama and a model are installed, set `LocalAi:Model` in `appsettings.json`, restart and click **Explain with local AI**. Only the research result is sent to `http://127.0.0.1:11434/api/generate`. The model receives no tools or brokerage credentials and cannot overwrite the computed signal. Generated text is labelled and can contain mistakes. No AI model is installed or configured by this change.

## C# learning map

- `Program.cs`: service registrations, HTTP clients and local access protections (like Laravel application/service bootstrap).
- `Pages/Index.cshtml.cs`: request handlers, company search and fetch (like controller actions).
- `Pages/Index.cshtml`: Razor view (like Blade).
- `PublicMarketDataProvider.cs`: symbol mapping, public requests, parsing and caching.
- `AnalysisService.cs` / `Indicators.cs`: recommendation rules and calculations.
- `BacktestService.cs`: historical execution/accounting.
- `DatasetStore.cs`: private local persistence.

## Verification

The application and checks compile successfully. **All 25 checks passed**, including no-credential requests, caching, ticker conversion, actual captured US/MY/HK responses, matched financial statements, missing fields, invalid/error responses, indicators, incomplete/stale data, import validation and backtest execution/costs.

The C# connector also fetched live public data successfully on 9 October 2026:

| Listing | Daily bars retrieved | Company financials |
| --- | --- | --- |
| Apple (`US.AAPL`) | 501 | Available |
| Maybank (`MY.1155`) | 490 | Available; bank is excluded from generic long-term rules |
| Tencent (`HK.00700`) | 493 | Incomplete/unavailable for the generic model |

Live company search found Maybank without authentication. These are integration-check observations, not recommendations. Coverage and data availability can change. The server started successfully, but this session's in-app browser timed out connecting to localhost; the final UI flow remains unverified. The verification server was stopped so Visual Studio can use the port. No local AI model has been exercised.

```powershell
dotnet restore Tests/MarketLens.Checks.csproj --configfile NuGet.Config
dotnet run --project Tests/MarketLens.Checks.csproj --no-restore
# Optional live integration check:
dotnet run --project Tests/MarketLens.Checks.csproj --no-restore -- --live
```

## Latest interface changes

| Requirement | Change | Verification |
|---|---|---|
| Clear, trustworthy source label | Main label: Market data via Yahoo Finance; unofficial connection details remain on Data sources. Existing stored labels are displayed consistently. | Razor build passed; source display reviewed |
| Understand evidence time | Daily analysis labels the provider bar timestamp, with a note that it may mark the session start; long-term analysis labels reporting-period end. Quote/retrieval times stay separate. | Razor build passed; existing incomplete-bar checks passed |
| Favourites above search | Removed saved-company list; favourite toggles on search results/details; per-browser localStorage, removal and cross-tab updates. | JavaScript syntax plus add/remove, reload, separate-browser and blocked-storage checks passed |

All 25 existing checks passed after these changes. Browser visual verification remains unavailable. Favourites are independent per browser, not per person using the same browser profile. The app remains a localhost prototype; this change does not deploy a public service or add accounts. Existing server watchlist files are preserved but not migrated into browser favourites automatically.