# MarketLens for InfinityFree

This standalone PHP 8.1+ deployment uses Yahoo Finance; it does not run the .NET project or require Twelve Data credentials. Keep the Render deployment available until hosted acceptance checks pass.

## Upload

1. Run `Tools/Build-InfinityFree.ps1` from the repository root. It produces `dist/MarketLens-InfinityFree.zip` with the **contents** of htdocs, including dotfiles. The catalogue is derived from the existing reference list, but all prices use Yahoo ticker conversion; catalogue coverage is not verified Yahoo coverage.
2. Upload/extract the zip **inside your website's htdocs directory**. `index.php`, `assets`, `app`, `storage`, and `.htaccess` must be directly inside htdocs, not inside another nested folder. If File Manager cannot extract it, extract locally and upload those contents using FTP.
3. Back up any existing site before replacing its index file. Rename or remove the default `index.html` if it takes precedence over index.php. Do not upload the repository, .NET binaries, configuration secrets, test fixtures, or local PHP runtime.
4. Ensure `storage` is writable by PHP. Its `.htaccess` denies web access; do not remove it. Keep `.htaccess` in `app` too. The package excludes all local cookies, tokens, caches and lock files.
5. Visit the HTTPS domain. Open US.AAPL, MY.1155, SG.D05 and HK.00700, check charts and timeframe controls, try company search, favourites, JSON export, pagination and a second browser session.
6. Direct visits to `/storage/yahoo.cookies`, `/storage/index.php`, `/app/catalogue-00.json` and `/app/Yahoo.php` must return 403/404. If any private file is downloadable, stop using the site until access protection is fixed. Delete the earlier Yahoo diagnostic pages when finished.

## What is ported

- Searchable/paginated bundled stock and ETF catalogue, market filters, Yahoo company search, and direct market.ticker lookup.
- Yahoo daily history with currency and ticker validation, provider snapshot timestamps, completed-market-date filtering, adjustment factors, charts and explicit failures.
- Browser favourites, ownership-aware Sell wording, weeks/months trend rules, long-term rules with matching annual statements and sector checks, JSON export, and next-open historical strategy checks with per-side costs.
- Shared file caches, outbound request budget, provider backoff, session-cookie/token renewal after Yahoo 401/403, and CSRF protection for browser-triggered screening.
- Radar checks one listing per minute while a visitor has the research page open, with a 100-check UTC-day cap. Results expire after 24 hours. It pauses when no page is open; it cannot reproduce the .NET background worker on this host. Refresh to show newly saved results.

## Deliberate hosting differences and remaining acceptance

The PHP deployment has no local Ollama integration, owner sign-in/import UI, fictional demo, or Twelve Data one-minute polling. Those paths remain in the original .NET project; they are not exposed by this deployment. The Yahoo snapshot updates when an expired history is requested (one-hour cache), and is labelled unverified. No day-trading signals are supplied. Existing browser favourites from the old Render origin do not transfer automatically.

The summary-fundamentals test passing does not establish access to matching annual time series. Long-term screens abstain when annual data, statement currencies, current EPS, sector or valuation timestamps cannot be verified. Banks/insurers abstain. ETFs support price research, not the company fundamentals model. More obscure catalogue tickers may differ between providers and can legitimately fail.

No database, Composer, cron, Node, .NET or credentials are needed. PHP cURL and standard JSON/session/file extensions are required. Yahoo is unofficial and may rate limit or change endpoints. Data-use rights must be assessed separately from successful technical access.

## Local checks

Run `php Tests/InfinityFree/check.php` from the repository root. Lint all PHP files with `php -l`. To preview: `php -d extension=curl -S 127.0.0.1:8088 -t InfinityFree/htdocs` (adjust the PHP extension directory for your installation).

With cURL enabled, also run `php Tests/InfinityFree/pages.php browse`, `export`, `long`, and `csrf` as separate commands (replacing `browse` with each mode). These use a temporary fixture cache and do not contact Yahoo. Hosted access protection must be verified on Apache/InfinityFree: PHP's development server does not process `.htaccess`.

The development runtime, packaging output and cache state are ignored by Git. The .NET/Render provider configuration is deliberately preserved; the PHP site is Yahoo-only. Changes to shared .NET services must still pass the original regression suite.
