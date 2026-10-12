# Marketstack connection and visitor access

The connector was checked against live provider responses on 12 October 2026: `/v1/tickers` returned Apple listing metadata, and `/v2/eod` returned daily prices. The former `/v2/tickers` request returned HTTP 404 and has been corrected. No API key is stored in this repository.

## Connection settings

Create a Free account at https://marketstack.com/pricing/ and obtain your API key. The Free plan lists 100 monthly requests, daily prices, one year of history and Non-Commercial Use. Commercial display requires suitable provider rights.

In Render → your web service → Environment, retain:

| Setting | Value |
|---|---|
| MarketData__Provider | Marketstack |
| MarketData__MarketstackApiKey | Your actual API key |
| Hosting__OwnerPassword | Your private website-owner password, at least 12 characters |
| Hosting__PublicMode | true |

The API key and owner password are different secrets. Never put either in GitHub. Render stores these settings across deployments.

For local development, the project is linked to its User Secrets store. Save the key with `dotnet user-secrets set "MarketData:MarketstackApiKey" "YOUR_KEY"` from the project directory, or edit Manage User Secrets in Visual Studio. The Development launch profile loads that setting automatically. Local management accepts loopback requests only and requires no sign-in.

## Upload the update

Restart the local app to use the changes. The clean upload repository is prepared locally:

```powershell
Set-Location "C:\Users\QYNIX145\Documents\Codex\2026-10-09\c\outputs\MarketLens-upload"
git push origin main
```

Render can auto-deploy the pushed commit. Otherwise select Manual Deploy → Deploy latest commit.

## Owner management and visitors

Visitors open the main website without a shared username or password. The homepage immediately lists Apple, Microsoft, NVIDIA, Amazon, Alphabet, Meta, Tesla, JPMorgan Chase, Visa and Walmart. Search filters this website list by name or ticker without provider requests. Opening a listed company fetches daily prices if needed, then shares the 24-hour cache across visitors. Prices are not claimed to be available until retrieval succeeds. Favourites are saved in each browser.

To manage data on Render, use **Owner sign in** at `/Owner`:

- Username: `owner` unless overridden by `Hosting__OwnerUser`.
- Password: the value of `Hosting__OwnerPassword`.

Apple needs no manual publishing: select Apple on the homepage. To add more companies, the owner can use **Manage data → Find and check companies**. A successful daily-price check adds the listing to the homepage catalogue for this server session. Provider directory searches remain owner-only. Owner credentials are never shared with visitors.

Provider searches and uncached histories consume the shared API quota. Anonymous fetching is restricted to companies in the website list; arbitrary symbols are rejected before a provider call. Failed visitor fetches back off for five minutes. The cache holds up to 128 checked companies for up to 24 hours. Checked company names remain listed after their prices expire within the server session. Server restarts clear prices and owner-added listings; the ten built-in companies remain visible. Visitor favourites remain saved. Quota exhaustion or provider errors can still prevent loading prices; the UI reports the error and keeps the company list visible. This version does not include every company worldwide.

## Price quality and verification limits

Daily-price rows are matched to the requested symbol/exchange. The parser retains a complete recent segment, stopping at the first missing or invalid price/volume bar. A warning explains the shortened history. It never fabricates prices or joins across an explicit data gap. If the latest adjusted bar is unavailable, complete raw prices may be shown with trend analysis withheld; if the latest raw bar is also incomplete, the request is rejected.

The live Apple response contained 250 rows, including null closes and missing adjusted OHLC. Its complete recent adjusted segment contained 84 bars. That is enough for the existing 60-bar swing screen, but below the 90-bar backtest threshold. No day-trading or fundamental long-term analysis is supplied by this connector.

Application and test projects compile. The 46-check suite includes history-gap and owner-route regression checks, but has not been confirmed passing: an earlier executable attempt was blocked by Windows Application Control. Browser sign-in, the updated app's complete fetch/analysis flow and the latest Render deployment remain unverified. Live provider connectivity is verified independently of the UI.

Sources: https://marketstack.com/pricing/, https://marketstack-wp.apilayer.green/documentation/
