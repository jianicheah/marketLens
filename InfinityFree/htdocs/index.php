<?php
declare(strict_types=1);
require __DIR__.'/app/bootstrap.php';
require __DIR__.'/app/Explorer.php';
require __DIR__.'/app/Radar.php';
if(input('action')==='radar-page') {
 header('Content-Type: application/json'); header('Cache-Control: no-store');
 $context=[]; foreach(['symbol','q','market','page','lookup','horizon','owns','cost'] as $key) if(input($key)!=='') $context[$key]=input($key);
 echo json_encode(radarPage(radar(),(int)input('radarPage','1'),$context),JSON_THROW_ON_ERROR); exit;
}
session_start(['cookie_httponly'=>true,'cookie_secure'=>(!empty($_SERVER['HTTPS'])&&$_SERVER['HTTPS']!=='off'),'cookie_samesite'=>'Lax','use_strict_mode'=>true]);
if(empty($_SESSION['csrf'])) $_SESSION['csrf']=bin2hex(random_bytes(24));
$csrf=$_SESSION['csrf']; session_write_close();
try { catalogue(); } catch(Throwable $e) { http_response_code(503); echo '<!doctype html><title>MarketLens setup</title><h1>MarketLens needs its catalogue files</h1><p>Upload all app/catalogue-*.json files from the package, then reload.</p>'; exit; }
$action=input('action');
if($action==='screen') {
 header('Content-Type: application/json'); header('Cache-Control: no-store');
 if($_SERVER['REQUEST_METHOD']!=='POST'||!hash_equals($csrf,$_SERVER['HTTP_X_CSRF_TOKEN']??'')) { http_response_code(403); echo json_encode(['error'=>'Reload this page to start screening.']); exit; }
 try {
  $result=withLock('screen',function(){
   $last=cacheRead('screen-clock',86400)??[];
   if(($last['time']??0)>time()-60) return ['message'=>'Screening waits at least one minute between listings.'];
   $daily=cacheRead('screen-count',86400)??['day'=>gmdate('Y-m-d'),'count'=>0];
   if($daily['day']!==gmdate('Y-m-d')) $daily=['day'=>gmdate('Y-m-d'),'count'=>0];
   if($daily['count']>=100) return ['message'=>'Today’s 100 automatic checks are complete.'];
   $saved=radar(); $failures=cacheRead('radar-failures',86400)??[];
   $popular=['US.AAPL','US.MSFT','US.NVDA','US.GOOGL','US.AMZN','US.META','US.TSLA','US.VOO','MY.1155','SG.D05','HK.00700'];
   $all=array_unique(array_merge($popular,array_column(catalogue(),'symbol'))); $symbol=null;
   foreach($all as $s) if(!isset($saved[$s])&&($failures[$s]['time']??0)<time()-3600) { $symbol=$s; break; }
   if(!$symbol) return ['message'=>'All available listings have a recent check.'];
   cacheWrite('screen-clock',['time'=>time()]); $daily['count']++; cacheWrite('screen-count',$daily);
   try { $data=(new Yahoo())->history($symbol); $analysis=Analysis::swing($data,time()); saveRadar($data,$analysis); unset($failures[$symbol]); $message=$symbol.': '.$analysis['signal']; }
   catch(RuntimeException $e) { $failures[$symbol]=['time'=>time()]; $message=$symbol.': data unavailable; will retry later.'; }
   cacheWrite('radar-failures',$failures); return ['message'=>$message];
  }); echo json_encode($result);
 } catch(Throwable $e) { http_response_code(503); echo json_encode(['error'=>'Screening temporarily unavailable. Try later.']); }
 exit;
}
$query=substr(input('q'),0,80); $market=input('market'); $symbol=strtoupper(input('symbol')); $horizon=input('horizon','swing');
if(preg_match('/^HK\.([0-9]{1,5})$/D',$symbol,$hk)) $symbol='HK.'.str_pad((string)(int)$hk[1],5,'0',STR_PAD_LEFT);
if(!in_array($horizon,['swing','long'],true)) $horizon='swing';
$owns=input('owns')==='1'; $cost=input('cost','10'); $bps=is_numeric($cost)?(float)$cost:10;
if($bps<0||$bps>100) $bps=10;
$page=max(1,min(10000,(int)input('page','1'))); $error=null; $searchError=null; $fundError=null; $data=null; $analysis=null; $bt=null; $summary=null; $financial=null;
try {
 $matches=filteredCatalogue($query,$market); $total=count($matches); $pages=max(1,(int)ceil($total/24)); $page=min($page,$pages); $companies=array_slice($matches,($page-1)*24,24); unset($matches);
 if(input('lookup')==='1'&&$query!=='') { try { $companies=(new Yahoo())->search($query); } catch(RuntimeException $e) { $searchError=$e->getMessage(); } }
 if($symbol!=='' && $action!=='catalogue') {
  $yahoo=new Yahoo(); $data=$yahoo->history($symbol); $swing=Analysis::swing($data,time()); saveRadar($data,$swing);
  if($horizon==='long') {
   try { $summary=$yahoo->fundamentals($symbol); $reports=$yahoo->statements($symbol); $financial=Analysis::annual($reports,$summary,$data,time()); }
   catch(RuntimeException $e) { $fundError=$e->getMessage(); }
   $analysis=Analysis::longTerm($financial,time());
  } else $analysis=$swing;
  $bt=$horizon==='swing'&&$data['adjusted']?Analysis::backtest($data['bars'],$bps):null;
 }
} catch(RuntimeException $e) { $error=$e->getMessage(); }
catch(Throwable $e) { $error='The server could not load this request. Check that all application files are uploaded and storage is writable.'; }
if($action==='catalogue') {
 header('Content-Type: application/json'); header('Cache-Control: no-store');
 if($error || $searchError) { http_response_code(503); echo json_encode(['error'=>$searchError??$error],JSON_THROW_ON_ERROR); exit; }
 $lookup=input('lookup')==='1' && $query!=='';
 echo json_encode(['html'=>explorerResults($companies,$query,$market,$page,$pages,$total,$lookup,$symbol,$horizon,$owns,$bps),'query'=>$query,'market'=>$market,'page'=>$page,'pages'=>$pages,'total'=>$total,'count'=>count($companies),'lookup'=>$lookup],JSON_THROW_ON_ERROR); exit;
}
if($action==='export'&&$data&&$analysis) {
 header('Content-Type: application/json'); header('Content-Disposition: attachment; filename="marketlens-analysis.json"'); header('Cache-Control: no-store');
 echo json_encode(['source'=>'Yahoo Finance (unofficial)','data'=>$data,'horizon'=>$horizon,'analysis'=>$analysis,'fundamentals'=>$financial,'backtest'=>$bt],JSON_PRETTY_PRINT|JSON_THROW_ON_ERROR); exit;
}
$radar=radar(); $counts=array_count_values(array_column($radar,'signal')); $failures=array_filter(cacheRead('radar-failures',86400)??[],fn($r)=>($r['time']??0)>time()-3600);
$visibleRadar=array_filter($radar,fn($r)=>in_array($r['signal']??'',['Buy','Sell','Hold'],true));
function linkTo(array $params): string { return 'index.php?'.http_build_query($params); }
function chartPoints(array $prices): string {
 if(count($prices)<2) return ''; $min=min($prices); $range=max(max($prices)-$min,0.01); $points=[];
 foreach($prices as $i=>$p) $points[]=round($i*600/(count($prices)-1),2).','.round(150-($p-$min)/$range*130,2);
 return implode(' ',$points);
}
function number($v,int $dec=2): string { return is_numeric($v)?number_format((float)$v,$dec):'—'; }
$labels=['close'=>'Reference close','sma20'=>'SMA 20','sma50'=>'SMA 50','rsi'=>'RSI 14 (Wilder)','atr'=>'ATR 14','volumeRatio'=>'Volume / prior 20 bars','revenueGrowth'=>'Revenue growth YoY (%)','earningsGrowth'=>'Earnings growth YoY (%)','freeCashFlow'=>'Free cash flow','debtEquity'=>'Debt / equity','roe'=>'ROE (%)','pe'=>'P/E TTM','eps'=>'Trailing EPS'];
?><!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><meta name="csrf-token" content="<?=h($csrf)?>"><title>MarketLens · Stock research</title><script src="assets/theme.js?v=20261019-toggle5"></script><link rel="icon" type="image/svg+xml" href="assets/images/marketlens-mark.svg?v=goldgreen9"><link rel="stylesheet" href="assets/site.css?v=20261019-signals1"><link rel="stylesheet" href="assets/gold-theme.css?v=20261019-cream17"><script src="assets/radar-pagination.js?v=20261019-sync14" defer></script><script src="assets/app.js?v=20261019-sync14" defer></script><script src="assets/catalogue-browser.js?v=20261019-colours12" defer></script><script src="assets/chart-hover.js?v=20261019-text9" defer></script></head>
<body><header class="topbar"><a class="brand" href="index.php" aria-label="MarketLens home"><img class="brand-logo logo-light" src="assets/images/marketlens-logo.svg?v=goldgreen9" width="280" height="64" alt="MarketLens"><img class="brand-logo logo-dark" src="assets/images/marketlens-logo-dark.svg?v=goldgreen9" width="280" height="64" alt="MarketLens"></a><nav aria-label="Main navigation"><a href="index.php" <?=!in_array(input('view'),['guide','sources'],true)?'aria-current="page"':''?>>Research</a><a href="?view=guide" <?=input('view')==='guide'?'aria-current="page"':''?>>Guide</a><a href="?view=sources" <?=input('view')==='sources'?'aria-current="page"':''?>>Data sources</a></nav><span class="badge">YAHOO FINANCE</span><button type="button" class="theme-toggle" data-theme-toggle hidden aria-label="Switch colour theme"><svg viewBox="0 0 24 24" width="21" height="21" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><g class="theme-icon-sun"><circle cx="12" cy="12" r="4"/><path d="M12 2v2m0 16v2M2 12h2m16 0h2M4.93 4.93l1.42 1.42m11.3 11.3 1.42 1.42M4.93 19.07l1.42-1.42m11.3-11.3 1.42-1.42"/></g><path class="theme-icon-moon" d="M20.9 13A9 9 0 0 1 11 3.1 9 9 0 1 0 20.9 13Z"/></svg></button></header><main>
<?php if(input('view')==='guide'): ?>
<section class="panel"><p class="eyebrow">RESEARCH GUIDE</p><h1>Understand the rules.</h1><p>Browse the catalogue, filter by name or market, or search Yahoo for additional listings. Use market codes such as US.AAPL, MY.1155, SG.D05 and HK.00700.</p><h2>Weeks / months</h2><p>Buy: close &gt; SMA20 &gt; SMA50, RSI between 45 and 70, and volume at least the prior 20-session average. Sell: close &lt; SMA20 &lt; SMA50 and RSI below 45. Otherwise Hold. At least 60 completed daily bars, recent history, adjustment factors and usable volume are required. The current market date is excluded.</p><h2>Long-term</h2><p>Requires matching annual revenue, income, free cash flow, debt and equity statements, recent trailing EPS and a verified sector. Six checks: positive revenue and earnings growth; positive free cash flow; debt/equity ≤1; ROE ≥12%; positive EPS and P/E ≤25. Buy requires five checks, positive EPS and debt/equity ≤1. Sell requires nonpositive EPS and free cash flow, or debt/equity &gt;2 with declining earnings. Financial companies abstain.</p><h2>Backtest</h2><p>Signals use the prior close; trades execute at the next open with selected costs on each side. Hold preserves the position. Any final position is liquidated at the last close. Long-only, without taxes or market impact. Separate cash dividends are excluded; Yahoo adjusted prices may embed dividends.</p><p>These baseline rules have not been independently validated. News, earnings events, liquidity, taxes and your financial situation are not assessed. Day-trading signals are withheld because this feed has no verified live bid/ask.</p></section>
<?php elseif(input('view')==='sources'): ?>
<section class="panel"><h1>Data sources and privacy</h1><p>Prices, search and company information come from unofficial Yahoo Finance endpoints. Availability, exchange coverage and delays vary. Your successful connection tests do not establish redistribution permission.</p><p>History and fundamentals are cached for one hour; annual statements for one day. All visitors share conservative limits of 8 outgoing requests per minute and 800 per day, including session authorization requests. These are application safeguards, not Yahoo-published quotas. Rate-limit responses pause requests for five minutes. Failures are shown explicitly.</p><p>Charts and technical analysis use completed provider-adjusted daily OHLC; volume remains provider-reported. The separately labelled snapshot is unverified and may be delayed. Annual financial periods and valuation timestamps are checked independently.</p><p>Yahoo receives the requested ticker or search query and the server’s IP address. The server stores a temporary Yahoo cookie/token; no visitor brokerage or Yahoo login is required. Favourites are saved only in this browser. Session cookies protect screening requests. Hosting logs may retain normal request metadata.</p><p>Radar advances one listing per minute while a visitor keeps the research page open, up to 100 checks per UTC day. It stops when no page is open. No unattended background worker is available in this PHP deployment. Results expire after 24 hours.</p></section>
<?php else: ?>
<div class="intro"><p class="eyebrow">YOUR MARKET WORKSPACE</p><h1>Evidence before action.</h1><p class="muted">Timeframe-specific signals with transparent rules and data checks.</p></div>
<?php if($error): ?><aside class="notice" role="alert"><?=h($error)?></aside><?php endif; ?>
<div class="research-layout"><aside class="panel explorer" id="company-list" data-catalogue-browser><h2>Your favourites</h2><div id="favourites"></div><p class="muted">Saved in this browser.</p><h2>Stocks and ETFs</h2><p class="muted"><?=number(count(catalogue()),0)?> bundled listings. Yahoo availability is checked when opened.</p>
<form method="get" action="index.php#company-list" class="search" data-catalogue-form><input type="hidden" name="symbol" value="<?=h($symbol)?>"><input type="hidden" name="horizon" value="<?=h($horizon)?>"><input type="hidden" name="owns" value="<?=$owns?'1':'0'?>"><input type="hidden" name="cost" value="<?=h($bps)?>"><label for="q">Company or ticker</label><input name="q" id="q" value="<?=h($query)?>" maxlength="80" placeholder="Apple, Maybank, US.AAPL"><label for="market">Market</label><select name="market" id="market"><option value="">All markets</option><?php foreach(Yahoo::SUFFIXES as $m=>$suffix): ?><option value="<?=h($m)?>" <?=$market===$m?'selected':''?>><?=h($m)?></option><?php endforeach; ?></select><button>Filter catalogue</button><button name="lookup" value="1" class="outline">Search Yahoo</button></form>
<?php if($searchError): ?><p class="notice"><?=h($searchError)?></p><?php endif; ?>
<p class="catalogue-status" data-catalogue-status role="status" aria-live="polite">Filter or change pages without leaving this view.</p>
<div id="catalogue-results" aria-busy="false"><?=explorerResults($companies??[],$query,$market,$page,$pages??1,$total??0,input('lookup')==='1' && $query!=='' && !$searchError,$symbol,$horizon,$owns,$bps)?></div><details class="direct-listing"><summary>Open a ticker directly</summary><form method="get" action="index.php#stock-analysis" class="search"><label for="symbol">Open a market.ticker directly</label><input id="symbol" name="symbol" placeholder="MY.1155" maxlength="28" required><button>Open listing</button></form></details></aside><div class="research-content"><section class="panel" id="research-radar"><p class="eyebrow">YOUR RESEARCH RADAR</p><h2>Where the signals stand</h2><p class="muted">Totals across all pages · completed daily history · results checked within 24 hours</p><div class="totals">
<?php foreach(['Buy','Sell','Hold'] as $signal): ?><div data-signal="<?=h($signal)?>"><span><?=h(strtoupper($signal))?></span><strong><?=h($counts[$signal]??0)?></strong></div><?php endforeach; ?></div>
<p class="radar-key"><span data-signal="Buy">● Green: Buy</span><span data-signal="Sell">● Red: Sell</span><span data-signal="Hold">● Grey: Hold</span></p><p class="muted">Line colour shows the research signal. The line itself shows historical prices, with a separate scale for each stock.</p>
<?php $radarContext=['symbol'=>$symbol,'q'=>$query,'market'=>$market,'page'=>$page,'lookup'=>input('lookup'),'horizon'=>$horizon,'owns'=>$owns?'1':'0','cost'=>$bps]; ?>
<div id="radar-results" aria-busy="false"><?=radarPage($radar,(int)input('radarPage','1'),$radarContext)['html']?></div>
<p class="muted" id="radar-page-status" role="status" aria-live="polite"></p>
<p class="muted" id="radar-status" role="status">Screening advances while this page is open, at most one listing per minute. Totals and list update automatically after each check.</p></section>

<div id="stock-analysis">
<?php if(!$data||!$analysis): ?><section class="panel"><p class="eyebrow">SELECT A COMPANY</p><h2>Choose a listing to analyse.</h2><p>Open a company to load its history, chart and research signal. Data failures never produce invented prices or signals.</p></section>
<?php else: $bars=$data['bars']; $last=$bars[count($bars)-1]; $chart=array_slice($bars,-120); $prices=array_column($chart,'close'); $change=(end($prices)/$prices[0]-1)*100; ?>
<section class="panel"><p class="eyebrow"><?=h($symbol)?> / YAHOO FINANCE</p><h2><?=h($data['name'])?></h2><div class="quote"><small><?=h($data['currency'])?></small> <?=number($data['quote']['price'])?></div><p class="muted">Unverified provider snapshot · <?=h(is_numeric($data['quote']['time'])?gmdate('Y-m-d H:i',$data['quote']['time']).' UTC':'time unavailable')?> · may be delayed</p><p class="muted">History retrieved <?=h(gmdate('Y-m-d H:i',$data['retrievedAt']))?> UTC · latest completed bar <?=h($last['date'])?></p>
<button type="button" class="outline" id="favourite-toggle" data-symbol="<?=h($symbol)?>" data-name="<?=h($data['name'])?>">☆ Favourite</button><h3>Completed daily closing prices</h3><div class="interactive-chart" data-trend="<?=$change>0?'up':($change<0?'down':'flat')?>" data-chart="<?=h(json_encode(array_map(fn($b)=>['date'=>$b['date'],'price'=>$b['close']],$chart),JSON_THROW_ON_ERROR))?>" data-currency="<?=h($data['currency'])?>" data-price-label="<?=h($data['adjusted'] ? 'Adjusted daily close' : 'Daily close')?>" tabindex="0" role="group" aria-label="Interactive price chart. Use Left and Right arrow keys to explore daily prices."><svg class="price-chart" viewBox="0 0 600 180" role="img" aria-label="Adjusted daily closing price chart"><path d="M0 40H600 M0 90H600 M0 140H600"/><polyline points="<?=h(chartPoints($prices))?>"/></svg></div><p class="chart-help">Hover or touch the chart to see the date and closing price. Keyboard: ← / →.</p><div class="chart-label"><span><?=h($chart[0]['date'])?></span><span><?=number($change)?>% · <?=h($last['date'])?></span></div><p class="muted">Historical prices use Yahoo adjustment factors, which may include dividends. <?=h($data['skipped'])?> incomplete observations skipped.</p></section>
<section class="panel"><h2>Investment analysis</h2><form method="get" class="controls"><input type="hidden" name="symbol" value="<?=h($symbol)?>"><label for="horizon">Timeframe</label><select name="horizon" id="horizon"><option value="swing" <?=$horizon==='swing'?'selected':''?>>Weeks / months</option><option value="long" <?=$horizon==='long'?'selected':''?>>Long-term</option></select><label><input type="checkbox" name="owns" value="1" <?=$owns?'checked':''?>> I own this stock</label><label for="cost">Backtest cost per side (bps)</label><input type="number" id="cost" name="cost" min="0" max="100" step="0.1" value="<?=h($bps)?>"><button>Analyse</button></form>
<?php if($fundError): ?><p class="notice"><?=h($fundError)?></p><?php endif; ?>
<div class="assessment" data-signal="<?=h($analysis['signal'])?>"><p class="eyebrow">RESEARCH SIGNAL</p><h3><?=h($analysis['signal'])?></h3><p><?=h($analysis['reason'])?></p><p><?=h($analysis['asOf']??'')?></p><?php if($analysis['signal']==='Sell'): ?><p><?=$owns?'Review reducing your existing holding after considering risks.':'Avoid-entry signal; this is not a short-sale instruction.'?></p><?php endif; ?></div><div class="metrics"><?php foreach($analysis['metrics'] as $key=>$value): ?><div><span><?=h($labels[$key]??$key)?></span><strong><?=number($value)?></strong></div><?php endforeach; ?></div>
<?php if(isset($analysis['stop'])): ?><p>Illustrative ATR stop: <?=number($analysis['stop'])?> · target: <?=number($analysis['target'])?>. These are not orders or guaranteed exits.</p><?php endif; ?>
<h3>Why this result?</h3><p><?=$horizon==='swing'?'Buy: close > SMA20 > SMA50, RSI 45–70, and volume ≥ prior 20-session average. Sell: close < SMA20 < SMA50 and RSI <45. Otherwise Hold.':'Positive revenue/earnings growth and free cash flow; debt/equity ≤1; ROE ≥12%; positive EPS and P/E ≤25. Financial firms and incomplete or stale financial periods abstain.'?></p>
<h3>Risks and limits</h3><p>Rules are a baseline, not a proven predictor. Past performance does not establish future returns. News, earnings events, taxes, liquidity and your financial situation are not assessed. This feed cannot support actionable day-trading signals. Long-term rules have no point-in-time fundamentals backtest.</p><a href="<?=h(linkTo(['symbol'=>$symbol,'horizon'=>$horizon,'cost'=>$bps,'owns'=>$owns?'1':'0','action'=>'export']))?>">Download analysis JSON</a></section>
<section class="panel"><h2>Historical strategy check</h2><?php if($bt): ?><p><?=h($bt['start'])?> to <?=h($bt['end'])?> · <?=h($bps)?> bps per side</p><div class="metrics"><?php foreach(['strategyReturn'=>'Strategy return (%)','buyHoldReturn'=>'Buy & hold (%)','maxDrawdown'=>'Max drawdown (%)','closedTrades'=>'Closed round trips','winRate'=>'Trade win rate (%)'] as $k=>$label): ?><div><span><?=h($label)?></span><strong><?=number($bt[$k],$k==='closedTrades'?0:2)?></strong></div><?php endforeach; ?></div><p>Signals use the prior close; trades use the next open. Long-only; Hold preserves the position. The final position is liquidated at the last close. Benchmark uses the same starting open and costs. Taxes, market impact and separate cash dividends are excluded. Provider adjustment may embed dividends.</p><p>This is a retrospective check on one series, not independent validation or a success probability.</p><?php else: ?><p>A swing backtest requires 90 completed adjusted daily bars. Fundamentals and intraday backtests are unavailable.</p><?php endif; ?></section>
<?php endif; ?></div></div></div>
<?php endif; ?></main><footer class="site-footer"><img class="footer-watermark logo-light" src="assets/images/marketlens-logo.svg?v=goldgreen9" width="280" height="64" alt="MarketLens"><img class="footer-watermark logo-dark" src="assets/images/marketlens-logo-dark.svg?v=goldgreen9" width="280" height="64" alt="MarketLens"><p>&copy; <?=gmdate('Y')?> MarketLens. All rights reserved.</p><p>WHD-A08ACD2DF2E7</p><p><a href="?view=sources">Sources & privacy</a></p></footer></body></html>
