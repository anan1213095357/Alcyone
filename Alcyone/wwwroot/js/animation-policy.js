(() => {
    let nativePaused = false, paused = document.hidden, pausedAt = paused ? performance.now() : 0;
    let suspendedTime = 0, sequence = 0;
    const frames = new Map(), animations = new Set(), listeners = new Set();
    const now = () => (paused ? pausedAt : performance.now()) - suspendedTime;
    function schedule(id, entry) {
        entry.native = requestAnimationFrame(() => {
            frames.delete(id);
            entry.callback(now());
        });
    }
    function update() {
        const next = nativePaused || document.hidden;
        if (next === paused) return;
        if (next) pausedAt = performance.now();
        else suspendedTime += performance.now() - pausedAt;
        paused = next;
        document.documentElement.classList.toggle('alcyone-animations-paused', paused);
        for (const [id, entry] of frames) {
            if (paused) cancelAnimationFrame(entry.native);
            else schedule(id, entry);
        }
        for (const animation of animations) {
            if (paused && animation.playState === 'running') { animation.pause(); animation.alcyonePaused = true; }
            else if (!paused && animation.alcyonePaused) { animation.alcyonePaused = false; animation.play(); }
        }
        for (const listener of listeners) listener(paused);
    }
    window.alcyoneAnimation = {
        get paused() { return paused; }, now,
        setNativePaused(value) { nativePaused = !!value; update(); },
        requestFrame(callback) {
            const id = ++sequence, entry = { callback, native: 0 };
            frames.set(id, entry);
            if (!paused) schedule(id, entry);
            return id;
        },
        cancelFrame(id) { const entry = frames.get(id); if (entry) cancelAnimationFrame(entry.native); frames.delete(id); },
        track(animation) {
            animations.add(animation);
            if (paused) { animation.pause(); animation.alcyonePaused = true; }
            animation.finished.catch(() => {}).finally(() => animations.delete(animation));
        },
        subscribe(listener) { listeners.add(listener); return () => listeners.delete(listener); }
    };
    document.documentElement.classList.toggle('alcyone-animations-paused', paused);
    document.addEventListener('visibilitychange', update);
})();
