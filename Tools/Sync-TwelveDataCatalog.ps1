param([string]$SourcePath, [string]$DestinationPath)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$catalogueFolder = if ($DestinationPath) { [IO.Path]::GetFullPath($DestinationPath) } else { Join-Path $projectRoot 'Catalogue' }
if (!$SourcePath) {
    $dataFolder = Join-Path $projectRoot 'App_Data'
    New-Item -ItemType Directory -Path $dataFolder -Force | Out-Null
    $SourcePath = Join-Path $dataFolder 'twelve-directory.json'
    # Public reference data: no key or price-history requests are sent.
    Invoke-WebRequest -Uri 'https://api.twelvedata.com/stocks?format=JSON' -OutFile $SourcePath -UseBasicParsing -TimeoutSec 60
}
$response = Get-Content -LiteralPath $SourcePath -Raw | ConvertFrom-Json
if ($response.status -ne 'ok' -or $null -eq $response.data) { throw 'Twelve Data did not return a valid stock directory. Existing catalogue retained.' }
$markets = @{}
$directorySource = Get-Content -LiteralPath (Join-Path $projectRoot 'Services/MarketDirectory.cs') -Raw
foreach ($entry in [regex]::Matches($directorySource, 'new\("([A-Z0-9]+)", "([A-Z]+)",')) {
    $markets[$entry.Groups[1].Value] = $entry.Groups[2].Value
}
if ($markets.Count -eq 0) { throw 'No project exchange mappings found.' }
$companies = @{}
$stockTypes = @('Common Stock', 'Preferred Stock', 'American Depositary Receipt', 'Global Depositary Receipt', 'Depositary Receipt', 'REIT', 'Limited Partnership')
foreach ($row in $response.data) {
    $mic = [string]$row.mic_code
    if (!$markets.ContainsKey($mic) -or $row.type -notin $stockTypes) { continue }
    $ticker = ([string]$row.symbol).ToUpperInvariant()
    $name = [string]$row.name
    if ($ticker -cnotmatch '^[A-Z0-9][A-Z0-9.\-]{0,24}$' -or [string]::IsNullOrWhiteSpace($name) -or $name.Length -gt 150) { continue }
    $symbol = $markets[$mic] + '.' + $ticker
    if ($companies.ContainsKey($symbol)) { continue }
    $companies[$symbol] = [ordered]@{
        symbol = $symbol; providerSymbol = $ticker; name = $name
        exchange = [string]$row.exchange; exchangeMic = $mic; currency = [string]$row.currency
    }
}
if ($companies.Count -eq 0) { throw 'No eligible stocks were found. Existing catalogue retained.' }
$baselinePath = Join-Path $projectRoot 'Catalogue/company-catalogue-twelvedata.json'
if (Test-Path -LiteralPath $baselinePath) {
    $baseline = @(Get-Content -LiteralPath $baselinePath -Raw | ConvertFrom-Json)
    if ($companies.Count -lt $baseline.Count * 0.8) { throw 'Directory unexpectedly lost over 20% of listings. Existing catalogue retained.' }
    $newCounts = @{}
    foreach ($group in ($companies.Values | Group-Object { $_.symbol.Split('.')[0] })) { $newCounts[$group.Name] = $group.Count }
    foreach ($group in ($baseline | Group-Object { $_.symbol.Split('.')[0] })) {
        if ([int]$newCounts[$group.Name] -lt $group.Count * 0.8) { throw ('Market ' + $group.Name + ' unexpectedly lost over 20% of listings. Existing catalogue retained.') }
    }
}
New-Item -ItemType Directory -Path $catalogueFolder -Force | Out-Null
$cataloguePath = Join-Path $catalogueFolder 'company-catalogue-twelvedata.json'
$snapshot = @($companies.Values | Sort-Object { $_.name })
$json = (ConvertTo-Json -InputObject $snapshot -Depth 6).Replace("`r`n", "`n") + "`n"
[IO.File]::WriteAllText(($cataloguePath + '.tmp'), $json, [Text.UTF8Encoding]::new($false))
Move-Item -LiteralPath ($cataloguePath + '.tmp') -Destination $cataloguePath -Force
$manifest = [ordered]@{
    sha256 = (Get-FileHash -LiteralPath $cataloguePath -Algorithm SHA256).Hash.ToLowerInvariant()
    count = $companies.Count
    checkedAt = [DateTimeOffset]::UtcNow.ToString('O')
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $catalogueFolder 'manifest.json') -Encoding UTF8
Write-Output "Saved $($companies.Count) distinct stock listing codes from $(@($response.data).Count) directory records."
$snapshot | Group-Object { $_.symbol.Split('.')[0] } | Sort-Object Name | Select-Object Name, Count
Write-Output 'Restart or deploy the app to load this catalogue. This is listing metadata; price access still depends on your plan.'
