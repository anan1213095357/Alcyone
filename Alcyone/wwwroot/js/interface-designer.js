const sessions = new WeakMap();
export function attach(root, bridge) {
    const abort = new AbortController(), signal = abort.signal;
    let drag = null, frame = 0;
    const draw = () => {
        frame = 0;
        if (!drag) return;
        const d = drag, dx = d.clientX - d.startX, dy = d.clientY - d.startY;
        d.x = d.resize ? d.left : Math.max(0, Math.min(d.board.clientWidth - d.width, d.left + dx));
        d.y = d.resize ? d.top : Math.max(0, Math.min(d.board.clientHeight - d.height, d.top + dy));
        d.w = d.resize ? Math.max(24, Math.min(d.board.clientWidth - d.x, d.width + dx)) : d.width;
        d.h = d.resize ? Math.max(24, Math.min(d.board.clientHeight - d.y, d.height + dy)) : d.height;
        Object.assign(d.node.style, {left:d.x+'px',top:d.y+'px',width:d.w+'px',height:d.h+'px'});
    };
    document.addEventListener('selectstart',e=>{if(drag)e.preventDefault();},{signal});
    root.addEventListener('pointerdown', e => {
        const node = e.target.closest('[data-ui-id]');
        if (e.target.closest('.design-control-delete')) return;
        if (!node || node.dataset.preview === 'true' || e.button !== 0) return;
        e.preventDefault(); e.stopPropagation(); window.getSelection()?.removeAllRanges();
        const board = node.closest('.design-board');
        drag = {node,board,resize:!!e.target.closest('[data-ui-resize]'),left:node.offsetLeft,top:node.offsetTop,width:node.offsetWidth,height:node.offsetHeight,startX:e.clientX,startY:e.clientY,clientX:e.clientX,clientY:e.clientY,pointer:e.pointerId};
        node.setPointerCapture(e.pointerId);
        root.querySelectorAll('.design-control.selected').forEach(n=>n.classList.remove('selected'));
        node.classList.add('selected');
    }, {signal});
    root.addEventListener('pointermove', e => {
        if (!drag || drag.pointer !== e.pointerId) return;
        drag.clientX=e.clientX; drag.clientY=e.clientY;
        if (!frame) frame=requestAnimationFrame(draw);
    }, {signal});
    const finish = e => {
        if (!drag || drag.pointer !== e.pointerId) return;
        cancelAnimationFrame(frame); draw();
        const d=drag; drag=null;
        bridge.invokeMethodAsync('CommitControl',d.board.dataset.screenId,d.node.dataset.uiId,d.x,d.y,d.w,d.h).catch(()=>{});
    };
    root.addEventListener('pointerup',finish,{signal}); root.addEventListener('pointercancel',finish,{signal});
    root.addEventListener('dragstart',e=>{const item=e.target.closest('[data-control-kind]'); if(item)e.dataTransfer.setData('application/alcyone-control',item.dataset.controlKind);},{signal});
    root.addEventListener('dragover',e=>{if(e.target.closest('.design-board:not(.preview)')) e.preventDefault();},{signal});
    root.addEventListener('drop',e=>{const board=e.target.closest('.design-board:not(.preview)'),kind=e.dataTransfer.getData('application/alcyone-control');if(!board||!kind)return;e.preventDefault();const r=board.getBoundingClientRect();bridge.invokeMethodAsync('DropControl',kind,e.clientX-r.left,e.clientY-r.top).catch(()=>{});},{signal});
    sessions.set(root,()=>{abort.abort();cancelAnimationFrame(frame);});
}
export function detach(root) { sessions.get(root)?.(); sessions.delete(root); }
