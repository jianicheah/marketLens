<?php
declare(strict_types=1);
function catalogue(): array {
 static $rows=null;
 if($rows===null) {
  $parts=glob(__DIR__.'/catalogue-*.json'); $rows=[];
  if(!$parts) throw new RuntimeException('Catalogue files are missing. Upload every app/catalogue-*.json file from the package.');
  foreach($parts as $part) {
   $batch=json_decode((string)file_get_contents($part),true,512,JSON_THROW_ON_ERROR);
   foreach($batch as $row) $rows[]=$row;
  }
 }
 return $rows;
}
function filteredCatalogue(string $query,string $market): array {
 $aliases=['maybank'=>'malayan banking','airasia'=>'capital a','google'=>'alphabet'];
 $term=strtolower($query); $alias=$aliases[$term]??$term;
 return array_values(array_filter(catalogue(),fn($c)=>($market===''||str_starts_with($c['symbol'],$market.'.'))&&($term===''||str_contains(strtolower($c['symbol'].' '.$c['name']),$term)||str_contains(strtolower($c['name']),$alias))));
}
function saveRadar(array $data,array $analysis): void {
 withLock('radar',function()use($data,$analysis){
  $rows=cacheRead('radar',86400)??[];
  $rows[$data['symbol']]=['symbol'=>$data['symbol'],'name'=>$data['name'],'currency'=>$data['currency'],'signal'=>$analysis['signal'],'checkedAt'=>time(),'prices'=>array_column(array_slice($data['bars'],-60),'close'),'lastDate'=>$data['bars'][count($data['bars'])-1]['date']];
  // Bound file size on shared hosting.
  if(count($rows)>1000) $rows=array_slice($rows,-1000,null,true);
  cacheWrite('radar',$rows);
 });
}
function radar(): array { return array_filter(cacheRead('radar',86400)??[],fn($r)=>($r['checkedAt']??0)>time()-86400); }
