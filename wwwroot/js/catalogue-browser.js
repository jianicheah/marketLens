(() => {
 'use strict';
 const explorer=document.querySelector('[data-catalogue-browser]');
 if(!explorer || !window.fetch || !window.AbortController) return;
 const form=explorer.querySelector('[data-catalogue-form]');
 const region=explorer.querySelector('#catalogue-results');
 const status=explorer.querySelector('[data-catalogue-status]');
 if(!form || !region || !status) return;
 let pending;
 async function update(url, push=true) {
  pending?.abort();
  const controller=new AbortController(); pending=controller;
  const request=new URL(url); request.hash=''; request.searchParams.set('action','catalogue'); request.searchParams.delete('view');
  region.setAttribute('aria-busy','true'); status.textContent='Loading listings…'; status.classList.remove('catalogue-error');
  try {
   const response=await fetch(request,{credentials:'same-origin',headers:{Accept:'application/json'},signal:controller.signal});
   const type=response.headers.get('content-type')||'';
   if(!type.includes('application/json')) throw new Error('The server could not update the list. Your current results are kept; please try again.');
   const data=await response.json();
   if(!response.ok || data.error || typeof data.html!=='string') throw new Error(data.error||'Could not update the stock list. Please try again.');
   if(pending!==controller) return;
   // Trusted same-origin server markup; all provider strings are escaped in PHP.
   region.innerHTML=data.html;
   region.querySelector('.stock-results').scrollTop=0;
   form.elements.q.value=data.query; form.elements.market.value=data.market;
   const next=new URL(url); next.searchParams.delete('action'); next.searchParams.delete('view');
   next.searchParams.set('q',data.query); next.searchParams.set('market',data.market); next.searchParams.set('page',String(data.page)); next.hash='';
   if(push) history.pushState({catalogue:true},'',next);
   status.textContent=data.lookup ? `${data.count} Yahoo search results loaded.` : `Page ${data.page} of ${data.pages}. ${data.total.toLocaleString()} matching listings.`;
  } catch(error) {
   if(error.name==='AbortError' || pending!==controller) return;
   status.textContent=error instanceof TypeError ? 'Connection failed. Your current results are kept. Please try again.' : error.message||'Could not update the list. Please try again.'; status.classList.add('catalogue-error');
  } finally { if(pending===controller) {region.setAttribute('aria-busy','false'); pending=null;} }
 }
 form.addEventListener('submit',event=>{
  event.preventDefault();
  const params=new URL(location.href);
  for(const key of ['q','market','page','lookup','action','view']) params.searchParams.delete(key);
  for(const [name,value] of new FormData(form)) params.searchParams.set(name,String(value));
  if(event.submitter?.name==='lookup') params.searchParams.set('lookup','1');
  params.searchParams.set('page','1');
  update(params);
 });
 region.addEventListener('click',event=>{
  const link=event.target.closest('a[data-catalogue-page]');
  if(!link || event.button!==0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
  event.preventDefault(); update(new URL(link.href));
 });
 window.addEventListener('popstate',()=>update(new URL(location.href),false));
})();
