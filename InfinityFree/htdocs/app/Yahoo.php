<?php
declare(strict_types=1);
final class Yahoo {
 public const SUFFIXES=['US'=>'','MY'=>'.KL','HK'=>'.HK','SG'=>'.SI','JP'=>'.T','SH'=>'.SS','SZ'=>'.SZ','BJ'=>'.BJ','AU'=>'.AX','CA'=>'.TO','KR'=>'.KS','UK'=>'.L','DE'=>'.DE','FR'=>'.PA','IN'=>'.NS','TW'=>'.TW','ID'=>'.JK','TH'=>'.BK','NZ'=>'.NZ','BR'=>'.SA','MX'=>'.MX','ZA'=>'.JO','NL'=>'.AS','IT'=>'.MI','CH'=>'.SW','ES'=>'.MC'];
 private string $jar;
 public function __construct() {
  if(!function_exists('curl_init')) throw new RuntimeException('PHP cURL must be enabled on this hosting account.');
  $this->jar=ML_STORAGE.'/yahoo.cookies';
 }
 public static function ticker(string $symbol): string {
  if(!preg_match('/^([A-Z]{2})\.([A-Z0-9][A-Z0-9.\-]{0,24})$/D',$symbol,$m)||!array_key_exists($m[1],self::SUFFIXES)) throw new RuntimeException('Use a supported market code, for example US.AAPL, MY.1155, SG.D05 or HK.00700.');
  [$all,$market,$code]=$m;
  if($market==='US') return str_replace('.','-',$code);
  if($market==='HK'&&ctype_digit($code)) $code=str_pad((string)(int)$code,4,'0',STR_PAD_LEFT);
  return $code.(str_contains($code,'.')?'':self::SUFFIXES[$market]);
 }
 public static function symbol(string $ticker): ?string {
  $ticker=strtoupper($ticker); $dot=strrpos($ticker,'.');
  if($dot===false) $symbol='US.'.$ticker;
  else {
   $suffix=substr($ticker,$dot); $market=array_search($suffix,self::SUFFIXES,true); $code=substr($ticker,0,$dot);
   if($market===false) { $market=['.BO'=>'IN','.TWO'=>'TW','.KQ'=>'KR','.V'=>'CA','.F'=>'DE','.MU'=>'DE','.BE'=>'DE','.DU'=>'DE','.HM'=>'DE','.HA'=>'DE','.SG'=>'DE'][$suffix]??null; $code=$ticker; }
   if(!$market) return null;
   if($market==='HK'&&ctype_digit($code)) $code=str_pad((string)(int)$code,5,'0',STR_PAD_LEFT);
   $symbol=$market.'.'.$code;
  }
  try { self::ticker($symbol); return $symbol; } catch(RuntimeException $e) { return null; }
 }
 private function request(string $host, string $path): array {
  if(!in_array($host,['query1.finance.yahoo.com','fc.yahoo.com'],true)) throw new RuntimeException('Invalid provider host.');
  // All visitors share an outbound budget and provider backoff, including uncached searches.
  withLock('outbound-budget',function(){
   $state=cacheRead('outbound-budget',86400)??[]; $now=time();
   if(($state['blockedUntil']??0)>$now) throw new RuntimeException('Yahoo requests are temporarily paused after a rate limit. Try again later.');
   $calls=array_values(array_filter($state['calls']??[],fn($t)=>$t>$now-86400));
   if(count($calls)>=800||count(array_filter($calls,fn($t)=>$t>$now-60))>=8) throw new RuntimeException('The shared Yahoo request budget is reached. Please try again later.');
   $calls[]=$now; cacheWrite('outbound-budget',['calls'=>$calls]);
  });
  $ch=curl_init('https://'.$host.'/'.$path);
  curl_setopt_array($ch,[CURLOPT_RETURNTRANSFER=>true,CURLOPT_CONNECTTIMEOUT=>5,CURLOPT_TIMEOUT=>10,CURLOPT_FOLLOWLOCATION=>false,CURLOPT_COOKIEFILE=>$this->jar,CURLOPT_COOKIEJAR=>$this->jar,CURLOPT_USERAGENT=>'Mozilla/5.0',CURLOPT_HTTPHEADER=>['Accept: application/json, text/plain, */*'],CURLOPT_PROTOCOLS=>CURLPROTO_HTTPS,CURLOPT_MAXFILESIZE=>8000000]);
  if(PHP_OS_FAMILY==='Windows'&&defined('CURLSSLOPT_NATIVE_CA')) curl_setopt($ch,CURLOPT_SSL_OPTIONS,CURLSSLOPT_NATIVE_CA);
  $body=curl_exec($ch); $status=(int)curl_getinfo($ch,CURLINFO_HTTP_CODE); curl_close($ch);
  if($status===429) withLock('outbound-budget',function(){ $s=cacheRead('outbound-budget',86400)??[]; $s['blockedUntil']=time()+300; cacheWrite('outbound-budget',$s); });
  if($body===false) throw new RuntimeException('Yahoo connection failed or timed out. No data was invented.');
  return [$status,$body];
 }
 private function json(string $path): array {
  [$status,$body]=$this->request('query1.finance.yahoo.com',$path);
  if($status!==200) throw new RuntimeException('Yahoo request failed (HTTP '.$status.'). Access may be restricted or rate limited.');
  $j=json_decode($body,true);
  if(!is_array($j)) throw new RuntimeException('Yahoo returned an unexpected response.');
  return $j;
 }
 private function crumb(bool $reset=false): string {
  if(!$reset&&($saved=cacheRead('yahoo-token',1800))&&is_file($this->jar)) return $saved['token'];
  if($reset&&is_file($this->jar)) unlink($this->jar);
  $this->request('fc.yahoo.com',''); // 404 is expected; the cookie is the useful result.
  [$status,$body]=$this->request('query1.finance.yahoo.com','v1/test/getcrumb');
  $token=trim($body);
  if($status!==200||strlen($token)<1||strlen($token)>128||preg_match('/[\s<>{}]/',$token)) throw new RuntimeException('Yahoo session authorization is unavailable.');
  cacheWrite('yahoo-token',['token'=>$token]); return $token;
 }
 public function search(string $query): array {
  if(strlen($query)<1||strlen($query)>80) return [];
  if($cached=cacheRead('search:'.$query,3600)) return $cached;
  return withLock('yahoo',function()use($query){
   $j=$this->json('v1/finance/search?'.http_build_query(['q'=>$query,'quotesCount'=>20,'newsCount'=>0])); $rows=[];
   foreach($j['quotes']??[] as $q) { $symbol=self::symbol($q['symbol']??''); if($symbol&&in_array($q['quoteType']??'',['EQUITY','ETF'],true)) $rows[]=['symbol'=>$symbol,'name'=>$q['longname']??$q['shortname']??$symbol,'exchange'=>$q['exchDisp']??$q['exchange']??'','instrumentType'=>$q['quoteType']]; }
   cacheWrite('search:'.$query,$rows); return $rows;
  });
 }
 public static function parseChart(array $j,string $symbol,int $now): array {
  $r=$j['chart']['result'][0]??null; $meta=$r['meta']??[];
  if(!$r||strcasecmp($meta['symbol']??'',self::ticker($symbol))!==0) throw new RuntimeException('Yahoo did not return the requested listing.');
  if(!in_array($meta['instrumentType']??'',['EQUITY','ETF'],true)||empty($meta['currency'])) throw new RuntimeException('Only stock and ETF histories with a currency are supported.');
  $zone=new DateTimeZone($meta['exchangeTimezoneName']??'UTC'); $today=(new DateTimeImmutable('@'.$now))->setTimezone($zone)->format('Y-m-d');
  $q=$r['indicators']['quote'][0]??[]; $adj=$r['indicators']['adjclose'][0]['adjclose']??[]; $bars=[]; $complete=true; $previous=0; $dates=[]; $skipped=0;
  foreach($r['timestamp']??[] as $i=>$t) {
   $values=[]; foreach(['open','high','low','close','volume'] as $f) $values[$f]=$q[$f][$i]??null;
   if(!is_numeric($t)||$t>$now+60||$t<=$previous) throw new RuntimeException('History contains invalid or unordered timestamps.');
   $previous=$t; $date=(new DateTimeImmutable('@'.$t))->setTimezone($zone)->format('Y-m-d');
   if(isset($dates[$date])) throw new RuntimeException('History contains duplicate market dates.'); $dates[$date]=true;
   if(count(array_filter($values,'is_numeric'))!==5||min($values['open']??0,$values['high']??0,$values['low']??0,$values['close']??0)<=0||$values['volume']<0||$values['high']<max($values['open'],$values['close'])||$values['low']>min($values['open'],$values['close'])) { $skipped++; continue; }
   $factor=isset($adj[$i])&&is_numeric($adj[$i])&&$adj[$i]>0?$adj[$i]/$values['close']:null;
   if($factor===null) $complete=false;
   foreach(['open','high','low','close'] as $f) $values[$f]*=$factor??1;
   if($date<$today) $bars[]=array_merge(['time'=>(int)$t,'date'=>$date],$values);
  }
  if(!$bars) throw new RuntimeException('No completed usable daily prices were returned.');
  return ['symbol'=>$symbol,'name'=>$meta['longName']??$meta['shortName']??self::ticker($symbol),'currency'=>$meta['currency'],'type'=>$meta['instrumentType'],'retrievedAt'=>$now,'quote'=>['price'=>$meta['regularMarketPrice']??null,'time'=>$meta['regularMarketTime']??null],'bars'=>$bars,'adjusted'=>$complete,'skipped'=>$skipped,'timezone'=>$zone->getName()];
 }
 public function history(string $symbol): array {
  $ticker=self::ticker($symbol);
  if($cached=cacheRead('history:'.$symbol,3600)) return $cached;
  return withLock('yahoo',function()use($symbol,$ticker){
   $j=$this->json('v8/finance/chart/'.rawurlencode($ticker).'?range=2y&interval=1d&events=div%2Csplits');
   $data=self::parseChart($j,$symbol,time()); cacheWrite('history:'.$symbol,$data); return $data;
  });
 }
 public function fundamentals(string $symbol): array {
  $ticker=self::ticker($symbol);
  if($cached=cacheRead('fundamentals:'.$symbol,3600)) return $cached;
  return withLock('yahoo',function()use($symbol,$ticker){
   for($attempt=0;$attempt<2;$attempt++) {
    $token=$this->crumb($attempt===1);
    [$status,$body]=$this->request('query1.finance.yahoo.com','v10/finance/quoteSummary/'.rawurlencode($ticker).'?'.http_build_query(['modules'=>'financialData,defaultKeyStatistics,summaryDetail,assetProfile','crumb'=>$token]));
    if($status!==401&&$status!==403) break;
   }
   if($status!==200) throw new RuntimeException('Yahoo fundamentals are unavailable (HTTP '.$status.').');
   $j=json_decode($body,true); $r=$j['quoteSummary']['result'][0]??null;
   if(!is_array($r)) throw new RuntimeException('Yahoo returned no company fundamentals.');
   $r['_retrievedAt']=time(); cacheWrite('fundamentals:'.$symbol,$r); return $r;
  });
 }
 public function statements(string $symbol): array {
  $ticker=self::ticker($symbol);
  if($cached=cacheRead('statements:'.$symbol,86400)) return $cached;
  return withLock('yahoo',function()use($symbol,$ticker){
   $j=$this->json('ws/fundamentals-timeseries/v1/finance/timeseries/'.rawurlencode($ticker).'?'.http_build_query(['type'=>'annualTotalRevenue,annualNetIncome,annualFreeCashFlow,annualTotalDebt,annualStockholdersEquity,trailingDilutedEPS','period1'=>strtotime('-4 years'),'period2'=>time()]));
   if(empty($j['timeseries']['result'])) throw new RuntimeException('Matching financial statements are unavailable.');
   cacheWrite('statements:'.$symbol,$j); return $j;
  });
 }
}
