(() => {
    function autoGrow(input) {
        if (!(input instanceof HTMLTextAreaElement)) return;
        input.style.height = 'auto';
        const height = Math.min(180, Math.max(34, input.scrollHeight));
        input.style.height = height + 'px';
        input.style.overflowY = input.scrollHeight > 180 ? 'auto' : 'hidden';
    }
    const pending = new Set();
    let resizeFrame;
    const queueResize = input => {
        pending.add(input);
        if (resizeFrame) return;
        resizeFrame = requestAnimationFrame(() => {
            resizeFrame = null;
            for (const input of pending) if (input.isConnected) window.alcyoneAiInput.resize(input);
            pending.clear();
        });
    };
    document.addEventListener('input', event => { if (event.target.matches?.('[data-auto-grow]')) queueResize(event.target); });
    const observer = new MutationObserver(() => document.querySelectorAll('textarea[data-auto-grow]').forEach(queueResize));
    observer.observe(document.documentElement, {childList:true,subtree:true,characterData:true});
    window.addEventListener('resize', () => document.querySelectorAll('textarea[data-auto-grow]').forEach(queueResize));
    const inputBounds = new WeakMap();
    window.alcyoneAiInput = {
        resizeAll() { document.querySelectorAll("textarea[data-auto-grow]").forEach(queueResize); },
        resize(input) {
            if (!input) return;
            autoGrow(input);
            if (!input.dataset.nativeInput || !window.external?.sendMessage) return;
            const r = input.getBoundingClientRect();
            const geometry = JSON.stringify({id:input.dataset.nativeInput,left:r.left,top:r.top,width:r.width,height:r.height,ratio:window.devicePixelRatio || 1});
            if (inputBounds.get(input) === geometry) return;
            inputBounds.set(input, geometry);
            window.external.sendMessage('alcyone:desktop:text-resize:' + geometry);
        },
        async copyReply(fallback) {
            const selection = window.getSelection();
            const node = selection?.anchorNode?.parentElement;
            const text = node?.closest('.desktop-ai-console .ai-response-body') && selection.toString() ? selection.toString() : fallback;
            if (text) await navigator.clipboard.writeText(text);
        },
        setText(input, text) {
            if (!input || !window.external?.sendMessage) return;
            input.value = text;
            autoGrow(input);
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
