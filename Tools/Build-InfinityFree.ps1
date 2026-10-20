param([string]$OutputPath = (Join-Path $PSScriptRoot '../dist/MarketLens-InfinityFree.zip'))
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$imageFolder = Join-Path $projectRoot 'InfinityFree/htdocs/assets/images'
New-Item -ItemType Directory -Path $imageFolder -Force | Out-Null
foreach ($image in @('marketlens-logo.svg', 'marketlens-logo-dark.svg', 'marketlens-mark.svg')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot "wwwroot/images/$image") -Destination (Join-Path $imageFolder $image) -Force
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'wwwroot/css/gold-theme.css') -Destination (Join-Path $projectRoot 'InfinityFree/htdocs/assets/gold-theme.css') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'wwwroot/js/theme.js') -Destination (Join-Path $projectRoot 'InfinityFree/htdocs/assets/theme.js') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'wwwroot/js/chart-hover.js') -Destination (Join-Path $projectRoot 'InfinityFree/htdocs/assets/chart-hover.js') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'wwwroot/js/catalogue-browser.js') -Destination (Join-Path $projectRoot 'InfinityFree/htdocs/assets/catalogue-browser.js') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'wwwroot/js/radar-pagination.js') -Destination (Join-Path $projectRoot 'InfinityFree/htdocs/assets/radar-pagination.js') -Force
$catalogue = Get-Content -LiteralPath (Join-Path $projectRoot 'Catalogue/company-catalogue-twelvedata.json') -Raw | ConvertFrom-Json
# Yahoo ticker conversion happens in PHP; Twelve Data's providerSymbol is never reused.
$compact = @($catalogue | ForEach-Object {
    $symbol = $_.symbol
    if ($symbol -match '^HK\.([0-9]+)$') { $symbol = 'HK.' + ([int]$Matches[1]).ToString('D5') }
    [ordered]@{ symbol=$symbol; name=$_.name; exchange=$_.exchange; instrumentType=$_.instrumentType }
})
$catalogueFolder = Join-Path $projectRoot 'InfinityFree/htdocs/app'
$legacyCatalogue = Join-Path $catalogueFolder 'catalogue.json'
if (Test-Path -LiteralPath $legacyCatalogue) { Remove-Item -LiteralPath $legacyCatalogue }
# Small parts stay well below InfinityFree's 10 MB limit for individual data files.
Get-ChildItem -LiteralPath $catalogueFolder -Filter 'catalogue-*.json' -File | ForEach-Object { Remove-Item -LiteralPath $_.FullName }
$popular = @('US.AAPL','US.MSFT','US.NVDA','US.GOOGL','US.AMZN','US.META','US.TSLA','US.VOO','MY.1155','SG.D05','HK.00700')
$leading = @($popular | ForEach-Object { $code = $_; $compact | Where-Object { $_.symbol -eq $code } })
$compact = @($leading) + @($compact | Where-Object { $_.symbol -notin $popular })
for ($offset = 0; $offset -lt $compact.Count; $offset += 25000) {
    $end = [Math]::Min($offset + 24999, $compact.Count - 1)
    $partPath = Join-Path $catalogueFolder ('catalogue-{0:D2}.json' -f [int]($offset / 25000))
    ConvertTo-Json -InputObject @($compact[$offset..$end]) -Depth 5 -Compress | Set-Content -LiteralPath $partPath -Encoding utf8NoBOM
}
$outputAbsolute = [System.IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Path (Split-Path $outputAbsolute -Parent) -Force | Out-Null
$root = Join-Path $projectRoot 'InfinityFree/htdocs'
$files = @(Get-ChildItem -LiteralPath $root -File -Recurse -Force | Where-Object {
    $_.FullName -notmatch '[\\/]storage[\\/]' -or $_.Name -in @('.htaccess','index.php')
})
foreach ($file in $files) {
    $limit = if ($file.Name -eq '.htaccess') { 10000 } elseif ($file.Extension -in @('.php','.html','.js')) { 1000000 } else { 10000000 }
    if ($file.Length -ge $limit) { throw "File exceeds the InfinityFree limit: $($file.FullName)" }
}
Add-Type -AssemblyName System.IO.Compression
if (Test-Path -LiteralPath $outputAbsolute) { Remove-Item -LiteralPath $outputAbsolute }
$archive = [System.IO.Compression.ZipFile]::Open($outputAbsolute, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in $files) {
        $relative = [System.IO.Path]::GetRelativePath($root, $file.FullName).Replace('\','/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$file.FullName,$relative,[System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $archive.Dispose() }
Write-Output "Packaged $($files.Count) files and $($compact.Count) listings: $outputAbsolute"
