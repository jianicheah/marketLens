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

This is your website login, not a brokerage login. Choose **Apple** under Popular companies, click **View**, and confirm the source says **Daily market data via Marketstack**. Save a favourite and reopen it the next day in the same browser profile. A restart can clear server data, so a favourite link fetches its history again.

## What works and what is limited

- The landing page shows popular US company shortcuts without an API call. These are suggestions, not a search restriction. Browse exchange filters or search the provider directory for more listings. Listing metadata does not establish price access: the UI marks prices as unchecked until retrieval succeeds. Filters include Malaysia and other international exchanges, but their plan coverage has not been live verified.
- Uncached directory browsing and searches also consume requests. Directory results are cached for 24 hours. An uncached price fetch requests one year of daily history for one stock through the documented v2 EOD endpoint. History is cached for 24 hours while the server process remains alive. Render sleep/restarts clear it; the provider's monthly quota remains enforced.
- Daily charts and existing weeks/months rules run when enough complete adjusted OHLC is returned. If adjustment data is missing, raw prices can be shown but trend signals and backtests abstain.
- Live quotes, day trading and long-term fundamentals are unavailable. International listings can be retrieved if the provider returns a matching listing and valid daily history with a recognised currency/unit.
- Rate/quota errors produce clear messages and a cooldown; there is no hidden fallback to Yahoo and no fictional substitute.
- API keys stay on the server. HttpClient logging for this provider is disabled because its documented authentication puts the key in the request URL.
- Owner access protects your personal website and quota. Do not share its credentials; broader sharing needs a provider plan that permits the intended use.

## Verification

The earlier 34-check suite passed before this browsing update. This update adds four synthetic-fixture checks for international listing parsing, directory caching, non-starter stock retrieval, pagination/filter forwarding, international currency/date handling and invalid filters. The updated application and test project compile successfully. Running the updated test executable is blocked by this laptop's Application Control policy (0x800711C7); the 38-check suite has not been confirmed passing. Browser rendering and live Marketstack coverage are also unverified. No API key was used during development.

Sources: https://marketstack.com/pricing, https://www.postman.com/apilayer/apilayer/documentation/k0f6wqp/marketstack, https://blog.apilayer.com/introducing-marketstack-v2-api/
