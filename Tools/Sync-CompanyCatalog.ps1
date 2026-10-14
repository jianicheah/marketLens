param(
    [ValidateRange(1, 1000)][int]$MaxRequests = 5,
    [ValidateRange(1, 1000)][int]$PageSize = 1000
)
$ErrorActionPreference = 'Stop'
# Run on the project machine. The key stays in the process environment, never in output/files.
$apiKey = [Environment]::GetEnvironmentVariable('MarketData__MarketstackApiKey')
if ([string]::IsNullOrWhiteSpace($apiKey)) { throw 'Set MarketData__MarketstackApiKey in this process environment first.' }
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dataFolder = Join-Path $projectRoot 'App_Data'
$catalogueFolder = Join-Path $projectRoot 'Catalogue'
$cataloguePath = Join-Path $catalogueFolder 'company-catalogue.json'
$statePath = Join-Path $dataFolder 'company-catalogue-sync.json'
$markets = @{}
$directorySource = Get-Content -LiteralPath (Join-Path $projectRoot 'Services/MarketDirectory.cs') -Raw
foreach ($entry in [regex]::Matches($directorySource, 'new\("([A-Z0-9]+)", "([A-Z]+)",')) {
    $markets[$entry.Groups[1].Value] = $entry.Groups[2].Value
}
if ($markets.Count -eq 0) { throw 'No project exchange mappings found.' }
$companies = @{}
if (Test-Path -LiteralPath $cataloguePath) {
    foreach ($company in @(Get-Content -LiteralPath $cataloguePath -Raw | ConvertFrom-Json)) {
        $companies[$company.symbol] = $company
    }
}
$offset = 0
if (Test-Path -LiteralPath $statePath) {
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    if ($state.complete) { Write-Output "Catalogue already synchronized: $($companies.Count) saved listings. No requests sent."; return }
    $offset = [int]$state.nextOffset
}
New-Item -ItemType Directory -Path $dataFolder -Force | Out-Null
New-Item -ItemType Directory -Path $catalogueFolder -Force | Out-Null
for ($requestNumber = 0; $requestNumber -lt $MaxRequests; $requestNumber++) {
    $url = 'https://api.marketstack.com/v1/tickers?access_key=' + [Uri]::EscapeDataString($apiKey) + '&limit=' + $PageSize + '&offset=' + $offset
    try { $response = Invoke-RestMethod -Uri $url -TimeoutSec 45 }
    catch { throw 'Provider directory request failed. Existing catalogue/progress retained. Check connection, plan access and quota.' }
    if ($response.error -or $null -eq $response.data -or $null -eq $response.pagination) {
        throw 'Provider returned no valid directory page. Existing catalogue/progress retained.'
    }
    $rows = @($response.data)
    $total = [int]$response.pagination.total
    if ($rows.Count -eq 0 -and $offset -lt $total) { throw 'Provider returned an empty page before the reported end. Progress retained.' }
    foreach ($row in $rows) {
        $mic = [string]$row.stock_exchange.mic
        if (!$markets.ContainsKey($mic) -or $row.has_eod -eq $false) { continue }
        $ticker = ([string]$row.symbol).ToUpperInvariant()
        $name = [string]$row.name
        if ($ticker -cnotmatch '^[A-Z0-9][A-Z0-9.\-]{0,24}$' -or [string]::IsNullOrWhiteSpace($name) -or $name.Length -gt 150) { continue }
        $symbol = $markets[$mic] + '.' + $ticker
        $currency = $row.currency
        if (!$currency) { $currency = $row.stock_exchange.currency }
        $companies[$symbol] = [ordered]@{
            symbol = $symbol; providerSymbol = $ticker; name = $name
            exchange = [string]$row.stock_exchange.name; exchangeMic = $mic; currency = $currency
        }
    }
    $offset += $rows.Count
    $complete = $offset -ge $total
    $snapshot = @($companies.Values | Sort-Object { $_.name })
    # Catalogue first: interruption before checkpoint only repeats the last page safely.
    ConvertTo-Json -InputObject $snapshot -Depth 8 | Set-Content -LiteralPath ($cataloguePath + '.tmp') -Encoding UTF8
    Move-Item -LiteralPath ($cataloguePath + '.tmp') -Destination $cataloguePath -Force
    [ordered]@{ nextOffset = $offset; totalProviderRows = $total; complete = $complete; updatedAt = [DateTimeOffset]::UtcNow.ToString('O') } |
        ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding UTF8
    Write-Output "Saved $($companies.Count) eligible listings; scanned $offset of $total provider rows; complete=$complete."
    if ($complete) { break }
}
Write-Output 'Restart the app to load the saved catalogue. Run again to resume an incomplete sync. Deploy Catalogue/company-catalogue.json with the website.'
