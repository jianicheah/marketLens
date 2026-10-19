<?php
// Self-contained fixture-backed page checks; production cache is never changed.
declare(strict_types=1);
require __DIR__.'/../../InfinityFree/htdocs/app/Yahoo.php';
$temp=__DIR__.'/.cache-'.bin2hex(random_bytes(8));
if(!mkdir($temp)) throw new RuntimeException('Cannot create test cache');
define('ML_STORAGE',$temp);
register_shutdown_function(function()use($temp){ foreach(glob($temp.'/*')?:[] as $file) if(is_file($file)) unlink($file); rmdir($temp); });
$readFixture=fn($name)=>json_decode(file_get_contents(__DIR__.'/../Fixtures/'.$name.'.json'),true,512,JSON_THROW_ON_ERROR);
$data=Yahoo::parseChart($readFixture('yahoo-aapl'),'US.AAPL',1791547200);
foreach(['history:US.AAPL'=>$data,'fundamentals:US.AAPL'=>['assetProfile'=>['sector'=>'Technology']],'statements:US.AAPL'=>$readFixture('yahoo-fundamentals')] as $key=>$value) file_put_contents($temp.'/'.hash('sha256',$key).'.json',json_encode($value,JSON_THROW_ON_ERROR));
$mode=$argv[1]??'browse';
$_SERVER['REQUEST_METHOD']='GET';
if($mode==='browse') $_GET=['q'=>'Maybank','market'=>'MY'];
elseif($mode==='export') $_GET=['symbol'=>'US.AAPL','action'=>'export'];
elseif($mode==='long') $_GET=['symbol'=>'US.AAPL','horizon'=>'long'];
elseif($mode==='csrf') { $_GET=['action'=>'screen']; $_SERVER['REQUEST_METHOD']='POST'; }
else throw new RuntimeException('Unknown mode');
if($mode==='export') {
 ob_start(function($body){
  $j=json_decode($body,true,512,JSON_THROW_ON_ERROR);
  if(($j['data']['symbol']??null)!=='US.AAPL'||empty($j['data']['bars'])||!isset($j['backtest']['costBps'])||isset($j['token'])) throw new RuntimeException('Export response failed validation');
  return "PASS: JSON export includes history, analysis and backtest without session credentials\n";
 });
} elseif($mode==='csrf') {
 ob_start(function($body){ if(http_response_code()!==403||!isset(json_decode($body,true)['error'])) throw new RuntimeException('CSRF check failed'); return "PASS: screening rejects requests without a session token\n"; });
} else {
 ob_start(function($body)use($mode){
  $required=$mode==='browse'?['MY.1155','Malayan Banking']:['Apple Inc.','Investment analysis','Long-term','Insufficient data'];
  foreach($required as $term) if(!str_contains($body,$term)) throw new RuntimeException('Missing page text: '.$term);
  return 'PASS: '.$mode." page renders expected content\n";
 });
}
require __DIR__.'/../../InfinityFree/htdocs/index.php';
