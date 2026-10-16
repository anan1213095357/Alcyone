import { prepareGravityScene, createGravityScene } from './assets/scene-CZLwjmEL.js';

export async function mount(host) {
    await prepareGravityScene();
    // A theme may finish loading after the user has switched to another theme.
    if (!host.isConnected) return;
    return createGravityScene(host, () => {}, () => {
        host.dataset.wallpaperError = 'true';
    }, { miniTool: false });
}
