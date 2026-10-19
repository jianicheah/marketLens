<?php
declare(strict_types=1);
final class Analysis {
 public static function indicators(array $bars): array {
  if(count($bars)<60) throw new RuntimeException('At least 60 completed daily bars are required.');
  $closes=array_column($bars,'close'); $n=count($bars); $gain=0; $loss=0;
  for($i=1;$i<=14;$i++) { $d=$closes[$i]-$closes[$i-1]; $gain+=max(0,$d); $loss+=max(0,-$d); }
  $gain/=14; $loss/=14;
  for($i=15;$i<$n;$i++) { $d=$closes[$i]-$closes[$i-1]; $gain=($gain*13+max(0,$d))/14; $loss=($loss*13+max(0,-$d))/14; }
  $atr=0; for($i=$n-14;$i<$n;$i++) $atr+=max($bars[$i]['high']-$bars[$i]['low'],abs($bars[$i]['high']-$closes[$i-1]),abs($bars[$i]['low']-$closes[$i-1]));
  $priorVolume=array_sum(array_column(array_slice($bars,-21,20),'volume'))/20;
  return ['close'=>$closes[$n-1],'sma20'=>array_sum(array_slice($closes,-20))/20,'sma50'=>array_sum(array_slice($closes,-50))/50,'rsi'=>$loss==0?($gain==0?50:100):100-100/(1+$gain/$loss),'atr'=>$atr/14,'volumeRatio'=>$priorVolume>0?$bars[$n-1]['volume']/$priorVolume:0];
 }
 public static function trend(array $bars): string {
  $i=self::indicators($bars);
  if($i['close']>$i['sma20']&&$i['sma20']>$i['sma50']&&$i['rsi']>=45&&$i['rsi']<=70&&$i['volumeRatio']>=1) return 'Buy';
  if($i['close']<$i['sma20']&&$i['sma20']<$i['sma50']&&$i['rsi']<45) return 'Sell';
  return 'Hold';
 }
 public static function swing(array $data,int $now): array {
  $bars=$data['bars']; $n=count($bars);
  $missing=fn($reason)=>['signal'=>'Insufficient data','reason'=>$reason,'metrics'=>[]];
  if($n<60) return $missing('At least 60 completed daily bars are required.');
  if($now-$bars[$n-1]['time']>5*86400) return $missing('Latest completed daily bar is older than five days.');
  if(!$data['adjusted']) return $missing('Complete adjusted prices are required for historical comparisons.');
  if($bars[$n-1]['time']-$bars[$n-60]['time']>180*86400) return $missing('Daily history is too sparse.');
  $i=self::indicators($bars);
  if($bars[$n-1]['volume']<=0||$i['volumeRatio']<=0) return $missing('Usable current and historical volume is required.');
  if($i['atr']/$i['close']>0.08) return $missing('ATR exceeds 8% of price; this baseline abstains.');
  $signal=self::trend($bars);
  return ['signal'=>$signal,'reason'=>'Based on the latest completed daily close.','asOf'=>$bars[$n-1]['date'],'metrics'=>$i,'stop'=>$signal==='Buy'?max($i['close']*0.01,$i['close']-2*$i['atr']):null,'target'=>$signal==='Buy'?$i['close']+4*$i['atr']:null];
 }
 public static function annual(array $reports,array $summary,array $data,int $now): ?array {
  $rows=[];
  foreach($reports['timeseries']['result']??[] as $group) foreach($group as $field=>$values) {
   if(!in_array($field,['annualTotalRevenue','annualNetIncome','annualFreeCashFlow','annualTotalDebt','annualStockholdersEquity','trailingDilutedEPS'],true)||!is_array($values)) continue;
   foreach($values as $r) if(isset($r['asOfDate'],$r['reportedValue']['raw'])&&is_numeric($r['reportedValue']['raw'])) $rows[$field][$r['asOfDate']]=$r;
  }
  foreach($rows as &$group) ksort($group); unset($group);
  $revenues=$rows['annualTotalRevenue']??[]; if(count($revenues)<2) return null;
  $dates=array_keys($revenues); $date=$dates[count($dates)-1]; $priorDate=$dates[count($dates)-2]; $records=[];
  foreach([['annualTotalRevenue',$date],['annualTotalRevenue',$priorDate],['annualNetIncome',$date],['annualNetIncome',$priorDate],['annualFreeCashFlow',$date],['annualTotalDebt',$date],['annualStockholdersEquity',$date],['annualStockholdersEquity',$priorDate]] as [$field,$d]) {
   if(!isset($rows[$field][$d])) return null; $records[]=$rows[$field][$d];
  }
  $epsRows=$rows['trailingDilutedEPS']??[]; if(!$epsRows) return null; $epsRow=end($epsRows); $records[]=$epsRow;
  $currency=$records[0]['currencyCode']??null;
  foreach($records as $r) if(!$currency||($r['currencyCode']??null)!==$currency) return null;
  $factor=$data['currency']===$currency?1:((($data['currency']==='GBp'&&$currency==='GBP')||($data['currency']==='ZAc'&&$currency==='ZAR'))?100:0);
  if(!$factor) return null;
  [$rev,$priorRev,$net,$priorNet,$fcf,$debt,$equity,$priorEquity,$eps]=array_map(fn($r)=>(float)$r['reportedValue']['raw'],$records);
  $period=strtotime($date.' UTC'); $epsPeriod=strtotime($epsRow['asOfDate'].' UTC');
  if(!$period||!$epsPeriod||$period>$now||$epsPeriod>$now||$now-$epsPeriod>200*86400||$priorRev<=0||$priorNet<=0||$equity<=0||$priorEquity<=0||$debt<0) return null;
  $sector=$summary['assetProfile']['sector']??null; if(!$sector) return null;
  $quote=$data['quote']; $eps*=$factor;
  if(!is_numeric($quote['price']??null)||!is_numeric($quote['time']??null)||$quote['time']>$now+60) return null;
  return ['period'=>$period,'valuationTime'=>$quote['time'],'financial'=>$sector==='Financial Services','revenueGrowth'=>($rev/$priorRev-1)*100,'earningsGrowth'=>($net/$priorNet-1)*100,'freeCashFlow'=>$fcf,'debtEquity'=>$debt/$equity,'roe'=>$net/(($equity+$priorEquity)/2)*100,'pe'=>$eps>0?$quote['price']/$eps:0,'eps'=>$eps];
 }
 public static function longTerm(?array $f,int $now): array {
  $missing=fn($why)=>['signal'=>'Insufficient data','reason'=>$why,'metrics'=>[]];
  if(!$f) return $missing('Complete matching annual statements, recent trailing EPS and sector classification are required. Summary fields alone do not establish comparable reporting periods.');
  if($now-$f['period']>460*86400) return $missing('Financial statements are older than 460 days.');
  if($now-$f['valuationTime']>5*86400) return $missing('A valuation timestamp within five days is required.');
  if($f['financial']) return $missing('Banks and insurers require sector-specific models.');
  $checks=[$f['revenueGrowth']>0,$f['earningsGrowth']>0,$f['freeCashFlow']>0,$f['debtEquity']<=1,$f['roe']>=12,$f['pe']>0&&$f['pe']<=25&&$f['eps']>0]; $score=count(array_filter($checks));
  $sell=($f['eps']<=0&&$f['freeCashFlow']<=0)||($f['debtEquity']>2&&$f['earningsGrowth']<0);
  $signal=$sell?'Sell':($score>=5&&$f['eps']>0&&$f['debtEquity']<=1?'Buy':'Hold');
  return ['signal'=>$signal,'reason'=>$score.'/6 screening criteria passed; this is a checklist count, not a probability.','asOf'=>gmdate('Y-m-d',$f['period']),'metrics'=>array_intersect_key($f,array_flip(['revenueGrowth','earningsGrowth','freeCashFlow','debtEquity','roe','pe','eps']))];
 }
 public static function backtest(array $bars,float $bps): ?array {
  if($bps<0||$bps>100) throw new RuntimeException('Backtest cost must be between 0 and 100 bps.');
  if(count($bars)<90) return null;
  $cost=$bps/10000; $cash=10000; $shares=0; $entry=0; $peak=10000; $drawdown=0; $trades=0; $wins=0;
  for($i=60;$i<count($bars);$i++) {
   $signal=self::trend(array_slice($bars,0,$i));
   if($signal==='Buy'&&$shares==0) { $entry=$cash; $shares=$cash/($bars[$i]['open']*(1+$cost)); $cash=0; }
   elseif($signal==='Sell'&&$shares>0) { $cash=$shares*$bars[$i]['open']*(1-$cost); $shares=0; $trades++; if($cash>$entry) $wins++; }
   $value=$cash+$shares*$bars[$i]['close']; $peak=max($peak,$value); $drawdown=max($drawdown,($peak-$value)/$peak);
  }
  $last=$bars[count($bars)-1];
  if($shares>0) { $cash=$shares*$last['close']*(1-$cost); $trades++; if($cash>$entry) $wins++; }
  $drawdown=max($drawdown,($peak-$cash)/$peak);
  return ['strategyReturn'=>($cash/10000-1)*100,'buyHoldReturn'=>($last['close']/($bars[60]['open']*(1+$cost))*(1-$cost)-1)*100,'maxDrawdown'=>$drawdown*100,'closedTrades'=>$trades,'winRate'=>$trades?$wins/$trades*100:null,'start'=>$bars[60]['date'],'end'=>$last['date'],'costBps'=>$bps];
 }
}
