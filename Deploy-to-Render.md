# Deploy MarketLens to Render Free

Repository: https://github.com/jianicheah/marketLens

## Current status

The project has Docker and Render configuration. All 28 C# checks and browser-favourites logic checks passed. Release publishing succeeded. HTTP smoke checks could not connect to the separately launched local process in this environment, so HTTP/UI verification and the actual Docker build remain unverified. The application has not been deployed to Render yet. Dashboard access was denied by the browser permission check, so account setup and the final deployment must be done by the user. No Docker executable is installed here; the Docker image build must be verified on Render.

## Upload to GitHub without losing subfolders

Git is installed on your laptop. Git uploads the folder structure automatically. The clean upload folder provided with this guide excludes App_Data (including encrypted account details), .vs, bin, obj, *.user and unused third-party browser libraries.

If GitHub authentication prevents the automatic push, open PowerShell in the clean upload folder and run `git push -u origin main`. Sign in to GitHub when Git requests it. Never paste passwords or access tokens into chat. The repository was empty when checked; no remote content was overwritten.

Alternatively, in GitHub's website choose Add file > Upload files. Drag the contents of the clean folder, including subfolders, into the upload area using Chrome or Edge. Enable hidden files in Explorer so .dockerignore and .gitignore are included. MarketLens.csproj, Dockerfile and render.yaml must be at the repository root. A ZIP file uploaded to GitHub is not automatically unpacked.

## Create the Render service

1. Sign in to https://dashboard.render.com in your browser.
2. Choose New > Blueprint and select jianicheah/marketLens. If needed, connect GitHub and permit access to this repository only.
3. Use `render.yaml` from the repository root. Review that it creates one Docker web service named marketlens with plan **Free**, no database and no disk.
4. Deploy the Blueprint and wait for the build and health check to succeed. Render builds the Dockerfile and supplies the public hostname.
5. Open the generated `https://...onrender.com` URL. This is the address to share or bookmark on your phone.

If you use New > Web Service instead, select Docker, the Free instance, Dockerfile path `./Dockerfile`, build context `.`, and health check `/health`. Set these environment variables:

| Key | Value |
|---|---|
| ASPNETCORE_ENVIRONMENT | Production |
| Hosting__PublicMode | true |
| PORT | 10000 |

Render supplies RENDER_EXTERNAL_HOSTNAME; do not enter localhost. Build and start command overrides should be left empty for Docker. Root directory should be empty if MarketLens.csproj is at the repository root.

## Check the deployed application

- `/health` returns OK.
- Search Apple or Maybank, fetch a listing and inspect source/quote/bar timestamps.
- Save a favourite, close the browser and reopen the same website address; it should remain.
- A separate browser/profile starts with separate favourites.
- File-import controls do not appear on the shared site. The Import POST returns 403 even if called directly.
- After an idle shutdown/restart, a favourite triggers a fresh public-data fetch. An old open form may need a page reload because temporary anti-forgery keys are recreated.
- If Yahoo refuses requests from the cloud IP, the page must show an unavailable-data message rather than fictional or invented data. Cloud-provider access still needs to be verified after deployment.

## How the hosted version differs

| Requirement | Implementation | Evidence |
|---|---|---|
| Accept Render traffic without changing local behaviour | Public mode allows the Render-provided hostname and binds PORT; local mode remains loopback-only | Hosting guard checks passed |
| Work without a persistent free disk | Bounded, temporary public-dataset cache; favourite links fetch data again | Storage/restart checks passed |
| Prevent shared visitor imports | Import UI hidden, POST blocked, public cache rejects imported/sample datasets | Cache rejection checks passed; HTTP verification unavailable in this environment |
| Keep user favourites independent | Browser localStorage, no shared server watchlist | Persistence, isolation and storage-failure checks passed |
| Deploy without uploading local account data | .gitignore, .dockerignore and clean upload exclude App_Data and generated files | Upload inventory checked before publishing |

The hosted preview has no website accounts, paid subscription or trading execution. It is accessible to anyone with the address. It uses rate limits and bounded caches for a small trial; it is not a production commercial investment service. Local Ollama is disabled in public mode because your laptop's model is not installed on Render. Source and calculation limitations remain visible.

Render Free sleeps after 15 minutes without visitors and can take about a minute to wake. Its filesystem is temporary and free Postgres expires after 30 days; this configuration creates no database. Free usage limits still apply. See https://render.com/docs/free and https://render.com/docs/docker.

The project currently uses .NET 9 to match your installed SDK. It is supported until 10 November 2026, so upgrade to .NET 10 LTS before that date. See https://dotnet.microsoft.com/en-us/platform/support/policy.
