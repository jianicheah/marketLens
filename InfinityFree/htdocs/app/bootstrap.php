<?php
declare(strict_types=1);
date_default_timezone_set('UTC');
ini_set('display_errors', '0');
header('X-Content-Type-Options: nosniff');
header('Referrer-Policy: no-referrer');
header("Content-Security-Policy: default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; object-src 'none'; base-uri 'self'; frame-ancestors 'none'; form-action 'self'");
if(!defined('ML_STORAGE')) define('ML_STORAGE', dirname(__DIR__) . '/storage');
function h($value): string { return htmlspecialchars((string)$value, ENT_QUOTES | ENT_SUBSTITUTE, 'UTF-8'); }
function input(string $key, string $default = ''): string { return isset($_GET[$key]) && is_string($_GET[$key]) ? trim($_GET[$key]) : $default; }
function cacheRead(string $key, int $ttl): ?array {
 $file=ML_STORAGE.'/'.hash('sha256',$key).'.json';
 if(!is_file($file)||filemtime($file)<time()-$ttl) return null;
 $data=json_decode((string)file_get_contents($file),true);
 return is_array($data)?$data:null;
}
function cacheWrite(string $key, array $data): void {
 $file=ML_STORAGE.'/'.hash('sha256',$key).'.json';
 $temp=tempnam(ML_STORAGE,'write-');
 if($temp===false) throw new RuntimeException('Server cache is not writable.');
 try { if(file_put_contents($temp,json_encode($data,JSON_THROW_ON_ERROR),LOCK_EX)===false || !rename($temp,$file)) throw new RuntimeException('Cannot save server cache.'); }
 finally { if(is_file($temp)) unlink($temp); }
}
function withLock(string $name, callable $action) {
 $lock=fopen(ML_STORAGE.'/'.hash('sha256',$name).'.lock','c');
 if(!$lock||!flock($lock,LOCK_EX|LOCK_NB)) { if($lock) fclose($lock); throw new RuntimeException('Another data request is running. Please try again shortly.'); }
 try { return $action(); } finally { flock($lock,LOCK_UN); fclose($lock); }
}
require_once __DIR__.'/Yahoo.php';
require_once __DIR__.'/Analysis.php';
require_once __DIR__.'/Catalogue.php';
