param([string]$BaseUrl = 'http://localhost:5206')
$ErrorActionPreference = 'Stop'
$root = [Uri]($BaseUrl.TrimEnd('/') + '/')
$pending = [Collections.Generic.Queue[Uri]]::new()
$pending.Enqueue($root)
$visited = [Collections.Generic.HashSet[string]]::new()
$companies = [Collections.Generic.Dictionary[string,Uri]]::new()
# Discover the website's actual catalogue, including subsequent pages.
while ($pending.Count -gt 0) {
    $page = $pending.Dequeue()
    if (!$visited.Add($page.AbsoluteUri)) { continue }
    $html = (Invoke-WebRequest $page.AbsoluteUri -UseBasicParsing -TimeoutSec 35).Content
    foreach ($anchor in [regex]::Matches($html, '<a\b[^>]*>.*?</a>', 'Singleline')) {
        $href = [regex]::Match($anchor.Value, 'href="([^"]+)"').Groups[1].Value
        if (!$href) { continue }
        $url = [Uri]::new($root, [Net.WebUtility]::HtmlDecode($href))
        if ($url.Authority -ne $root.Authority) { continue }
        if ($anchor.Value.Contains('company-view')) {
            $symbol = [regex]::Match([Uri]::UnescapeDataString($url.Query), '(?:\?|&)Symbol=([^&]+)').Groups[1].Value
            if ($symbol) { $companies[$symbol] = $url }
        } elseif ($anchor.Value.Contains('Next companies')) { $pending.Enqueue($url) }
    }
}
if (!$companies.Count) { throw 'The website returned no company links.' }
$results = foreach ($entry in $companies.GetEnumerator() | Sort-Object Key) {
    try {
        $html = (Invoke-WebRequest $entry.Value.AbsoluteUri -UseBasicParsing -TimeoutSec 35).Content
        $name = [Net.WebUtility]::HtmlDecode([regex]::Match($html, '<h2 class="company-title">(.*?)</h2>', 'Singleline').Groups[1].Value)
        $signal = [regex]::Match($html, '<div class="assessment">.*?<h3>(.*?)</h3>', 'Singleline').Groups[1].Value
        $errors = @([regex]::Matches($html, '<aside class="notice">(.*?)</aside>', 'Singleline') | ForEach-Object { [Net.WebUtility]::HtmlDecode(($_.Groups[1].Value -replace '<[^>]*>', ' ' -replace '\s+', ' ')) })
        $status = if (!$name -or !$signal -or $errors.Count) { 'Failed' } elseif ($signal -eq 'Insufficient data') { 'Limited history' } else { 'Ready' }
        [pscustomobject]@{ Symbol = $entry.Key; Company = $name; Status = $status; Signal = $signal; Chart = $html.Contains('class="price-chart"'); Errors = $errors -join ' | ' }
    } catch {
        [pscustomobject]@{ Symbol = $entry.Key; Company = ''; Status = 'Failed'; Signal = ''; Chart = $false; Errors = $_.Exception.GetType().Name }
    }
}
$results | Format-Table Symbol, Status, Signal, Chart -AutoSize
$results | Where-Object Status -eq 'Failed' | Format-List Symbol, Errors
Write-Output ('Checked {0} companies: {1} ready, {2} with limited history, {3} failed.' -f $results.Count, @($results | Where-Object Status -eq 'Ready').Count, @($results | Where-Object Status -eq 'Limited history').Count, @($results | Where-Object Status -eq 'Failed').Count)
if (@($results | Where-Object Status -eq 'Failed').Count) { exit 1 }
