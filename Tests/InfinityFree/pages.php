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
elseif($mode==='catalogue') $_GET=['action'=>'catalogue','market'=>'KR','page'=>'2','symbol'=>'US.AAPL'];
elseif(str_starts_with($mode,'radar-page')) {
 $requested=$mode==='radar-page-last'?3:($mode==='radar-page-clamp'?999:2);
 $_GET=['action'=>'radar-page','radarPage'=>(string)$requested,'symbol'=>'US.AAPL','market'=>'MY']; $radar=[];
 for($i=1;$i<=25;$i++) $radar[]=['symbol'=>'US.TEST'.$i,'name'=>'Fixture '.$i,'currency'=>'USD','signal'=>'Hold','checkedAt'=>time(),'prices'=>[10,11],'lastDate'=>'2026-10-16'];
 $radar[]=['signal'=>'Insufficient data','checkedAt'=>time(),'name'=>'Hidden fixture'];
 file_put_contents($temp.'/'.hash('sha256','radar').'.json',json_encode($radar,JSON_THROW_ON_ERROR));
}
elseif($mode==='radar') {
 $_GET=[]; $radar=[];
 foreach(['Buy','Sell','Hold','Insufficient data'] as $signal) {
  $radar[]=['symbol'=>'US.TEST','name'=>$signal==='Insufficient data'?'Unavailable radar fixture':'Valid radar '.$signal.' fixture','currency'=>'USD','signal'=>$signal,'checkedAt'=>time(),'prices'=>[10,11],'lastDate'=>'2026-10-16'];
 }
 file_put_contents($temp.'/'.hash('sha256','radar').'.json',json_encode($radar,JSON_THROW_ON_ERROR));
}
else throw new RuntimeException('Unknown mode');
if(str_starts_with($mode,'radar-page')) {
 ob_start(function($body)use($mode){
  $j=json_decode($body,true,512,JSON_THROW_ON_ERROR); $last=$mode!=='radar-page';
  if($j['total']!==25||$j['pages']!==3||$j['page']!==($last?3:2)||substr_count($j['html'],'class="radar-row"')!==($last?5:10)||str_contains($j['html'],'Hidden fixture')||!str_contains($j['html'],'symbol=US.AAPL')||!str_contains($j['html'],'market=MY')) throw new RuntimeException('Radar pagination failed');
  if(!str_contains($j['html'],$last?'Fixture 5':'Fixture 15')) throw new RuntimeException('Incorrect radar order');
  return 'PASS: '.$mode." contains the correct slice and preserves context without fetching stock data\n";
 });
} elseif($mode==='radar') {
 ob_start(function($body){
  foreach(['Buy','Sell','Hold'] as $signal) if(!str_contains($body,'Valid radar '.$signal.' fixture')) throw new RuntimeException('A valid radar signal was hidden');
  if(str_contains($body,'Unavailable radar fixture')||str_contains($body,'RECENT FAILED CHECKS')||str_contains($body,'CATALOGUE NOT CHECKED')) throw new RuntimeException('Unwanted radar row or summary card remains');
  return "PASS: radar cards and graph rows contain only Buy, Sell and Hold\n";
 });
} elseif($mode==='catalogue') {
 ob_start(function($body){
  $j=json_decode($body,true,512,JSON_THROW_ON_ERROR);
  if(($j['page']??0)!==2||($j['market']??'')!=='KR'||!str_contains($j['html']??'','KR.')||str_contains($j['html'],'<main>')) throw new RuntimeException('Catalogue response did not contain only the requested result page');
  return "PASS: partial catalogue page works without cURL or reloading selected stock data\n";
 });
} elseif($mode==='export') {
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
