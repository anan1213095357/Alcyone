(() => {
    window.alcyoneAiInput = {
        setText(input, text) {
            if (!input || !window.external?.sendMessage) return;
            input.value = text;
            const r = input.getBoundingClientRect();
            window.external.sendMessage('alcyone:desktop:text-input:' + JSON.stringify({
                id: input.dataset.nativeInput, text, left: r.left, top: r.top,
                width: r.width, height: r.height, ratio: window.devicePixelRatio || 1
            }));
        }
    };
    function scrollAt(x, y, delta) {
        let node = document.elementFromPoint(x, y);
        if (!node?.closest('.desktop-ai-console')) return false;
        for (; node && !node.matches('.desktop-ai-console'); node = node.parentElement) {
            const style = getComputedStyle(node);
            if (!/auto|scroll/.test(style.overflowY) || node.scrollHeight <= node.clientHeight + 1) continue;
            const before = node.scrollTop;
            node.scrollTop += delta;
            if (node.scrollTop !== before) return true;
        }
        return true;
    }
    document.addEventListener('wheel', event => {
        if (!event.target.closest?.('.desktop-ai-console')) return;
        const delta = event.deltaY * (event.deltaMode === 1 ? 24 : event.deltaMode === 2 ? innerHeight : 1);
        scrollAt(event.clientX, event.clientY, delta);
        event.preventDefault(); event.stopPropagation();
    }, { capture: true, passive: false });
    window.external?.receiveMessage?.(message => {
        const prefix = 'alcyone:ai:wheel:';
        if (typeof message !== 'string' || !message.startsWith(prefix)) return;
        try { const wheel = JSON.parse(message.slice(prefix.length)); scrollAt(wheel.x, wheel.y, -wheel.delta * .8); } catch { }
    });
})();
