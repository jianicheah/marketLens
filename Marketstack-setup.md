# Enable the free personal daily-data version

The connector has been implemented, but it has not yet retrieved live Marketstack data: an account API key is required. Keep the website on Render; it is the data connector that changes.

## 1. Configure Render before uploading the code update

Create a **Free** account at https://marketstack.com/pricing and get the API access key from your account dashboard. Do not select a paid plan. The published Free plan lists 100 requests per month, end-of-day data and one year of history. This is a limited personal prototype; it does not enable public commercial use.

In your existing Render service, open **Environment** and add:

| Key | Value |
|---|---|
| MarketData__Provider | Marketstack |
| MarketData__MarketstackApiKey | Your actual provider key |
| Hosting__OwnerPassword | Your own unique password, at least 12 characters |

Keep `Hosting__PublicMode=true` and the existing Production settings. Save these settings before pushing the update, so the new application can start. Never put either secret in appsettings.json, GitHub, screenshots or this chat. The hosted personal version intentionally fails startup if its owner password is missing.

## 2. Upload the prepared changes

The updated source is in the same clean local Git repository used for your original upload. Open PowerShell and run:

```powershell
Set-Location "C:\Users\QYNIX145\Documents\Codex\2026-10-09\c\outputs\MarketLens-upload"
git push origin main
```

Render should redeploy if automatic deployment is enabled. Otherwise choose Manual Deploy > Deploy latest commit. Do not upload a ZIP and expect GitHub to unpack it.

## 3. Test from your phone

Open the existing HTTPS website address. The browser asks for website owner credentials:

- Username: `owner`
- Password: the Hosting__OwnerPassword value you set in Render.

This is your website login, not a brokerage login. Search **Apple**, click **Fetch**, and confirm the source says **Daily market data via Marketstack**. Save a favourite and reopen it the next day in the same browser profile. A restart can clear server data, so a favourite link fetches its history again.

## What works and what is limited

- Search is a local 10-stock catalogue: AAPL, MSFT, NVDA, AMZN, GOOGL, META, TSLA, JPM, V and WMT. This avoids spending API requests on searches. It is not all US companies.
- An uncached price fetch requests one year of daily history for one stock through the documented v2 EOD endpoint. History is cached for 24 hours while the server process remains alive. Render sleep/restarts clear it; the provider's monthly quota remains enforced.
- Daily charts and existing weeks/months rules run when enough complete adjusted OHLC is returned. If adjustment data is missing, raw prices can be shown but trend signals and backtests abstain.
- Live quotes, day trading, long-term fundamentals, Malaysia and other markets are not supported by this connector.
- Rate/quota errors produce clear messages and a cooldown; there is no hidden fallback to Yahoo and no fictional substitute.
- API keys stay on the server. HttpClient logging for this provider is disabled because its documented authentication puts the key in the request URL.
- Owner access protects your personal website and quota. Do not share its credentials; broader sharing needs a provider plan that permits the intended use.

## Verification

All **34 C# checks passed**: existing analysis/backtest checks plus Marketstack response-schema fixtures, trading-date mapping, local search, missing-key rejection, unsupported markets, price caching, quota cooldown and owner credential checks. The new fixtures are synthetic examples based on the documented schema, not live provider responses. The app builds with zero warnings/errors. Browser/HTTP and actual cloud API retrieval remain unverified here. Live testing requires your key and the deployed update.

Sources: https://marketstack.com/pricing, https://www.postman.com/apilayer/apilayer/documentation/k0f6wqp/marketstack, https://blog.apilayer.com/introducing-marketstack-v2-api/
