const watchers = new WeakMap();
export function watch(root) {
    unwatch(root);
    let frame = 0;
    const scroll = () => {
        if (frame) return;
        frame = requestAnimationFrame(() => {
            frame = 0;
            for (const view of root.querySelectorAll('[data-ai-thought-scroll]'))
                view.scrollTop = view.scrollHeight;
        });
    };
    const observer = new MutationObserver(scroll);
    observer.observe(root, { childList: true, subtree: true, characterData: true });
    root.addEventListener('toggle', scroll, true);
    watchers.set(root, () => { observer.disconnect(); root.removeEventListener('toggle', scroll, true); cancelAnimationFrame(frame); });
    scroll();
}
export function unwatch(root) { watchers.get(root)?.(); watchers.delete(root); }
