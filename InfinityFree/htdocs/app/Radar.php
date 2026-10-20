<?php
declare(strict_types=1);
function radarPage(array $rows, int $requested, array $context=[]): array {
 $rows=array_values(array_reverse(array_filter($rows,fn($r)=>in_array($r['signal']??'',['Buy','Sell','Hold'],true)),true));
 // Stable sorting keeps the existing newest-first order within each signal.
 $priority=['Sell'=>0,'Buy'=>1,'Hold'=>2];
 usort($rows,fn($a,$b)=>$priority[$a['signal']]<=>$priority[$b['signal']]);
 $total=count($rows); $pages=max(1,(int)ceil($total/10)); $page=max(1,min($requested,$pages));
 $slice=array_slice($rows,($page-1)*10,10); $pageCounts=array_count_values(array_column($slice,'signal'));
 ob_start(); ?>
 <p class="muted" data-radar-current="<?=$page?>"><?= $total ? 'Showing '.(($page-1)*10+1).'–'.min($page*10,$total).' of '.$total.' results · 10 per page' : 'No Buy, Sell or Hold results are available yet.' ?></p>
 <?php if($total): ?><p class="muted">Sorted: Sell → Buy → Hold. This page: <?=$pageCounts['Sell']??0?> Sell · <?=$pageCounts['Buy']??0?> Buy · <?=$pageCounts['Hold']??0?> Hold. Summary cards count all pages.</p><?php endif; ?>
 <?php if($pages>1): ?><nav class="pagination" aria-label="Research radar pages">
 <?php if($page>1): ?><a data-radar-page href="<?=h(linkTo(array_merge($context,['radarPage'=>$page-1])).'#research-radar')?>">← Previous</a><?php else: ?><span class="muted">← Previous</span><?php endif; ?>
 <span>Page <?=$page?> of <?=$pages?></span>
 <?php if($page<$pages): ?><a data-radar-page href="<?=h(linkTo(array_merge($context,['radarPage'=>$page+1])).'#research-radar')?>">Next →</a><?php else: ?><span class="muted">Next →</span><?php endif; ?>
 </nav><?php endif; ?>
 <div class="radar-list"><?php foreach($slice as $r): ?><a class="radar-row" data-signal="<?=h($r['signal'])?>" href="<?=h(linkTo(['symbol'=>$r['symbol']]))?>"><span><strong><?=h($r['name'])?></strong><small><?=h($r['symbol'])?></small></span><svg viewBox="0 0 600 180" aria-label="Recent daily closing prices; <?=h($r['signal'])?> research signal"><polyline points="<?=h(chartPoints($r['prices']))?>"/></svg><span class="radar-signal"><?=h($r['signal'])?><small><?=h($r['currency'])?> <?=number(end($r['prices']))?> · <?=h($r['lastDate'])?></small></span></a><?php endforeach; ?></div>
 <?php return ['html'=>(string)ob_get_clean(),'page'=>$page,'pages'=>$pages,'total'=>$total,'counts'=>array_count_values(array_column($rows,'signal'))];
}
