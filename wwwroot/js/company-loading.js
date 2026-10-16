(() => {
    document.addEventListener('click', event => {
        const link = event.target.closest('a.company-view');
        if (!link || event.defaultPrevented || event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
        if (link.lastElementChild) link.lastElementChild.textContent = 'Loading…';
        link.setAttribute('aria-busy', 'true');
    });
    window.addEventListener('pageshow', () => {
        document.querySelectorAll('a.company-view[aria-busy]').forEach(link => {
            link.removeAttribute('aria-busy');
            if (link.lastElementChild) link.lastElementChild.textContent = 'View →';
        });
    });
})();
