<?php
header('Content-Type: text/html; charset=UTF-8');
header('Cache-Control: no-store');
function esc($s) { return htmlspecialchars((string)$s, ENT_QUOTES, 'UTF-8'); }
function requestYahoo($url, $jar) {
 $ch=curl_init($url);
 curl_setopt_array($ch,[CURLOPT_RETURNTRANSFER=>true,CURLOPT_CONNECTTIMEOUT=>5,CURLOPT_TIMEOUT=>12,CURLOPT_FOLLOWLOCATION=>false,CURLOPT_COOKIEFILE=>$jar,CURLOPT_COOKIEJAR=>$jar,CURLOPT_USERAGENT=>'Mozilla/5.0',CURLOPT_HTTPHEADER=>['Accept: application/json, text/plain, */*']]);
 $body=curl_exec($ch); $status=curl_getinfo($ch,CURLINFO_HTTP_CODE); $error=curl_error($ch); curl_close($ch);
 return [$status,$body,$error];
}
?>
<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Yahoo session test</title>
<style>body{font:16px system-ui;max-width:950px;margin:40px auto;padding:0 20px}td,th{padding:12px;text-align:left;border-bottom:1px solid #ddd}button{padding:12px}.pass{color:green}.fail{color:#a32121}</style>
<h1>Yahoo cookie and token test</h1><p>This requests a temporary Yahoo session, obtains its token, and tries Apple fundamentals. Cookies and tokens are never displayed.</p>
<form method="post"><button name="run" value="1">Test Apple fundamentals with a session</button></form>
<?php
if($_SERVER['REQUEST_METHOD']==='POST' && isset($_POST['run'])) {
 if(!function_exists('curl_init')) echo '<p>PHP cURL unavailable.</p>';
 else {
  $jar=tempnam(sys_get_temp_dir(),'ml_yahoo_');
  if($jar===false) echo '<p>Cannot create temporary cookie storage on this host.</p>';
  else {
   try {
    echo '<p>Checked ' . esc(gmdate('Y-m-d H:i:s')) . ' UTC</p><table><tr><th>Step</th><th>HTTP</th><th>Result</th></tr>';
    [$s,$b,$e]=requestYahoo('https://fc.yahoo.com/',$jar);
    clearstatcache(true,$jar); $hasCookie=false;
    foreach(file($jar,FILE_IGNORE_NEW_LINES)?:[] as $line) { if($line!=='' && ($line[0]!=='#'||strpos($line,'#HttpOnly_')===0) && count(explode("\t",$line))>=7) $hasCookie=true; }
    echo '<tr><td>Session cookie</td><td>' . esc($s) . '</td><td>' . esc($e!==''?$e:($hasCookie?'Cookie received. This endpoint can return 404 while setting a cookie.':'No cookie stored; later steps may fail.')) . '</td></tr>';
    [$s,$token,$e]=requestYahoo('https://query1.finance.yahoo.com/v1/test/getcrumb',$jar);
    $valid=$s===200&&is_string($token)&&strlen(trim($token))>0&&strlen(trim($token))<128&&!preg_match('/[\s<>{}]/',trim($token));
    echo '<tr><td>Session token</td><td>' . esc($s) . '</td><td>' . esc($valid?'Token received (hidden).':($e!==''?$e:'No valid token received.')) . '</td></tr>';
    if($valid) {
     [$s,$body,$e]=requestYahoo('https://query1.finance.yahoo.com/v10/finance/quoteSummary/AAPL?modules=financialData,defaultKeyStatistics,summaryDetail&crumb=' . rawurlencode(trim($token)),$jar);
     $j=is_string($body)?json_decode($body,true):null; $r=$j['quoteSummary']['result'][0]??[]; $fields=[];
     foreach(['financialData','defaultKeyStatistics','summaryDetail'] as $module) foreach(($r[$module]??[]) as $field=>$value) if(is_array($value)&&isset($value['raw'])&&is_numeric($value['raw'])) $fields[]=$module . '.' . $field;
     $ok=$s===200&&count($fields)>0;
     $detail=$ok?'PASS — ' . count($fields) . ' numeric fundamental fields returned.':'FAIL — fundamentals were not returned.';
     // Do not display raw provider errors: they can include request credentials.
     if(!$ok&&$e!=='') $detail='FAIL — connection failed.';
     echo '<tr><td>Apple fundamentals</td><td>' . esc($s) . '</td><td class="' . ($ok?'pass':'fail') . '">' . esc($detail) . '</td></tr>';
    } else echo '<tr><td>Apple fundamentals</td><td>—</td><td>Skipped because the token step failed.</td></tr>';
    echo '</table>';
   } finally { if(is_file($jar)) unlink($jar); }
  }
 }
}
?>
<p>Run manually and avoid rapid repeats. A pass confirms this session flow works now; it does not guarantee ongoing access or data redistribution rights. Delete this diagnostic after testing.</p></html>
