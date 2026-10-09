(() => {
    'use strict';
    const key = 'marketlens.favourites.v1';
    const list = document.getElementById('favourites-list');
    if (!list) return;
    const status = document.getElementById('favourites-status');
    const count = document.getElementById('favourite-count');
    const valid = x => x && typeof x.symbol === 'string' && /^[A-Z]{2}\.[A-Z0-9.\-]{1,25}$/.test(x.symbol) && typeof x.name === 'string' && x.name.length <= 200;
    let favourites = [];
    const read = () => {
        const value = JSON.parse(localStorage.getItem(key) || '[]');
        if (!Array.isArray(value)) throw new Error('Invalid favourites');
        const seen = new Set();
        return value.filter(valid).filter(x => !seen.has(x.symbol) && seen.add(x.symbol)).slice(0, 200);
    };
    const updateButton = button => {
        const active = favourites.some(x => x.symbol === button.dataset.symbol);
        button.textContent = active ? '★ Favourited' : '☆ Favourite';
        button.setAttribute('aria-pressed', String(active));
        button.hidden = false;
    };
    const toggle = item => {
        if (!valid(item)) return;
        try {
            const latest = read();
            const active = latest.some(x => x.symbol === item.symbol);
            if (!active && latest.length >= 200) { status.textContent = 'You can save up to 200 favourites. Remove one first.'; return; }
            const next = active ? latest.filter(x => x.symbol !== item.symbol) : [...latest, item];
            localStorage.setItem(key, JSON.stringify(next));
            favourites = next;
            render();
            status.textContent = 'Favourites saved in this browser. Clearing browser data removes them; they do not sync across devices.';
        } catch { status.textContent = 'Browser storage is unavailable. Your change could not be saved. Allow site storage to use favourites.'; }
    };
    const render = () => {
        list.replaceChildren();
        count.textContent = `${favourites.length} saved`;
        if (!favourites.length) {
            const empty = document.createElement('p'); empty.className = 'empty';
            empty.textContent = 'Save companies with ☆ Favourite. They will appear here above market search.';
            list.append(empty);
        }
        for (const item of favourites) {
            const row = document.createElement('div'); row.className = 'favourite-row';
            const link = document.createElement('a'); link.className = 'stock-row';
            const params = new URLSearchParams({ Symbol: item.symbol, AutoFetch: 'true' });
            link.href = `/?${params}`;
            const name = document.createElement('span'); name.className = 'stock-name';
            const title = document.createElement('strong'); title.textContent = item.name;
            const symbol = document.createElement('small'); symbol.textContent = item.symbol;
            name.append(title, symbol); link.append(name);
            const remove = document.createElement('button'); remove.type = 'button'; remove.className = 'outline';
            remove.textContent = '★'; remove.setAttribute('aria-label', `Remove ${item.name} from favourites`);
            remove.addEventListener('click', () => toggle(item));
            row.append(link, remove); list.append(row);
        }
        document.querySelectorAll('.favourite-toggle').forEach(updateButton);
    };
    document.querySelectorAll('.favourite-toggle').forEach(button => {
        button.addEventListener('click', () => toggle({ symbol: button.dataset.symbol, name: button.dataset.name }));
    });
    try { favourites = read(); } catch { status.textContent = 'Saved favourites could not be read. Check browser site-storage settings.'; }
    render();
    window.addEventListener('storage', event => {
        if (event.key === key || event.key === null) {
            try { favourites = read(); render(); } catch { status.textContent = 'Saved favourites could not be read.'; }
        }
    });
})();
