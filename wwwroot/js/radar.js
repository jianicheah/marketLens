// Read saved screening results without fetching prices or reloading the detail panel.
(() => {
    async function refresh() {
        const current = document.querySelector('.signal-overview');
        if (!current || document.hidden || current.contains(document.activeElement)) return;
        const url = new URL(window.location.href);
        url.searchParams.set('handler', 'Radar');
        url.hash = '';
        try {
            const response = await fetch(url, { cache: 'no-store' });
            if (!response.ok) return;
            const next = new DOMParser().parseFromString(await response.text(), 'text/html').querySelector('.signal-overview');
            if (!next) return;
            const scrollTop = current.querySelector('.signal-chart')?.scrollTop ?? 0;
            current.replaceWith(next);
            const chart = next.querySelector('.signal-chart');
            if (chart) chart.scrollTop = scrollTop;
        } catch { /* Keep the last visible result during connection failures. */ }
    }
    setInterval(refresh, 30000);
})();
