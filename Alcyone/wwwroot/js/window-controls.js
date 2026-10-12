(() => {
    // A selection/drag ending outside a dialog produces a click on its backdrop.
    // Dismiss only a stationary press that also began on that exact backdrop.
    let dismissPress;
    document.addEventListener('pointerdown', event => {
        if (event.isPrimary !== false) dismissPress = { target: event.target, x: event.clientX, y: event.clientY };
    }, true);
    document.addEventListener('pointercancel', () => dismissPress = null, true);
    document.addEventListener('click', event => {
        if (!(event.target instanceof Element) || !event.target.matches('[data-overlay-dismiss]')) return;
        const press = dismissPress;
        dismissPress = null;
        if (event.detail !== 0 && (!press || press.target !== event.target || Math.hypot(event.clientX - press.x, event.clientY - press.y) > 6)) {
            event.preventDefault();
            event.stopImmediatePropagation();
        }
    }, true);
    const send = command => window.external?.sendMessage?.(`alcyone:window:${command}`);
    const dragArea = target => target instanceof Element && target.closest('.topbar') &&
        !target.closest('button,input,select,textarea,a,label,[role="button"],.config-menu,.app-settings-menu,[data-no-window-drag]');
    let origin;
    document.addEventListener('pointerdown', event => {
        const edge = event.target instanceof Element && event.target.closest('[data-window-resize]');
        if (edge && event.button === 0) {
            event.preventDefault();
            origin = null;
            send(`resize:${edge.dataset.windowResize}`);
            return;
        }
        if (event.button === 0 && dragArea(event.target) && window.external?.sendMessage)
            origin = { x: event.screenX, y: event.screenY };
    });
    document.addEventListener('pointermove', event => {
        if (!origin) return;
        if (!(event.buttons & 1)) { origin = null; return; }
        if (Math.hypot(event.screenX - origin.x, event.screenY - origin.y) < 4) return;
        origin = null;
        send('drag');
    });
    document.addEventListener('pointerup', () => origin = null);
    document.addEventListener('pointercancel', () => origin = null);
    window.addEventListener('blur', () => origin = null);
    window.addEventListener('focus', () => send('state'));
    // Blazor renders the native controls after the initial document load.
    const observer = new MutationObserver(() => {
        if (!document.querySelector('.window-controls')) return;
        send('state');
        observer.disconnect();
    });
    observer.observe(document.documentElement, { childList: true, subtree: true });
    let resizeTimer;
    window.addEventListener('resize', () => {
        clearTimeout(resizeTimer);
        resizeTimer = setTimeout(() => send('state'), 100);
    });
    document.addEventListener('dblclick', event => {
        if (event.button === 0 && dragArea(event.target)) send('maximize');
    });
    document.addEventListener('click', event => {
        const button = event.target instanceof Element && event.target.closest('[data-window-command]');
        if (button) send(button.dataset.windowCommand);
    });
})();
