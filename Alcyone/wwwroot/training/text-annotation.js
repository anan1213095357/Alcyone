window.ftfCanvas = (() => {
    let canvas, ctx, dotnet, image, seeds = [], rect = {}, selected = -1, selectionTool = false, zoom = 2, disabled = false;
    let start = null, dragIndex = -1, preview = null;
    let magnifier, magnifierCanvas, magnifierContext, magnifierInfo, hoverPoint = null, keyboardTracking = false;
    let pointerX = 0, pointerY = 0;
    let frameUrl = '', hasCapture = false, frameTimer = null, frameInFlight = false, generation = 0, previewError = '';
    const sampleCanvas = document.createElement('canvas');
    sampleCanvas.width = sampleCanvas.height = 1;
    const sampleContext = sampleCanvas.getContext('2d', { willReadFrequently: true });
    const events = [];
    function init(id, reference, url = '') {
        dispose(); canvas = document.getElementById(id); ctx = canvas.getContext('2d'); dotnet = reference;
        magnifier = document.getElementById('textPixelMagnifier');
        magnifierCanvas = document.getElementById('textMagnifierCanvas');
        magnifierContext = magnifierCanvas.getContext('2d');
        magnifierInfo = document.getElementById('textMagnifierInfo');
        frameUrl = url; scheduleFrame();
        for (const [name, handler] of [['pointerdown', down], ['pointermove', move], ['pointerup', up], ['pointercancel', cancel], ['pointerleave', leave], ['blur', leave], ['keydown', key]]) {
            canvas.addEventListener(name, handler); events.push([name, handler]);
        }
        window.addEventListener('scroll', leave, true);
        window.addEventListener('resize', leave);
    }
    async function setImage(src) {
        const current = ++generation; frameInFlight = false;
        const next = new Image(); next.src = src; await next.decode();
        if (current !== generation || !canvas) return;
        image = next; leave(); render(); scheduleFrame();
    }
    function setState(points, selection, index, tool, scale, busy, captureWidth = 0) {
        seeds = points; rect = selection; selected = index; selectionTool = tool; zoom = scale; disabled = busy;
        hasCapture = captureWidth > 0;
        if (keyboardTracking && seeds[selected]) hoverPoint = { x: seeds[selected].x, y: seeds[selected].y };
        render(); updateMagnifier();
    }
    function scheduleFrame() {
        clearTimeout(frameTimer);
        if (frameUrl && canvas) frameTimer = setTimeout(updateFrame, 100);
    }
    async function updateFrame() {
        const current = generation;
        if (!canvas || !hasCapture || start || frameInFlight || !canvas.offsetParent) { scheduleFrame(); return; }
        frameInFlight = true;
        let objectUrl;
        try {
            const response = await fetch(frameUrl + '?t=' + Date.now(), { cache: 'no-store' });
            if (response.status === 204) return;
            if (!response.ok) { const detail = await response.json(); throw new Error(detail.detail || '实时截图失败'); }
            objectUrl = URL.createObjectURL(await response.blob());
            const next = new Image(); next.src = objectUrl; await next.decode();
            if (current !== generation || !canvas || start) return;
            image = next; render(); updateMagnifier(); previewError = '';
        } catch (error) {
            if (current === generation && previewError !== error.message) { previewError = error.message; call('OnTextPreviewError', previewError); }
        } finally {
            if (objectUrl) URL.revokeObjectURL(objectUrl);
            if (current === generation) { frameInFlight = false; scheduleFrame(); }
        }
    }
    function render() {
        if (!canvas || !image) return;
        if (canvas.width !== image.width * zoom) canvas.width = image.width * zoom;
        if (canvas.height !== image.height * zoom) canvas.height = image.height * zoom;
        ctx.imageSmoothingEnabled = false; ctx.drawImage(image, 0, 0, canvas.width, canvas.height);
        const r = preview || rect;
        if (r.width > 0 && r.height > 0) {
            ctx.fillStyle = 'rgba(108,159,255,.12)'; ctx.fillRect(r.x * zoom, r.y * zoom, r.width * zoom, r.height * zoom);
            ctx.strokeStyle = '#ffd35a'; ctx.lineWidth = 1; ctx.strokeRect(r.x * zoom + .5, r.y * zoom + .5, r.width * zoom, r.height * zoom);
        }
        seeds.forEach((p, i) => {
            const x = (p.x + .5) * zoom, y = (p.y + .5) * zoom;
            ctx.strokeStyle = i === selected ? '#ffd35a' : '#45d6a7'; ctx.lineWidth = 1;
            ctx.beginPath(); ctx.arc(x, y, 5, 0, Math.PI * 2); ctx.moveTo(x - 9, y); ctx.lineTo(x + 9, y); ctx.moveTo(x, y - 9); ctx.lineTo(x, y + 9); ctx.stroke();
            ctx.font = '12px Segoe UI'; ctx.fillStyle = '#000'; ctx.fillRect(x + 7, y - 20, 30, 16);
            ctx.fillStyle = '#fff'; ctx.fillText('P' + (i + 1), x + 9, y - 8);
        });
        canvas.style.cursor = disabled ? 'wait' : 'crosshair';
    }
    function point(e) {
        const r = canvas.getBoundingClientRect();
        return { x:Math.max(0, Math.min(image.width - 1, Math.floor((e.clientX - r.left) * image.width / r.width))),
            y:Math.max(0, Math.min(image.height - 1, Math.floor((e.clientY - r.top) * image.height / r.height))) };
    }
    function bounds(a, b) { return { x:Math.min(a.x,b.x), y:Math.min(a.y,b.y), width:Math.abs(a.x-b.x)+1, height:Math.abs(a.y-b.y)+1 }; }
    function trackPointer(e) {
        hoverPoint = point(e); pointerX = e.clientX; pointerY = e.clientY; keyboardTracking = false;
    }
    function updateMagnifier() {
        if (!magnifier || !image || !hoverPoint || disabled) { if (magnifier) magnifier.hidden = true; return; }
        const size = 15, cell = 12, half = 7, p = hoverPoint;
        // 仅从原始截图取像素，不把标定标记或框选遮罩带入放大镜。
        magnifierContext.fillStyle = '#172233'; magnifierContext.fillRect(0, 0, 180, 180);
        magnifierContext.imageSmoothingEnabled = false;
        magnifierContext.drawImage(image, p.x - half, p.y - half, size, size, 0, 0, 180, 180);
        magnifierContext.strokeStyle = 'rgba(130,146,170,.35)'; magnifierContext.lineWidth = 1;
        magnifierContext.beginPath();
        for (let i = 0; i <= size; i++) {
            const offset = i * cell + .5;
            magnifierContext.moveTo(offset, 0); magnifierContext.lineTo(offset, 180);
            magnifierContext.moveTo(0, offset); magnifierContext.lineTo(180, offset);
        }
        magnifierContext.stroke();
        magnifierContext.strokeStyle = '#ff4d68'; magnifierContext.lineWidth = 2;
        magnifierContext.strokeRect(half * cell + 1, half * cell + 1, cell - 2, cell - 2);
        sampleContext.clearRect(0, 0, 1, 1); sampleContext.drawImage(image, p.x, p.y, 1, 1, 0, 0, 1, 1);
        const rgb = sampleContext.getImageData(0, 0, 1, 1).data;
        const hex = '#' + Array.from(rgb).slice(0, 3).map(v => v.toString(16).padStart(2, '0')).join('').toUpperCase();
        magnifierInfo.textContent = `X ${p.x}  Y ${p.y}\nRGB ${rgb[0]}, ${rgb[1]}, ${rgb[2]}  ${hex}`;
        magnifier.hidden = false;
        const bounds = canvas.closest('.text-finder').getBoundingClientRect();
        const width = magnifier.offsetWidth, height = magnifier.offsetHeight;
        const right = Math.min(window.innerWidth, bounds.right), bottom = Math.min(window.innerHeight, bounds.bottom);
        let left = pointerX + 22, top = pointerY + 22;
        if (left + width > right - 8) left = pointerX - width - 22;
        if (top + height > bottom - 8) top = pointerY - height - 22;
        magnifier.style.left = Math.max(bounds.left + 8, Math.min(left, right - width - 8)) + 'px';
        magnifier.style.top = Math.max(bounds.top + 8, Math.min(top, bottom - height - 8)) + 'px';
    }
    function leave() { hoverPoint = null; keyboardTracking = false; if (magnifier) magnifier.hidden = true; }
    function call(method, ...args) { if (dotnet) dotnet.invokeMethodAsync(method, ...args).catch(() => {}); }
    function notifyPoint(p, index) {
        // 发送画面实际显示的颜色，避免实时刷新时服务端的下一帧取色错位。
        sampleContext.clearRect(0, 0, 1, 1); sampleContext.drawImage(image, p.x, p.y, 1, 1, 0, 0, 1, 1);
        const rgb = sampleContext.getImageData(0, 0, 1, 1).data;
        call('OnTextPoint', p.x, p.y, index, rgb[0], rgb[1], rgb[2]);
    }
    function down(e) {
        if (disabled || !image || e.button !== 0) return;
        trackPointer(e);
        canvas.focus(); start = point(e); canvas.setPointerCapture(e.pointerId);
        if (selectionTool) { preview = bounds(start, start); }
        else {
            dragIndex = seeds.findIndex(p => Math.abs(p.x - start.x) * zoom <= 7 && Math.abs(p.y - start.y) * zoom <= 7);
            if (dragIndex >= 0) { selected = dragIndex; call('OnTextSeedSelected', dragIndex); }
        }
        render(); updateMagnifier();
    }
    function move(e) {
        if (!image || disabled) { leave(); return; }
        trackPointer(e);
        if (start) {
            if (selectionTool) preview = bounds(start, hoverPoint);
            else if (dragIndex >= 0) seeds[dragIndex] = { ...seeds[dragIndex], ...hoverPoint };
            render();
        }
        updateMagnifier();
    }
    function up(e) {
        if (!start) return;
        if (!disabled) {
            const p = point(e);
            trackPointer(e);
            if (selectionTool) { rect = bounds(start, p); call('OnTextSelection', rect.x, rect.y, rect.width, rect.height); }
            else notifyPoint(p, dragIndex);
        }
        cancel();
        if (canvas.hasPointerCapture(e.pointerId)) canvas.releasePointerCapture(e.pointerId);
        updateMagnifier();
    }
    function cancel() { start = null; preview = null; dragIndex = -1; render(); }
    function key(e) {
        if (disabled || !image || selected < 0 || !seeds[selected]) return;
        const directions = { ArrowLeft:[-1,0], ArrowRight:[1,0], ArrowUp:[0,-1], ArrowDown:[0,1] };
        const d = directions[e.key]; if (!d) return; e.preventDefault();
        const p = seeds[selected], distance = e.shiftKey ? 5 : 1;
        hoverPoint = { x: Math.max(0, Math.min(image.width-1, p.x+d[0]*distance)), y: Math.max(0, Math.min(image.height-1, p.y+d[1]*distance)) };
        keyboardTracking = true; seeds[selected] = { ...p, ...hoverPoint };
        const area = canvas.getBoundingClientRect(); pointerX = area.left + (hoverPoint.x + .5) * area.width / image.width;
        pointerY = area.top + (hoverPoint.y + .5) * area.height / image.height;
        render(); updateMagnifier();
        notifyPoint(hoverPoint, selected);
    }
    function dispose() {
        generation++; clearTimeout(frameTimer); frameTimer = null; frameUrl = ''; frameInFlight = false; previewError = '';
        if (canvas) events.forEach(([name, handler]) => canvas.removeEventListener(name, handler));
        window.removeEventListener('scroll', leave, true); window.removeEventListener('resize', leave); leave();
        events.length = 0; canvas = ctx = dotnet = image = null; seeds = []; rect = {}; selected = -1; start = preview = null;
        magnifier = magnifierCanvas = magnifierContext = magnifierInfo = null;
    }
    return { init, setImage, setState, dispose };
})();

window.ftfFiles = {
    downloadUrl(url) {
        const link = document.createElement('a'); link.href = url; link.download = '';
        document.body.appendChild(link); link.click(); link.remove();
    },
    async copy(text) {
        try { await navigator.clipboard.writeText(text); }
        catch {
            const area = document.createElement('textarea'); area.value = text;
            area.style.position = 'fixed'; area.style.opacity = '0'; document.body.appendChild(area); area.select();
            const copied = document.execCommand('copy'); area.remove();
            if (!copied) throw new Error('复制失败，请从调用代码预览中手动复制。');
        }
    }
};
