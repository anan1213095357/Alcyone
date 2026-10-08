const channel = 'state-machine-training-v1';
let editorListener, editorReference;
const pending = new Map();

export function connectEditor(reference) {
    editorReference = reference;
    editorListener = async event => {
        if (event.origin !== location.origin || event.source !== parent || event.data?.channel !== channel) return;
        const message = event.data;
        if (message.type === 'initialize') await reference.invokeMethodAsync('InitializeEditor', message.initial);
        if (message.type === 'saved') {
            const request = pending.get(message.id);
            if (!request) return;
            pending.delete(message.id); clearTimeout(request.timer);
            if (message.error) request.reject(new Error(message.error));
            else request.resolve(message.targetId);
        }
    };
    addEventListener('message', editorListener);
    parent.postMessage({ channel, type: 'ready' }, location.origin);
}
export function save(result) {
    return new Promise((resolve, reject) => {
        const id = crypto.randomUUID();
        const timer = setTimeout(() => { pending.delete(id); reject(new Error('保存超时，请检查状态机连接后重试。')); }, 60000);
        pending.set(id, { resolve, reject, timer });
        parent.postMessage({ channel, type: 'save', id, result }, location.origin);
    });
}
export function closeEditor() { parent.postMessage({ channel, type: 'close' }, location.origin); }
export function disconnectEditor() {
    removeEventListener('message', editorListener);
    for (const request of pending.values()) { clearTimeout(request.timer); request.reject(new Error('编辑器已关闭。')); }
    pending.clear(); editorReference = null;
}
export function connectHost(frame, reference, initial) {
    const initialize = () => frame.contentWindow?.postMessage({ channel, type: 'initialize', initial }, location.origin);
    const listener = async event => {
        if (event.origin !== location.origin || event.source !== frame.contentWindow || event.data?.channel !== channel) return;
        const message = event.data;
        if (message.type === 'ready') initialize();
        if (message.type === 'close') await reference.invokeMethodAsync('CloseTraining');
        if (message.type === 'save') {
            let targetId, error;
            try { targetId = await reference.invokeMethodAsync('SaveTraining', message.result); }
            catch (failure) { error = String(failure.message ?? failure); }
            frame.contentWindow?.postMessage({ channel, type: 'saved', id: message.id, targetId, error }, location.origin);
        }
    };
    addEventListener('message', listener);
    initialize();
    return { dispose() { removeEventListener('message', listener); } };
}
