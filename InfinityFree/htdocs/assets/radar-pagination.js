(() => {
  const region = document.getElementById('radar-results');
  const status = document.getElementById('radar-page-status');
  if (!region || !status) return;
  let pending;
  let page = Number(region.querySelector('[data-radar-current]')?.dataset.radarCurrent || 1);
  async function update(requested = page, automatic = false) {
    if (automatic && (pending || document.hidden)) return;
    pending?.abort();
    const controller = pending = new AbortController();
    const url = new URL(location.href);
    url.searchParams.set('radarPage', requested);
    url.searchParams.set('action', 'radar-page');
    region.setAttribute('aria-busy', 'true');
    if (!automatic) status.textContent = 'Loading radar page…';
    try {
      const response = await fetch(url, {signal: controller.signal, credentials: 'same-origin'});
      if (!response.ok) throw new Error('Request failed');
      const data = await response.json();
      if (typeof data.html !== 'string' || controller.signal.aborted) throw new Error('Invalid response');
      const scroll = window.scrollY;
      // Leave unchanged rows and keyboard focus intact on routine refreshes.
      if (region.innerHTML !== data.html) {
        const focused = region.contains(document.activeElement) ? document.activeElement?.getAttribute('href') : null;
        region.innerHTML = data.html;
        if (focused) Array.from(region.querySelectorAll('a')).find(a => a.getAttribute('href') === focused)?.focus({preventScroll: true});
      }
      page = data.page;
      document.querySelectorAll('#research-radar .totals [data-signal]').forEach(card => {
        card.querySelector('strong').textContent = data.counts[card.dataset.signal] || 0;
      });
      status.textContent = `Page ${data.page} of ${data.pages} · totals and list updated together at ${new Date().toLocaleTimeString()}.`;
      window.scrollTo({top: scroll, behavior: 'instant'});
    } catch (error) {
      if (error.name !== 'AbortError') status.textContent = 'Could not update radar. Showing previously loaded results; will retry.';
    } finally {
      if (pending === controller) { region.setAttribute('aria-busy', 'false'); pending = null; }
    }
  }
  region.addEventListener('click', event => {
    const link = event.target.closest('a[data-radar-page]');
    if (!link || event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
    event.preventDefault();
    update(Number(new URL(link.href).searchParams.get('radarPage')));
  });
  document.addEventListener('marketlens:screened', () => update(page, true));
  document.addEventListener('visibilitychange', () => { if (!document.hidden) update(page, true); });
  setInterval(() => update(page, true), 60000);
})();
