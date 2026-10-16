export function mount(host, path) {
    let disposed = false, cleanup;
    import(new URL(path, document.baseURI).href).then(module => module.mount(host)).then(stop => {
        if (disposed) stop?.();
        else cleanup = stop;
    }).catch(error => {
        console.error('Wallpaper could not be loaded', error);
        host.dataset.wallpaperError = 'true';
    });
    return { dispose() { disposed = true; cleanup?.(); host.replaceChildren(); } };
}
