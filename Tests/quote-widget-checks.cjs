const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const source = fs.readFileSync(require('node:path').join(__dirname, '../wwwroot/js/intraday-quote.js'), 'utf8');

async function main() {
    const elements = Object.fromEntries(['price', 'status', 'time', 'error'].map(name => [name, { textContent: '' }]));
    const panel = { dataset: { quoteUrl: '/?handler=Quote&Symbol=MY.1155' }, querySelector: selector => elements[selector.match(/quote-(\w+)/)[1]] };
    const document = { hidden: false, querySelector: () => panel, addEventListener() {} };
    const timers = new Map();
    let id = 0;
    let requests = 0;
    let result = { quote: { price: 10, currency: 'MYR', barTime: new Date(Date.now() - 60000).toISOString(), retrievedAt: new Date().toISOString(), marketOpen: true }, error: null, refreshAfterSeconds: 60 };
    vm.runInNewContext(source, {
        document, Date, Intl, Number, AbortController,
        setTimeout: (callback, milliseconds) => { timers.set(++id, { callback, milliseconds }); return id; },
        clearTimeout: timer => timers.delete(timer),
        fetch: async (url, options) => { requests++; assert.equal(options.cache, 'no-store'); assert.match(url, /MY\.1155/); return { ok: true, json: async () => result }; }
    });
    const flush = () => new Promise(setImmediate);
    await flush();
    assert.equal(elements.price.textContent, 'MYR 10');
    assert.equal(elements.status.textContent, 'Recent intraday snapshot');
    assert.match(elements.time.textContent, /Provider bar time:.*SGT/);
    async function tick() {
        const timer = [...timers.values()].find(value => value.milliseconds >= 60000);
        assert.ok(timer); timers.clear(); timer.callback(); await flush();
    }
    result = { ...result, error: 'Plan access unavailable', refreshAfterSeconds: 300 };
    await tick();
    assert.equal(elements.status.textContent, 'Saved snapshot — refresh failed');
    assert.equal(elements.error.textContent, 'Plan access unavailable');
    const beforeHidden = requests;
    document.hidden = true;
    await tick();
    assert.equal(requests, beforeHidden, 'Hidden tab sent a quote request');
    document.hidden = false;
    result = { quote: null, error: 'Exchange unavailable', refreshAfterSeconds: 300 };
    await tick();
    assert.equal(elements.price.textContent, '—');
    assert.equal(elements.status.textContent, 'Intraday price unavailable');
    console.log('PASS quote widget: recent/stale/unavailable labels, provider timestamps, hidden-tab pause and shared-cache request mode');
}
main().catch(error => { console.error(error); process.exitCode = 1; });
