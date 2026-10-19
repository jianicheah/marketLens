<?php
header('Content-Type: text/html; charset=UTF-8');
header('Cache-Control: no-store');
function esc($s) { return htmlspecialchars((string)$s, ENT_QUOTES, 'UTF-8'); }
$tests = [];
foreach (['AAPL', '1155.KL', 'D05.SI', '0700.HK'] as $symbol) {
    $tests[] = ['2-year history: ' . $symbol, 'https://query1.finance.yahoo.com/v8/finance/chart/' . rawurlencode($symbol) . '?interval=1d&range=2y', 'history'];
}
$tests[] = ['Search: Maybank', 'https://query1.finance.yahoo.com/v1/finance/search?q=Maybank&quotesCount=5&newsCount=0', 'search'];
$tests[] = ['Fundamentals: Apple', 'https://query1.finance.yahoo.com/v10/finance/quoteSummary/AAPL?modules=financialData,defaultKeyStatistics,summaryDetail', 'fundamentals'];
?>
<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Yahoo feature tests</title><style>body{font:16px system-ui;max-width:1000px;margin:35px auto;padding:0 20px}td,th{padding:12px;border-bottom:1px solid #ddd;text-align:left}table{width:100%;border-collapse:collapse}button{padding:12px}.pass{color:green}.fail{color:#a32121}</style>
<h1>Yahoo feature tests from this host</h1><p>Six fixed requests: two years of daily history across four markets, company search, and company fundamentals. Run manually; avoid rapid repeats.</p>
<form method="post"><button name="run" value="1">Run six feature checks</button></form>
<?php
if ($_SERVER['REQUEST_METHOD'] === 'POST' && isset($_POST['run'])) {
 if (!function_exists('curl_init')) { echo '<p>PHP cURL unavailable.</p>'; }
 else {
 echo '<p>Checked ' . esc(gmdate('Y-m-d H:i:s')) . ' UTC</p><table><tr><th>Test</th><th>HTTP</th><th>Result</th></tr>';
 foreach ($tests as [$name, $url, $kind]) {
  $ch = curl_init($url);
  curl_setopt_array($ch, [CURLOPT_RETURNTRANSFER=>true,CURLOPT_CONNECTTIMEOUT=>5,CURLOPT_TIMEOUT=>10,CURLOPT_FOLLOWLOCATION=>false,CURLOPT_USERAGENT=>'Mozilla/5.0 MarketLens-Access-Test/1.0',CURLOPT_HTTPHEADER=>['Accept: application/json']]);
  $body=curl_exec($ch); $status=curl_getinfo($ch,CURLINFO_HTTP_CODE); $error=curl_error($ch); curl_close($ch);
  $j=is_string($body)?json_decode($body,true):null; $ok=false; $detail='No usable data returned.';
  if ($kind==='history') {
   $r=$j['chart']['result'][0]??[]; $count=0;
   foreach (($r['timestamp']??[]) as $i=>$time) { if(is_numeric($time)&&isset($r['indicators']['quote'][0]['close'][$i])&&is_numeric($r['indicators']['quote'][0]['close'][$i])) $count++; }
   $ok=$count>=200; $detail=$count . ' dated daily prices (minimum 200 for this diagnostic).';
  } elseif ($kind==='search') {
   $matches=[]; foreach (($j['quotes']??[]) as $q) { if(!empty($q['symbol'])) $matches[]=$q['symbol']; }
   $ok=count($matches)>0; $detail=$ok?'Matches: ' . implode(', ', $matches):'No stock matches.';
  } else {
   $r=$j['quoteSummary']['result'][0]??[]; $fields=[];
   foreach (['financialData','defaultKeyStatistics','summaryDetail'] as $module) { foreach (($r[$module]??[]) as $field=>$value) { if(is_array($value)&&isset($value['raw'])&&is_numeric($value['raw'])) $fields[]=$module . '.' . $field; } }
   $ok=count($fields)>0; $detail=count($fields) . ' numeric fundamental fields returned.';
  }
  $ok=$status===200&&$ok;
  $providerError=$j['chart']['error']['description']??$j['finance']['error']['description']??$j['quoteSummary']['error']['description']??null;
  if($error!=='') $detail=$error; elseif($providerError) $detail=substr((string)$providerError,0,240); elseif($status!==200) $detail='Request failed; HTTP ' . $status . '. May require authorization or be rate limited.';
  echo '<tr><td>' . esc($name) . '</td><td>' . esc($status) . '</td><td class="' . ($ok?'pass':'fail') . '">' . ($ok?'PASS — ':'FAIL — ') . esc($detail) . '</td></tr>';
 }
 echo '</table>';
 }
}
?>
<p>This tests endpoint availability only, not the complete MarketLens analysis pipeline. A fundamentals failure can mean Yahoo requires cookies/authorization; history can still work independently. Repeat later to assess consistency. Delete this page when finished.</p></html>
