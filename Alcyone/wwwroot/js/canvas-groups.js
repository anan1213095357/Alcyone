(() => {
    let api, layer, box, groups = [], selected = new Set(), gesture = null, confirmation = null;
    let configKey = null, busy = false, epoch = 0, active = new Set();
    let collapseButton;
    let popupAnchor = null, lastSelectionEnd = null, transition = null;
    const animations = new Set();
    const reducedMotion = () => matchMedia('(prefers-reduced-motion: reduce)').matches;
    const english = () => document.documentElement.lang === 'en';
    const cards = () => [...api.world.querySelectorAll('.state-card')];
    const cardFor = id => api.world.querySelector(`.state-card[data-state-id="${CSS.escape(id)}"]`);
    const groupFor = id => groups.find(g => g.stateIds.includes(id));
    const point = event => {
        const rect = api.world.getBoundingClientRect(), zoom = api.zoom();
        return { x: (event.clientX - rect.left) / zoom, y: (event.clientY - rect.top) / zoom };
    };
    function controls() {
        if (collapseButton) {
            collapseButton.disabled = selected.size === 0 || busy;
            collapseButton.textContent = (english() ? 'Fold into Alcyone' : '收纳为 Alcyone') + (selected.size ? ` · ${selected.size}` : '');
        }
        for (const card of cards()) card.classList.toggle('box-selected', selected.has(card.dataset.stateId));
    }
    function render() {
        if (!api) return;
        const existing = new Set(cards().map(c => c.dataset.stateId));
        groups = groups.map(g => ({ ...g, stateIds: g.stateIds.filter(id => existing.has(id)) })).filter(g => g.stateIds.length);
        selected = new Set([...selected].filter(id => existing.has(id) && !groupFor(id)));
        for (const card of cards()) card.hidden = !!groupFor(card.dataset.stateId) && !transition?.members.has(card.dataset.stateId);
        for (const orb of [...layer.children]) if (!groups.some(g => g.id === orb.dataset.groupId)) {
            window.alcyonePlanets.detach(orb.querySelector('canvas')); orb.remove();
        }
        for (const group of groups) {
            let orb = [...layer.children].find(node => node.dataset.groupId === group.id);
            if (!orb) {
                orb = document.createElement('div');
                orb.className = 'alcyone-orb'; orb.dataset.groupId = group.id;
                orb.innerHTML = '<span class="orb-halo"></span><button type="button" class="orb-surface"><canvas class="planet-disc" width="320" height="320" aria-hidden="true"></canvas></button><button type="button" class="orb-name"></button><span class="orb-count"></span><span class="orb-hint"></span>';
                window.alcyonePlanets.attach(orb.querySelector('canvas'), group.id);
                orb.querySelector('.orb-surface').addEventListener('click', event => {
                    event.stopPropagation();
                    if (performance.now() < (orb.suppressUntil || 0)) return;
                    void expand(orb.dataset.groupId);
                });
                orb.querySelector('.orb-name').addEventListener('click', event => {
                    event.stopPropagation();
                    requestRename(orb.dataset.groupId);
                });
                layer.append(orb);
            }
            orb.style.left = `${group.x}px`; orb.style.top = `${group.y}px`;
            orb.classList.toggle('is-running', group.stateIds.some(id => active.has(id)));
            window.alcyonePlanets.setActive(orb.querySelector('canvas'), group.stateIds.some(id => active.has(id)));
            const count = english() ? `${group.stateIds.length} STATES` : `${group.stateIds.length} 个状态`;
            orb.querySelector('.orb-count').textContent = count;
            const name = group.name?.trim() || 'Alcyone';
            orb.querySelector('.orb-name').textContent = name;
            orb.querySelector('.orb-name').title = english() ? 'Rename planet' : '点击重命名星球';
            orb.querySelector('.orb-name').setAttribute('aria-label', `${english() ? 'Rename' : '重命名'} ${name}`);
            orb.querySelector('.orb-hint').textContent = english() ? 'EXPAND · DRAG TO MOVE' : '点击星球展开 · 拖动移动';
            orb.querySelector('.orb-surface').setAttribute('aria-label', `${name} · ${count} · ${english() ? 'Expand' : '展开'}`);
            orb.title = group.stateIds.map(id => cardFor(id)?.querySelector('.state-name')?.textContent.trim()).filter(Boolean).join(' / ');
            orb.querySelectorAll('button').forEach(button => button.disabled = busy);
        }
        controls(); api.drawEdges(); positionPopover();
    }
    function animate(element, frames, options = {}) {
        const motion = matchMedia('(prefers-reduced-motion: reduce)').matches;
        const animation = element.animate(frames, { duration: motion ? 1 : 620, easing: 'cubic-bezier(.22,.8,.2,1)', ...options });
        animations.add(animation);
        return animation.finished.catch(() => {}).finally(() => animations.delete(animation));
    }
    function toward(card, group) {
        return `translate(${group.x + 90 - card.offsetLeft - card.offsetWidth / 2}px, ${group.y + 90 - card.offsetTop - card.offsetHeight / 2}px) scale(.025) rotate(-9deg)`;
    }
    function beginTransition(group, members, expanding) {
        const ports = new Map(), world = api.world.getBoundingClientRect(), zoom = api.zoom();
        // Measure visible ports before transforms start, including cards restored for expansion.
        members.forEach(card => {
            card.hidden = false;
            for (const port of card.querySelectorAll('.port')) {
                const rect = port.getBoundingClientRect();
                ports.set(`${card.dataset.stateId}/${port.dataset.portId}`, {
                    x: (rect.left - world.left + rect.width / 2) / zoom,
                    y: (rect.top - world.top + rect.height / 2) / zoom,
                    input: port.dataset.direction === 'input'
                });
            }
        });
        transition = { group, members: new Set(group.stateIds), ports, progress: expanding ? 1 : 0, frame: 0, finish: null };
        return transition;
    }
    function animateConnections(current, expanding, duration) {
        return new Promise(resolve => {
            const start = performance.now(); current.finish = resolve;
            const tick = now => {
                if (transition !== current || !api) { resolve(); return; }
                const t = Math.min(1, (now - start) / duration);
                const eased = 1 - Math.pow(1 - t, 3);
                current.progress = expanding ? 1 - eased : eased;
                api.drawEdges();
                if (t < 1) current.frame = requestAnimationFrame(tick); else resolve();
            };
            current.frame = requestAnimationFrame(tick);
        });
    }
    function clearTransition() {
        if (transition) { cancelAnimationFrame(transition.frame); transition.finish?.(); }
        transition = null;
    }
    async function save(version, previous) {
        try {
            await api.dotnet.invokeMethodAsync('SaveGroupsFromJs', configKey, groups);
        } catch (error) {
            if (version === epoch && api) {
                groups = previous;
                console.error('Could not save Alcyone groups', error);
                showSaveError();
            }
        } finally {
            if (version === epoch && api) { busy = false; render(); }
        }
    }
    async function collapse() {
        if (busy || !selected.size) return;
        const members = [...selected].map(cardFor).filter(c => c && !c.hidden);
        if (!members.length) return;
        const left = Math.min(...members.map(c => c.offsetLeft)), top = Math.min(...members.map(c => c.offsetTop));
        const right = Math.max(...members.map(c => c.offsetLeft + c.offsetWidth)), bottom = Math.max(...members.map(c => c.offsetTop + c.offsetHeight));
        const group = { id: `group_${crypto.randomUUID()}`, name: 'Alcyone', stateIds: members.map(c => c.dataset.stateId), x: Math.max(0, Math.min(3800, (left + right) / 2 - 90)), y: Math.max(0, Math.min(2300, (top + bottom) / 2 - 90)) };
        const previous = structuredClone(groups), version = epoch;
        busy = true; controls();
        const current = beginTransition(group, members, false);
        // Build and paint the planet before any card can disappear.
        groups.push(group); render();
        const orb = [...layer.children].find(node => node.dataset.groupId === group.id);
        const duration = reducedMotion() ? 1 : 680;
        api.world.classList.add('group-transition');
        await Promise.all([...members.map(card => animate(card, [
            { transform: 'translate(0,0) scale(1)', opacity: 1, filter: 'blur(0px)' },
            { transform: toward(card, group), opacity: 0, filter: 'blur(7px)' }
        ], { duration, easing: 'cubic-bezier(.33,1,.68,1)', fill: 'forwards' })),
            animate(orb, [
                { transform: 'scale(.3)', opacity: 0, filter: 'brightness(2)' },
                { transform: 'scale(1.06)', opacity: 1, filter: 'brightness(1.25)', offset: .72 },
                { transform: 'scale(1)', opacity: 1, filter: 'brightness(1)' }
            ], { duration, fill: 'both' }),
            animateConnections(current, false, duration)
        ]);
        if (version !== epoch || !api) return;
        clearTransition(); selected.clear(); render();
        // Clear filled animations after the original cards have been hidden.
        members.forEach(card => card.getAnimations().forEach(a => a.cancel()));
        orb.getAnimations().forEach(a => a.cancel());
        api.world.classList.remove('group-transition');
        if (version === epoch && api) await save(version, previous);
    }
    async function expand(id) {
        if (busy) return;
        const group = groups.find(g => g.id === id);
        if (!group) return;
        const previous = structuredClone(groups), version = epoch;
        busy = true;
        const members = group.stateIds.map(cardFor).filter(Boolean);
        const current = beginTransition(group, members, true);
        const orb = [...layer.children].find(node => node.dataset.groupId === group.id);
        const duration = reducedMotion() ? 1 : 680;
        render();
        api.world.classList.add('group-transition');
        await Promise.all([...members.map(card => animate(card, [
            { transform: toward(card, group), opacity: 0, filter: 'blur(6px)' },
            { transform: 'translate(0,0) scale(1)', opacity: 1, filter: 'blur(0px)' }
        ], { duration, easing: 'cubic-bezier(.33,1,.68,1)', fill: 'both' })),
            animate(orb, [{ transform: 'scale(1)', opacity: 1 }, { transform: 'scale(.3)', opacity: 0 }], { duration, fill: 'both' }),
            animateConnections(current, true, duration)
        ]);
        if (version !== epoch || !api) return;
        groups = groups.filter(g => g.id !== id); clearTransition(); render();
        members.forEach(card => card.getAnimations().forEach(a => a.cancel()));
        api.world.classList.remove('group-transition');
        selected = new Set(group.stateIds);
        await save(version, previous);
    }
    function dismissConfirmation() {
        confirmation?.remove(); confirmation = null; popupAnchor = null;
    }
    function positionPopover() {
        if (!confirmation || !api || !popupAnchor) return;
        const host = api.workspace, bounds = host.getBoundingClientRect(), world = api.world.getBoundingClientRect();
        const x = world.left + popupAnchor.x * api.zoom() - bounds.left;
        const y = world.top + popupAnchor.y * api.zoom() - bounds.top;
        const width = confirmation.offsetWidth, height = confirmation.offsetHeight, margin = 10;
        let left = x + 12, top = y + 12;
        if (left + width > host.clientWidth - margin) left = x - width - 12;
        if (top + height > host.clientHeight - margin) top = y - height - 12;
        left = Math.max(margin, Math.min(left, host.clientWidth - width - margin));
        top = Math.max(margin, Math.min(top, host.clientHeight - height - margin));
        confirmation.style.left = `${host.scrollLeft + left}px`;
        confirmation.style.top = `${host.scrollTop + top}px`;
    }
    function createPopover(titleText, anchor) {
        dismissConfirmation();
        const panel = document.createElement('section');
        panel.className = 'alcyone-fold-popover'; panel.setAttribute('role', 'dialog');
        panel.setAttribute('aria-modal', 'false'); panel.setAttribute('aria-labelledby', 'alcyone-fold-title');
        const title = document.createElement('h2'); title.id = 'alcyone-fold-title'; title.textContent = titleText;
        panel.append(title);
        panel.addEventListener('pointerdown', event => event.stopPropagation());
        panel.addEventListener('click', event => event.stopPropagation());
        panel.addEventListener('keydown', event => {
            event.stopPropagation();
            if (event.key === 'Escape') { event.preventDefault(); dismissConfirmation(); }
        });
        confirmation = panel;
        popupAnchor = anchor || lastSelectionEnd || point({ clientX: api.workspace.getBoundingClientRect().left + 80, clientY: api.workspace.getBoundingClientRect().top + 80 });
        api.workspace.append(panel);
        return panel;
    }
    function popupActions(panel, confirmText, onConfirm) {
        const actions = document.createElement('div'); actions.className = 'fold-dialog-actions';
        const cancel = document.createElement('button'); cancel.type = 'button';
        cancel.textContent = english() ? 'Cancel' : '取消';
        const confirm = document.createElement('button'); confirm.type = 'button'; confirm.className = 'confirm-fold';
        confirm.textContent = confirmText;
        cancel.addEventListener('click', dismissConfirmation);
        confirm.addEventListener('click', onConfirm);
        actions.append(cancel, confirm); panel.append(actions);
        positionPopover();
        return confirm;
    }
    function requestCollapse() {
        if (busy || !selected.size || confirmation) return;
        const dialog = createPopover(english() ? 'Fold into a planet?' : '收纳为星球？', lastSelectionEnd);
        const detail = document.createElement('p');
        detail.textContent = english() ? `${selected.size} states selected. You can expand them anytime.` : `已选中 ${selected.size} 张卡片，收纳后可随时展开。`;
        dialog.append(detail);
        popupActions(dialog, english() ? 'Fold' : '折叠收纳', () => { dismissConfirmation(); void collapse(); });
    }
    function requestRename(id) {
        if (busy) return;
        const group = groups.find(g => g.id === id);
        if (!group) return;
        const panel = createPopover(english() ? 'Rename planet' : '重命名星球', { x: group.x + 90, y: group.y + 180 });
        const input = document.createElement('input'); input.type = 'text'; input.maxLength = 48;
        input.className = 'planet-name-input'; input.value = group.name?.trim() || 'Alcyone';
        input.setAttribute('aria-label', english() ? 'Planet name' : '星球名称');
        panel.append(input);
        const commit = () => {
            // Look up the latest model because runtime renders can replace the group objects.
            const current = groups.find(g => g.id === id);
            if (!current) { dismissConfirmation(); return; }
            const previous = structuredClone(groups);
            current.name = input.value.trim() || 'Alcyone';
            dismissConfirmation(); busy = true; render(); void save(epoch, previous);
        };
        popupActions(panel, english() ? 'Save' : '保存', commit);
        input.addEventListener('keydown', event => {
            if (event.key === 'Enter' && !event.isComposing) { event.preventDefault(); commit(); }
        });
        input.focus({ preventScroll: true }); input.select();
    }
    function showSaveError() {
        const panel = createPopover(english() ? 'Unable to save' : '保存失败', lastSelectionEnd);
        const detail = document.createElement('p');
        detail.textContent = english() ? 'Reconnect and try again.' : '请恢复连接后重试。';
        panel.append(detail);
        popupActions(panel, english() ? 'OK' : '知道了', dismissConfirmation);
    }
    function onOutsidePointerDown(event) {
        if (confirmation && !confirmation.contains(event.target) && event.target !== collapseButton) dismissConfirmation();
    }
    function cancelGesture() {
        if (gesture?.type === 'orb') {
            gesture.group.x = gesture.startX; gesture.group.y = gesture.startY;
            render();
        }
        gesture = null; box?.remove(); box = null;
    }
    window.alcyoneGroups = {
        init(context) {
            api = context; layer = document.getElementById('alcyoneGroupLayer');
            window.alcyonePlanets.init(api.workspace);
            collapseButton = document.getElementById('collapseSelectionButton');
            collapseButton?.addEventListener('click', requestCollapse);
            api.workspace.addEventListener('scroll', positionPopover);
            window.addEventListener('resize', positionPopover);
            document.addEventListener('pointerdown', onOutsidePointerDown);
            window.addEventListener('pointercancel', cancelGesture);
            window.addEventListener('blur', cancelGesture);
        },
        sync(state) {
            if (!api) return;
            if (state.configKey !== configKey) {
                clearTransition(); window.alcyonePlanets.clear();
                epoch++; dismissConfirmation(); cancelGesture(); animations.forEach(a => a.cancel()); animations.clear();
                cards().forEach(c => c.getAnimations().forEach(a => a.cancel()));
                api.world.classList.remove('group-transition');
                configKey = state.configKey; selected.clear(); busy = false;
                lastSelectionEnd = null;
                groups = []; layer.replaceChildren();
            }
            active = new Set(state.currentStateIds || []);
            for (const group of groups) {
                const orb = [...layer.children].find(node => node.dataset.groupId === group.id);
                if (orb) window.alcyonePlanets.setActive(orb.querySelector('canvas'), group.stateIds.some(id => active.has(id)));
            }
            if (!busy && !gesture) groups = structuredClone(state.groups || []);
            if (!busy) render();
        },
        pointerDown(event) {
            if (event.target.closest?.('.alcyone-fold-popover,.orb-name')) return true;
            if (busy) return true;
            const orb = event.target.closest?.('.alcyone-orb');
            if (orb && event.button === 0) {
                const group = groups.find(g => g.id === orb.dataset.groupId);
                gesture = { type: 'orb', orb, group, start: point(event), startX: group.x, startY: group.y, moved: false, previous: structuredClone(groups) };
                event.preventDefault(); return true;
            }
            const background = ['workspace', 'world', 'stateLayer', 'edgeSvg', 'worldScale', 'alcyoneGroupLayer'].includes(event.target.id);
            if (event.button === 0 && background) {
                selected.clear(); gesture = { type: 'box', start: point(event), moved: false };
                box = document.createElement('div'); box.className = 'alcyone-selection-box'; api.world.append(box);
                controls(); event.preventDefault(); return true;
            }
            return false;
        },
        pointerMove(event) {
            if (!gesture) return busy;
            const p = point(event), g = gesture;
            if (g.type === 'box') {
                const x = Math.min(g.start.x, p.x), y = Math.min(g.start.y, p.y), w = Math.abs(p.x - g.start.x), h = Math.abs(p.y - g.start.y);
                if ((w + h) * api.zoom() > 5) g.moved = true;
                Object.assign(box.style, { left: `${x}px`, top: `${y}px`, width: `${w}px`, height: `${h}px` });
                selected.clear();
                for (const card of cards()) if (!card.hidden && card.offsetLeft < x + w && card.offsetLeft + card.offsetWidth > x && card.offsetTop < y + h && card.offsetTop + card.offsetHeight > y) selected.add(card.dataset.stateId);
                controls();
            } else {
                const members = g.group.stateIds.map(cardFor).filter(Boolean);
                const minX = Math.min(...members.map(c => parseFloat(c.style.left) || 0)), minY = Math.min(...members.map(c => parseFloat(c.style.top) || 0));
                g.group.x = Math.max(0, g.startX - minX, Math.min(3800, g.startX + p.x - g.start.x));
                g.group.y = Math.max(0, g.startY - minY, Math.min(2300, g.startY + p.y - g.start.y));
                if (Math.abs(p.x - g.start.x) + Math.abs(p.y - g.start.y) > 3) g.moved = true;
                g.orb.style.left = `${g.group.x}px`; g.orb.style.top = `${g.group.y}px`; api.drawEdges();
            }
            return true;
        },
        pointerUp(event) {
            if (!gesture) return false;
            const g = gesture; gesture = null; box?.remove(); box = null;
            if (g.type === 'box' && g.moved && selected.size) {
                lastSelectionEnd = event ? point(event) : g.start;
                requestCollapse();
            }
            if (g.type === 'orb' && g.moved) {
                g.orb.suppressUntil = performance.now() + 250;
                for (const id of g.group.stateIds) {
                    const card = cardFor(id);
                    if (card) { card.style.left = `${parseFloat(card.style.left) + g.group.x - g.startX}px`; card.style.top = `${parseFloat(card.style.top) + g.group.y - g.startY}px`; }
                }
                busy = true; controls(); void save(epoch, g.previous);
            }
            return g.moved;
        },
        click(event) {
            if (event.target.closest?.('.alcyone-fold-popover')) return true;
            if (busy || event.target.closest?.('.alcyone-orb')) return true;
            const card = event.target.closest?.('.state-card');
            if (event.shiftKey && card && !event.target.closest('button,input,select,.port')) {
                const id = card.dataset.stateId;
                if (selected.has(id)) selected.delete(id); else selected.add(id);
                controls(); return true;
            }
            if (!card && !event.target.closest?.('button,.port,[data-edge-id]')) { selected.clear(); controls(); }
            return false;
        },
        keyDown(event) {
            if (event.key === 'Escape') { dismissConfirmation(); cancelGesture(); selected.clear(); controls(); return false; }
            if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'g') { event.preventDefault(); requestCollapse(); return true; }
            if (selected.size && event.key === 'Delete') return true;
            return busy;
        },
        portPosition(stateId, portId) {
            const port = transition?.ports.get(`${stateId}/${portId}`);
            if (port) {
                const group = transition.group, t = transition.progress;
                return { x: port.x + (group.x + (port.input ? 24 : 156) - port.x) * t,
                    y: port.y + (group.y + 86 - port.y) * t };
            }
            const group = groupFor(stateId);
            if (!group) return null;
            const input = api.world.querySelector(`.port[data-port-id="${CSS.escape(portId)}"][data-state-id="${CSS.escape(stateId)}"]`)?.dataset.direction === 'input';
            return { x: group.x + (input ? 24 : 156), y: group.y + 86 };
        },
        internalEdge(edge) {
            if (transition?.members.has(edge.fromStateId) && transition.members.has(edge.toStateId)) return false;
            const group = groupFor(edge.fromStateId); return group && group.stateIds.includes(edge.toStateId);
        },
        edgeOpacity(edge) {
            if (transition?.members.has(edge.fromStateId) && transition.members.has(edge.toStateId)) return Math.pow(1 - transition.progress, 2);
            return 1;
        },
        dispose() {
            clearTransition(); window.alcyonePlanets.dispose();
            epoch++; dismissConfirmation(); cancelGesture(); animations.forEach(a => a.cancel()); animations.clear();
            if (api) cards().forEach(c => { c.hidden = false; c.classList.remove('box-selected'); c.getAnimations().forEach(a => a.cancel()); });
            collapseButton?.removeEventListener('click', requestCollapse);
            api?.workspace.removeEventListener('scroll', positionPopover);
            window.removeEventListener('resize', positionPopover);
            document.removeEventListener('pointerdown', onOutsidePointerDown);
            window.removeEventListener('pointercancel', cancelGesture); window.removeEventListener('blur', cancelGesture);
            layer?.replaceChildren(); groups = []; selected.clear(); configKey = null; busy = false; api = null;
        }
    };
})();
