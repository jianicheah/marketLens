(() => {
    const panel = document.querySelector('[data-intraday-quote]');
    if (!panel) return;
    const price = panel.querySelector('[data-quote-price]');
    const status = panel.querySelector('[data-quote-status]');
    const time = panel.querySelector('[data-quote-time]');
    const error = panel.querySelector('[data-quote-error]');
    const formatTime = value => new Intl.DateTimeFormat('en-SG', {
        dateStyle: 'medium', timeStyle: 'medium', timeZone: 'Asia/Singapore'
    }).format(new Date(value)) + ' SGT';
    let timer;
    let running = false;
    const schedule = seconds => { clearTimeout(timer); timer = setTimeout(refresh, Math.max(60, Math.min(3600, seconds)) * 1000); };
    async function refresh() {
        if (running) return;
        if (document.hidden) { schedule(60); return; }
        running = true;
        const controller = new AbortController();
        const timeout = setTimeout(() => controller.abort(), 30000);
        let wait = 300;
        try {
            const response = await fetch(panel.dataset.quoteUrl, { cache: 'no-store', signal: controller.signal });
            if (!response.ok) throw new Error('Quote unavailable');
            const result = await response.json();
            wait = Number.isFinite(result.refreshAfterSeconds) ? result.refreshAfterSeconds : 300;
            error.textContent = result.error || '';
            const quote = result.quote;
            if (quote) {
                price.textContent = quote.currency + ' ' + Number(quote.price).toLocaleString('en-SG', { maximumFractionDigits: 6 });
                const age = Date.now() - new Date(quote.barTime).getTime();
                status.textContent = result.error ? 'Saved snapshot — refresh failed'
                    : quote.marketOpen === false ? 'Market closed — latest available snapshot'
                    : age > 180000 ? 'Delayed or older intraday snapshot'
                    : quote.marketOpen === true ? 'Recent intraday snapshot'
                    : 'Intraday snapshot — market status unconfirmed';
                time.textContent = 'Provider bar time: ' + formatTime(quote.barTime) + ' · Retrieved: ' + formatTime(quote.retrievedAt);
            } else {
                price.textContent = '—';
                status.textContent = 'Intraday price unavailable';
                time.textContent = '';
            }
        } catch (_) {
            status.textContent = 'Quote refresh unavailable';
            error.textContent = 'Could not update the intraday price. Any displayed value is an older snapshot; check its timestamp.';
        } finally {
            clearTimeout(timeout);
            running = false;
            schedule(wait);
        }
    }
    document.addEventListener('visibilitychange', () => {
        if (!document.hidden) { clearTimeout(timer); refresh(); }
    });
    refresh();
})();
