(() => {
 'use strict';
 const key='marketlens-php-favourites-v1';
 const valid=s=>typeof s==='string'&&/^[A-Z]{2}\.[A-Z0-9][A-Z0-9.\-]{0,24}$/.test(s);
 function load(){try{const a=JSON.parse(localStorage.getItem(key)||'[]');return Array.isArray(a)?a.filter(x=>x&&valid(x.symbol)&&typeof x.name==='string').slice(0,100):[];}catch{return [];}}
 function render(){const list=document.getElementById('favourites');const saved=load();if(list){list.replaceChildren();for(const row of saved){const a=document.createElement('a');a.href='index.php?symbol='+encodeURIComponent(row.symbol);a.textContent=row.name+' · '+row.symbol;list.append(a);}if(!saved.length)list.textContent='No favourites yet.';}const b=document.getElementById('favourite-toggle');if(b){const on=saved.some(x=>x.symbol===b.dataset.symbol);b.textContent=on?'★ Remove favourite':'☆ Favourite';b.setAttribute('aria-pressed',String(on));}}
 document.getElementById('favourite-toggle')?.addEventListener('click',e=>{const b=e.currentTarget;let saved=load();if(saved.some(x=>x.symbol===b.dataset.symbol))saved=saved.filter(x=>x.symbol!==b.dataset.symbol);else saved.push({symbol:b.dataset.symbol,name:b.dataset.name});try{localStorage.setItem(key,JSON.stringify(saved.slice(-100)));render();}catch{b.textContent='Browser storage unavailable';}});
 render();
 const status=document.getElementById('radar-status');let busy=false;
 async function screen(){if(!status||document.hidden||busy)return;busy=true;try{const r=await fetch('index.php?action=screen',{method:'POST',credentials:'same-origin',headers:{'X-CSRF-Token':document.querySelector('meta[name=csrf-token]').content}});const j=await r.json();status.textContent=(j.message||j.error||'Screening unavailable.');if(r.ok)document.dispatchEvent(new Event('marketlens:screened'));}catch{status.textContent='Automatic screening could not connect. You can still open individual listings.';}finally{busy=false;}}
 // Delay the first check so a visitor's chosen listing gets priority in the shared budget.
 if(status){setTimeout(screen,20000);setInterval(screen,65000);}
})();
