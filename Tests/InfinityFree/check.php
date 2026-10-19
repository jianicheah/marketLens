<?php
declare(strict_types=1);
require __DIR__.'/../../InfinityFree/htdocs/app/Yahoo.php';
require __DIR__.'/../../InfinityFree/htdocs/app/Analysis.php';
$checks=0;
function check(bool $ok,string $label): void { global $checks; $checks++; if(!$ok) throw new RuntimeException('FAIL: '.$label); }
function rejects(callable $fn,string $label): void { try { $fn(); } catch(RuntimeException $e) { check(true,$label); return; } check(false,$label); }
function fixture(string $name): array { return json_decode(file_get_contents(__DIR__.'/../Fixtures/'.$name.'.json'),true,512,JSON_THROW_ON_ERROR); }
check(Yahoo::ticker('US.AAPL')==='AAPL','US ticker');
check(Yahoo::ticker('US.BRK.B')==='BRK-B','US share class');
check(Yahoo::ticker('MY.1155')==='1155.KL','Malaysia ticker');
check(Yahoo::ticker('HK.00700')==='0700.HK','Hong Kong padding');
check(Yahoo::ticker('SG.D05')==='D05.SI','Singapore ticker');
check(Yahoo::symbol('0700.HK')==='HK.00700','Hong Kong reverse mapping');
check(Yahoo::symbol('AAPL')==='US.AAPL','US reverse mapping');
rejects(fn()=>Yahoo::ticker('../../secret'),'path injection');
rejects(fn()=>Yahoo::ticker('ZZ.AAPL'),'unsupported market');
check(Yahoo::symbol('THING.UNKNOWN')===null,'unknown suffix');
$raw=fixture('yahoo-aapl'); $now=1791547200;
$data=Yahoo::parseChart($raw,'US.AAPL',$now);
check(count($data['bars'])>60,'real Yahoo fixture parses');
check($data['currency']==='USD','currency retained');
check($data['adjusted']===true,'adjustment coverage');
$today=(new DateTimeImmutable('@'.$now))->setTimezone(new DateTimeZone('America/New_York'))->format('Y-m-d');
check(count(array_filter($data['bars'],fn($b)=>$b['date']>=$today))===0,'today excluded in market timezone');
rejects(fn()=>Yahoo::parseChart($raw,'US.MSFT',$now),'wrong ticker rejected');
$future=$raw; $future['chart']['result'][0]['timestamp'][0]=$now+3600;
rejects(fn()=>Yahoo::parseChart($future,'US.AAPL',$now),'future timestamp rejected');
$unordered=$raw; $unordered['chart']['result'][0]['timestamp'][1]=$unordered['chart']['result'][0]['timestamp'][0];
rejects(fn()=>Yahoo::parseChart($unordered,'US.AAPL',$now),'duplicate timestamp rejected');
$unadjusted=$raw; unset($unadjusted['chart']['result'][0]['indicators']['adjclose']);
$u=Yahoo::parseChart($unadjusted,'US.AAPL',$now);
check(Analysis::swing($u,$now)['signal']==='Insufficient data','missing adjustment abstains');
check(Analysis::swing($data,$now+20*86400)['signal']==='Insufficient data','stale prices abstain');
$short=$data; $short['bars']=array_slice($short['bars'],0,20);
check(Analysis::swing($short,$now)['signal']==='Insufficient data','short history abstains');
$flat=[]; for($i=0;$i<100;$i++) $flat[]=['time'=>$now-(101-$i)*86400,'date'=>gmdate('Y-m-d',$now-(101-$i)*86400),'open'=>100,'high'=>101,'low'=>99,'close'=>100,'volume'=>1000];
$ind=Analysis::indicators($flat);
check($ind['rsi']===50,'flat Wilder RSI is neutral');
check($ind['sma20']===100||$ind['sma20']===100.0,'flat SMA');
check(Analysis::trend($flat)==='Hold','flat series holds');
$up=[]; $down=[]; $uprice=100; $dprice=100;
for($i=0;$i<100;$i++) {
 $uprice+=$i%2===0?1:-0.6; $dprice+=$i%2===0?-1:0.6;
 $base=['time'=>$now-(101-$i)*86400,'date'=>gmdate('Y-m-d',$now-(101-$i)*86400),'volume'=>1000];
 $up[]=array_merge($base,['open'=>$uprice,'high'=>$uprice+1,'low'=>$uprice-1,'close'=>$uprice]);
 $down[]=array_merge($base,['open'=>$dprice,'high'=>$dprice+1,'low'=>$dprice-1,'close'=>$dprice]);
}
check(Analysis::trend($up)==='Buy','rising moderate RSI and confirmed volume buys');
check(Analysis::trend($down)==='Sell','falling moderate RSI sells');
$gap=$up; $gap[60]['open']=200; $gap[60]['high']=201;
$gapBt=Analysis::backtest($gap,0);
check(abs($gapBt['strategyReturn']-($gap[99]['close']/200-1)*100)<0.000001,'backtest buys at next open including a gap, not the signal close');
$bt=Analysis::backtest($flat,10);
check(abs($bt['strategyReturn'])<0.000001,'no trades returns zero');
check($bt['buyHoldReturn']<0,'benchmark includes costs');
check($bt['closedTrades']===0&&$bt['winRate']===null,'empty trade win rate is unavailable');
check(Analysis::backtest(array_slice($flat,0,60),10)===null,'short backtest unavailable');
rejects(fn()=>Analysis::backtest($flat,101),'invalid backtest costs');
$funds=fixture('yahoo-fundamentals');
$summary=['assetProfile'=>['sector'=>'Technology']];
$annual=Analysis::annual($funds,$summary,$data,$now);
check($annual!==null,'matching annual statements parse');
check($annual['revenueGrowth']>0,'annual growth computed');
check(Analysis::annual($funds,[],$data,$now)===null,'missing classification abstains');
$mismatch=$funds; $mismatch['timeseries']['result'][0]['annualTotalRevenue'][0]['currencyCode']='EUR';
check(Analysis::annual($mismatch,$summary,$data,$now)===null,'statement currency mismatch rejected');
$bank=$annual; $bank['financial']=true;
check(Analysis::longTerm($bank,$now)['signal']==='Insufficient data','financial companies abstain');
check(Analysis::longTerm(null,$now)['signal']==='Insufficient data','missing fundamentals abstain');
$old=$annual; $old['period']=$now-461*86400;
check(Analysis::longTerm($old,$now)['signal']==='Insufficient data','stale statements abstain');
$old=$annual; $old['valuationTime']=$now-6*86400;
check(Analysis::longTerm($old,$now)['signal']==='Insufficient data','stale valuation abstains');
foreach(['yahoo-maybank'=>'MY.1155','yahoo-tencent'=>'HK.00700'] as $name=>$symbol) { $d=Yahoo::parseChart(fixture($name),$symbol,$now); check(count($d['bars'])>60,$name.' history parses'); }
echo 'PASS: '.$checks." PHP regression checks\n";
