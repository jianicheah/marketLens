<?php
declare(strict_types=1);
function explorerResults(array $companies, string $query, string $market, int $page, int $pages, int $total, bool $lookup, string $symbol, string $horizon, bool $owns, float $bps): string {
 ob_start();
 $context=['q'=>$query,'market'=>$market,'page'=>$page,'horizon'=>$horizon,'owns'=>$owns?'1':'0','cost'=>$bps];
 if($symbol!=='') $context['symbol']=$symbol;
 if($lookup) $context['lookup']='1';
 ?>
 <p class="muted catalogue-count"><?= $lookup ? count($companies).' Yahoo search results' : number($total,0).' matching listings' ?></p>

 <?php if(!$lookup): ?><nav class="pagination" aria-label="Stock catalogue pages">
 <?php if($page>1): ?><a data-catalogue-page href="<?=h(linkTo(array_merge($context,['page'=>$page-1])).'#company-list')?>">← Previous</a><?php else: ?><span class="muted">← Previous</span><?php endif; ?>
 <span>Page <?=$page?> of <?=$pages?></span>
 <?php if($page<$pages): ?><a data-catalogue-page href="<?=h(linkTo(array_merge($context,['page'=>$page+1])).'#company-list')?>">Next →</a><?php else: ?><span class="muted">Next →</span><?php endif; ?>
 </nav><?php endif; ?>
 <div class="stock-results" tabindex="0" aria-label="Stock search results">
 <?php if(!$companies): ?><p class="muted">No listings match these filters.</p><?php endif; ?>
 <?php foreach($companies as $c): ?><a class="stock-row" href="<?=h(linkTo(array_merge($context,['symbol'=>$c['symbol']])).'#stock-analysis')?>"><strong><?=h($c['name'])?></strong><small><?=h($c['symbol'])?> · <?=h($c['exchange']??'')?></small></a><?php endforeach; ?>
 </div>
 <?php return (string)ob_get_clean();
}
