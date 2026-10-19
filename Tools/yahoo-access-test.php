<?php
// Upload this standalone diagnostic to InfinityFree's htdocs directory.
header('Content-Type: text/html; charset=UTF-8');
header('Cache-Control: no-store');
function escape($value) { return htmlspecialchars((string)$value, ENT_QUOTES, 'UTF-8'); }
$symbols = ['AAPL' => 'Apple (US)', '1155.KL' => 'Maybank (Malaysia)', '0700.HK' => 'Tencent (Hong Kong)'];
?>
<!doctype html>
<html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>MarketLens — Yahoo access test</title>
<style>body{font:16px system-ui;max-width:1000px;margin:40px auto;padding:0 20px;color:#172235}table{border-collapse:collapse;width:100%}th,td{padding:12px;text-align:left;border-bottom:1px solid #ddd}button{padding:12px 18px;cursor:pointer}.pass{color:#08753e}.fail{color:#a32121}small{color:#526070}</style>
<h1>Yahoo access from this hosting server</h1>
<p>This checks three fixed Yahoo Finance price-history requests from PHP on your host. A pass requires HTTP 200 and valid dated prices.</p>
<form method="post"><button name="run" value="1">Run three Yahoo checks</button></form>
<?php
if ($_SERVER['REQUEST_METHOD'] === 'POST' && isset($_POST['run'])) {
    if (!function_exists('curl_init')) {
        echo '<p class="fail">PHP cURL is unavailable on this host.</p>';
    } else {
        echo '<p>Checked at ' . escape(gmdate('Y-m-d H:i:s')) . ' UTC</p>';
        echo '<table><tr><th>Listing</th><th>HTTP</th><th>Result</th></tr>';
        foreach ($symbols as $symbol => $name) {
            $handle = curl_init('https://query1.finance.yahoo.com/v8/finance/chart/' . rawurlencode($symbol) . '?interval=1d&range=1mo');
            curl_setopt_array($handle, [CURLOPT_RETURNTRANSFER => true, CURLOPT_CONNECTTIMEOUT => 8, CURLOPT_TIMEOUT => 15, CURLOPT_FOLLOWLOCATION => false, CURLOPT_USERAGENT => 'Mozilla/5.0 MarketLens-Access-Test/1.0', CURLOPT_HTTPHEADER => ['Accept: application/json']]);
            $body = curl_exec($handle);
            $status = curl_getinfo($handle, CURLINFO_HTTP_CODE);
            $error = curl_error($handle);
            curl_close($handle);
            $json = is_string($body) ? json_decode($body, true) : null;
            $result = $json['chart']['result'][0] ?? null;
            $timestamps = $result['timestamp'] ?? [];
            $closes = $result['indicators']['quote'][0]['close'] ?? [];
            $valid = [];
            foreach ($timestamps as $i => $timestamp) {
                if (is_numeric($timestamp) && isset($closes[$i]) && is_numeric($closes[$i])) $valid[] = ['time' => (int)$timestamp, 'close' => $closes[$i]];
            }
            $passed = $status === 200 && ($result['meta']['symbol'] ?? '') === $symbol && count($valid) > 0;
            if ($passed) {
                $last = $valid[count($valid) - 1];
                $message = 'PASS — ' . count($valid) . ' dated prices; latest ' . gmdate('Y-m-d', $last['time']) . ', close ' . $last['close'] . ' ' . ($result['meta']['currency'] ?? '');
            } elseif ($error !== '') {
                $message = 'FAIL — connection error: ' . $error;
            } elseif ($status === 429) {
                $message = 'FAIL — Yahoo rate limited this request (429).';
            } elseif ($status === 401 || $status === 403) {
                $message = 'FAIL — request denied (' . $status . ').';
            } else {
                $description = $json['chart']['error']['description'] ?? 'No matching, valid dated prices returned; response may be HTML or a provider error.';
                $message = 'FAIL — ' . substr((string)$description, 0, 240);
            }
            echo '<tr><td>' . escape($name . ' — ' . $symbol) . '</td><td>' . escape($status) . '</td><td class="' . ($passed ? 'pass' : 'fail') . '">' . escape($message) . '</td></tr>';
        }
        echo '</table>';
    }
}
?>
<p><small>A successful check confirms access at this moment only. Repeat manually later; avoid rapid refreshes. This does not verify redistribution permission, all exchanges, or compatibility with the full .NET MarketLens application. Delete this diagnostic after testing.</small></p>
</html>
