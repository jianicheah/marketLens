(() => {
 'use strict';
 const key = 'marketlens-theme';
 const root = document.documentElement;
 const system = window.matchMedia('(prefers-color-scheme: dark)');
 let preference = 'system';
 try { const saved = localStorage.getItem(key); if (['light','dark','system'].includes(saved)) preference = saved; } catch {}
 function apply() {
  root.dataset.theme = preference === 'system' ? (system.matches ? 'dark' : 'light') : preference;
  for (const button of document.querySelectorAll('[data-theme-toggle]')) {
   const label = root.dataset.theme === 'dark' ? 'Switch to light mode' : 'Switch to dark mode';
   button.setAttribute('aria-label', label);
   button.title = label;
  }
 }
 apply();
 system.addEventListener('change', () => { if (preference === 'system') apply(); });
 function bind() {
  for (const button of document.querySelectorAll('[data-theme-toggle]')) {
   button.hidden = false;
   button.addEventListener('click', () => {
    preference = root.dataset.theme === 'dark' ? 'light' : 'dark';
    try { localStorage.setItem(key, preference); } catch {}
    apply();
   });
  }
  apply();
 }
 if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', bind, {once:true}); else bind();
 window.addEventListener('storage', event => {
  if (event.key !== key) return;
  preference = ['light','dark','system'].includes(event.newValue) ? event.newValue : 'system';
  apply();
 });
})();
