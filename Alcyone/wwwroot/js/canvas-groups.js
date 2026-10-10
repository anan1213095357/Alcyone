(() => {
    let api, layer, box, groups = [], selected = new Set(), gesture = null, confirmation = null;
    let configKey = null, busy = false, epoch = 0, active = new Set();
    let collapseButton, toolbar, selectedGroups = new Set(), pendingSelection = null;
    let toolbarTimer = 0, toolbarVisible = false;
    function showToolbarOnClick(event) {
        if (!event.target.closest?.('.state-card,.alcyone-orb')) return;
        clearTimeout(toolbarTimer);
        toolbarVisible = true;
        controls();
        toolbarTimer = setTimeout(() => { toolbarVisible = false; controls(); }, 2000);
    }
    let popupAnchor = null, lastSelectionEnd = null, transition = null;
    const animations = new Set();
    let hitObserver = null, hitFrame = 0, lastHitRegions = '';
    function reportHitRegions() {
        if (gesture || hitFrame || !api?.desktopView || !window.external?.sendMessage) return;
        hitFrame = window.alcyoneAnimation.requestFrame(() => {
            hitFrame = 0;
            if (gesture) return;
            const rectangles = [...api.workspace.querySelectorAll('.state-card:not([hidden]),.alcyone-orb,.canvas-selection-toolbar:not([hidden]),.alcyone-fold-popover')]
                .map(node => { const r = node.getBoundingClientRect(); return { left: r.left, top: r.top, right: r.right, bottom: r.bottom }; });
            const json = JSON.stringify({ ratio: window.devicePixelRatio || 1, rectangles });
            if (json !== lastHitRegions) { lastHitRegions = json; window.external.sendMessage('alcyone:desktop:hitregions:' + json); }
        });
    }
    const reducedMotion = () => matchMedia('(prefers-reduced-motion: reduce)').matches;
    const english = () => document.documentElement.lang === 'en';
    const cards = () => [...api.world.querySelectorAll('.state-card')];
    const cardFor = id => api.world.querySelector(`.state-card[data-state-id="${CSS.escape(id)}"]`);
    const collapsed = group => group.isCollapsed !== false;
    const groupFor = id => groups.find(g => collapsed(g) && g.stateIds.includes(id));
    const point = event => {
        const rect = api.world.getBoundingClientRect(), zoom = api.zoom();
        return { x: (event.clientX - rect.left) / zoom, y: (event.clientY - rect.top) / zoom };
    };
    function controls() {
        if (toolbar) {
            toolbar.hidden = !toolbarVisible || (!selected.size && !selectedGroups.size);
            for (const button of toolbar.querySelectorAll('button')) {
                const action = button.dataset.action;
                button.disabled = busy || (action === 'fold' ? !selected.size : action === 'expand' ? !selectedGroups.size : action === 'appearance' ? selectedGroups.size !== 1 : !selected.size && !selectedGroups.size);
                button.querySelector('span').textContent = ({ fold: ['折叠', 'Fold'], copy: ['复制', 'Copy'], delete: ['删除', 'Delete'], expand: ['展开', 'Expand'], appearance: ['外观', 'Appearance'] })[action][english() ? 1 : 0];
            }
            toolbar.style.left = `${api.workspace.scrollLeft + api.workspace.clientWidth / 2}px`;
            toolbar.style.top = `${api.workspace.scrollTop + 14}px`;
        }
        for (const card of cards()) card.classList.toggle('box-selected', selected.has(card.dataset.stateId));
        for (const orb of layer.children) orb.classList.toggle('is-selected', selectedGroups.has(orb.dataset.groupId));
    }
    function render() {
        if (!api) return;
        const existing = new Set(cards().map(c => c.dataset.stateId));
        groups = groups.map(g => ({ ...g, stateIds: g.stateIds.filter(id => existing.has(id)) })).filter(g => g.stateIds.length);
        selectedGroups = new Set([...selectedGroups].filter(id => groups.some(g => g.id === id && collapsed(g))));
        selected = new Set([...selected].filter(id => existing.has(id) && !groupFor(id)));
        for (const card of cards()) card.hidden = !!groupFor(card.dataset.stateId) && !transition?.members.has(card.dataset.stateId);
        for (const orb of [...layer.children]) if (!groups.some(g => g.id === orb.dataset.groupId && collapsed(g))) {
            window.alcyonePlanets.detach(orb.querySelector('canvas')); orb.remove();
        }
        for (const group of groups.filter(collapsed)) {
            let orb = [...layer.children].find(node => node.dataset.groupId === group.id);
            if (!orb) {
                orb = document.createElement('div');
                orb.className = 'alcyone-orb'; orb.dataset.groupId = group.id;
                orb.innerHTML = '<span class="orb-halo"></span><button type="button" class="orb-surface"><canvas class="planet-disc" width="320" height="320" aria-hidden="true"></canvas></button><button type="button" class="orb-name"></button><span class="orb-count"></span><span class="orb-hint"></span>';
                window.alcyonePlanets.attach(orb.querySelector('canvas'), group.appearance || 'ocean');
                orb.querySelector('.orb-surface').addEventListener('click', event => {
                    event.stopPropagation();
                    if (performance.now() < (orb.suppressUntil || 0)) return;
                    if (!event.shiftKey) { selected.clear(); selectedGroups.clear(); }
                    if (event.shiftKey && selectedGroups.has(orb.dataset.groupId)) selectedGroups.delete(orb.dataset.groupId);
                    else selectedGroups.add(orb.dataset.groupId);
                    controls();
                });
                orb.querySelector('.orb-surface').addEventListener('dblclick', event => { event.stopPropagation(); void expand(orb.dataset.groupId); });
                orb.querySelector('.orb-name').addEventListener('click', event => {
                    event.stopPropagation();
                    requestRename(orb.dataset.groupId);
                });
                layer.append(orb);
            }
            orb.style.left = `${group.x}px`; orb.style.top = `${group.y}px`;
            orb.classList.toggle('is-running', group.stateIds.some(id => active.has(id)));
            window.alcyonePlanets.setActive(orb.querySelector('canvas'), group.stateIds.some(id => active.has(id)));
            window.alcyonePlanets.setAppearance(orb.querySelector('canvas'), group.appearance || 'ocean');
            const count = english() ? `${group.stateIds.length} STATES` : `${group.stateIds.length} 个状态`;
            orb.querySelector('.orb-count').textContent = count;
            const name = group.name?.trim() || 'Alcyone';
            orb.querySelector('.orb-name').textContent = name;
            orb.querySelector('.orb-name').title = english() ? 'Rename planet' : '点击重命名星球';
            orb.querySelector('.orb-name').setAttribute('aria-label', `${english() ? 'Rename' : '重命名'} ${name}`);
            orb.querySelector('.orb-hint').textContent = english() ? 'DOUBLE-CLICK TO EXPAND' : '单击选中 · 双击展开';
            orb.querySelector('.orb-surface').setAttribute('aria-label', `${name} · ${count} · ${english() ? 'Select planet' : '选中星球'}`);
            orb.title = group.stateIds.map(id => cardFor(id)?.querySelector('.state-name')?.textContent.trim()).filter(Boolean).join(' / ');
            orb.querySelectorAll('button').forEach(button => button.disabled = busy);
        }
        controls(); api.drawEdges(); positionPopover();
    }
    function animate(element, frames, options = {}) {
        const motion = matchMedia('(prefers-reduced-motion: reduce)').matches;
        const animation = element.animate(frames, { duration: motion ? 1 : 620, easing: 'cubic-bezier(.22,.8,.2,1)', ...options });
        animations.add(animation);
        window.alcyoneAnimation.track(animation);
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
        transition = { group, members: new Set(members.map(card => card.dataset.stateId)), ports, progress: expanding ? 1 : 0, frame: 0, finish: null };
        return transition;
    }
    function animateConnections(current, expanding, duration) {
        return new Promise(resolve => {
            const start = window.alcyoneAnimation.now(); current.finish = resolve;
            const tick = now => {
                if (transition !== current || !api) { resolve(); return; }
                const t = Math.min(1, (now - start) / duration);
                const eased = 1 - Math.pow(1 - t, 3);
                current.progress = expanding ? 1 - eased : eased;
                api.drawEdges();
                if (t < 1) current.frame = window.alcyoneAnimation.requestFrame(tick); else resolve();
            };
            current.frame = window.alcyoneAnimation.requestFrame(tick);
        });
    }
    function clearTransition() {
        if (transition) { window.alcyoneAnimation.cancelFrame(transition.frame); transition.finish?.(); }
        transition = null;
    }
    async function save(version, previous, positions = []) {
        try {
            await api.dotnet.invokeMethodAsync('SaveGroupsFromJs', configKey, groups, positions);
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
    async function collapse(targetId = null, positions = []) {
        if (busy || !selected.size) return;
        const members = [...selected].map(cardFor).filter(c => c && !c.hidden);
        if (!members.length) return;
        const left = Math.min(...members.map(c => c.offsetLeft)), top = Math.min(...members.map(c => c.offsetTop));
        const right = Math.max(...members.map(c => c.offsetLeft + c.offsetWidth)), bottom = Math.max(...members.map(c => c.offsetTop + c.offsetHeight));
        const previous = structuredClone(groups), version = epoch;
        const group = groups.find(g => g.id === targetId && collapsed(g))
            || groups.find(g => !collapsed(g) && g.stateIds.length === selected.size && g.stateIds.every(id => selected.has(id)))
            || { id: `group_${crypto.randomUUID()}`, name: 'Alcyone', appearance: 'ocean', isCollapsed: false, stateIds: [], x: 0, y: 0 };
        const absorbing = groups.includes(group) && collapsed(group);
        if (!absorbing) {
            group.x = Math.max(0, Math.min(3800, (left + right) / 2 - 90));
            group.y = Math.max(0, Math.min(2300, (top + bottom) / 2 - 90));
        }
        for (const other of groups) if (other !== group && !collapsed(other)) other.stateIds = other.stateIds.filter(id => !selected.has(id));
        groups = groups.filter(g => g === group || g.stateIds.length);
        group.isCollapsed = true;
        group.stateIds = [...new Set([...group.stateIds, ...members.map(c => c.dataset.stateId)])];
        busy = true; controls();
        const current = beginTransition(group, members, false);
        // Build and paint the planet before any card can disappear.
        if (!groups.includes(group)) groups.push(group);
        render();
        const orb = [...layer.children].find(node => node.dataset.groupId === group.id);
        const duration = reducedMotion() ? 1 : 680;
        api.world.classList.add('group-transition');
        await Promise.all([...members.map(card => animate(card, [
            { transform: 'translate(0,0) scale(1)', opacity: 1, filter: 'blur(0px)' },
            { transform: toward(card, group), opacity: 0, filter: 'blur(7px)' }
        ], { duration, easing: 'cubic-bezier(.33,1,.68,1)', fill: 'forwards' })),
            animate(orb, [
                { transform: absorbing ? 'scale(1)' : 'scale(.3)', opacity: absorbing ? 1 : 0, filter: 'brightness(1)' },
                { transform: 'scale(1.14)', opacity: 1, filter: 'brightness(1.65)', offset: .72 },
                { transform: 'scale(1)', opacity: 1, filter: 'brightness(1)' }
            ], { duration, fill: 'both' }),
            animateConnections(current, false, duration)
        ]);
        if (version !== epoch || !api) return;
        clearTransition(); selected.clear(); selectedGroups = new Set([group.id]); render();
        // Clear filled animations after the original cards have been hidden.
        members.forEach(card => card.getAnimations().forEach(a => a.cancel()));
        orb.getAnimations().forEach(a => a.cancel());
        api.world.classList.remove('group-transition');
        if (version === epoch && api) await save(version, previous, positions);
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
        groups.find(g => g.id === id).isCollapsed = false; clearTransition(); render();
        members.forEach(card => card.getAnimations().forEach(a => a.cancel()));
        api.world.classList.remove('group-transition');
        selected = new Set(group.stateIds);
        await save(version, previous);
    }
    function dismissConfirmation() {
        confirmation?.remove(); confirmation = null; popupAnchor = null;
    }
    function positionPopover() {
        if (toolbar && api) {
            toolbar.style.left = `${api.workspace.scrollLeft + api.workspace.clientWidth / 2}px`;
            toolbar.style.top = `${api.workspace.scrollTop + 14}px`;
        }
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
        dismissConfirmation(); void collapse();
    }
    function requestRename(id) {
        if (busy) return;
        const group = groups.find(g => g.id === id);
        if (!group) return;
        const panel = createPopover(english() ? 'Planet settings' : '星球设置', { x: group.x + 90, y: group.y + 180 });
        panel.classList.add('planet-settings');
        const input = document.createElement('input'); input.type = 'text'; input.maxLength = 48;
        input.className = 'planet-name-input'; input.value = group.name?.trim() || 'Alcyone';
        input.setAttribute('aria-label', english() ? 'Planet name' : '星球名称');
        panel.append(input);
        let appearance = group.appearance || 'ocean';
        const choices = [['ocean','海洋','Ocean'],['rust','赤岩','Rust'],['ice','冰川','Ice'],['violet','紫气','Violet'],['sand','砂环','Sand rings'],['jade','翡翠','Jade'],['rock','岩石','Rock'],['lava','熔岩','Lava'],['blue','深蓝','Blue'],['rose','玫瑰环','Rose rings'],['forest','森林','Forest'],['silver','银环','Silver rings']];
        const grid = document.createElement('div'); grid.className = 'planet-appearance-grid';
        for (const [key, zh, en] of choices) {
            const button = document.createElement('button'); button.type = 'button';
            button.setAttribute('aria-pressed', String(key === appearance));
            const preview = document.createElement('canvas'); preview.setAttribute('aria-hidden', 'true');
            window.alcyonePlanets.preview(preview, key);
            const label = document.createElement('span'); label.textContent = english() ? en : zh;
            button.append(preview, label); button.addEventListener('click', () => {
                appearance = key;
                for (const option of grid.children) option.setAttribute('aria-pressed', String(option === button));
            });
            grid.append(button);
        }
        panel.append(grid);
        const commit = () => {
            // Look up the latest model because runtime renders can replace the group objects.
            const current = groups.find(g => g.id === id);
            if (!current) { dismissConfirmation(); return; }
            const previous = structuredClone(groups);
            current.name = input.value.trim() || 'Alcyone';
            current.appearance = appearance;
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
    function allSelectedIds() {
        return [...new Set([...selected, ...groups.filter(g => selectedGroups.has(g.id)).flatMap(g => g.stateIds)])];
    }
    function applyPendingSelection() {
        if (!pendingSelection) return true;
        if (pendingSelection.stateIds.some(id => !cardFor(id))) return false;
        groups = pendingSelection.groups;
        selected = new Set(pendingSelection.stateIds.filter(id => !groupFor(id)));
        selectedGroups = new Set(pendingSelection.groupIds);
        pendingSelection = null;
        return true;
    }
    async function editSelection(command, positions = []) {
        if (busy) return;
        const version = epoch;
        busy = true; controls();
        try {
            const result = await api.dotnet.invokeMethodAsync('EditSelectionFromJs', configKey, command, allSelectedIds(), positions);
            if (version !== epoch || !api) return;
            if (command !== 'move') pendingSelection = result;
        } catch (error) {
            if (version === epoch && api) { console.error(error); showSaveError(); }
        } finally {
            if (version === epoch && api) {
                busy = false;
                if (applyPendingSelection()) render(); else controls();
            }
        }
    }
    async function expandSelection() {
        if (busy) return;
        const version = epoch, ids = [...selectedGroups], restored = new Set(selected);
        for (const id of ids) {
            const group = groups.find(g => g.id === id);
            if (group) group.stateIds.forEach(stateId => restored.add(stateId));
            await expand(id);
            if (epoch !== version || !api) return;
        }
        selected = restored; selectedGroups.clear(); controls();
    }
    function onToolbarClick(event) {
        const action = event.target.closest('button')?.dataset.action;
        if (busy) return;
        if (action === 'fold') requestCollapse();
        else if (action === 'appearance') requestRename([...selectedGroups][0]);
        else if (action === 'expand') void expandSelection();
        else if (action === 'copy' || action === 'delete') void editSelection(action);
    }
    function createToolbar() {
        toolbar = document.createElement('div'); toolbar.className = 'canvas-selection-toolbar'; toolbar.hidden = true;
        toolbar.setAttribute('role', 'toolbar'); toolbar.setAttribute('aria-label', english() ? 'Selection actions' : '选中项操作');
        toolbar.innerHTML = '<div class="selection-actions">'
            + '<button type="button" data-action="fold"><svg viewBox="0 0 20 20"><path d="M2 2l5 5M3 7h4V3m11-1-5 5m0-4v4h4M2 18l5-5m-4 0h4v4m11 1-5-5m0 4v-4h4"/></svg><span></span></button>'
            + '<button type="button" data-action="copy"><svg viewBox="0 0 20 20"><rect x="7" y="7" width="10" height="10" rx="2"/><path d="M12 7V3H3v9h4"/></svg><span></span></button>'
            + '<button type="button" data-action="delete"><svg viewBox="0 0 20 20"><path d="M3 5h14M7 5V3h6v2M5 5l1 12h8l1-12M8 8v6m4-6v6"/></svg><span></span></button>'
            + '<button type="button" data-action="expand"><svg viewBox="0 0 20 20"><path d="M8 8 3 3m0 4V3h4m5 5 5-5m-4 0h4v4M8 12l-5 5m0-4v4h4m5-5 5 5m-4 0h4v-4"/></svg><span></span></button>'
            + '<button type="button" data-action="appearance"><svg viewBox="0 0 20 20"><circle cx="10" cy="10" r="6"/><ellipse cx="10" cy="10" rx="9" ry="3" transform="rotate(-30 10 10)"/></svg><span></span></button></div>';
        if (api.desktopView) toolbar.querySelectorAll('[data-action="copy"],[data-action="delete"]').forEach(button => button.remove());
        toolbar.addEventListener('click', onToolbarClick);
        toolbar.addEventListener('pointerdown', event => event.stopPropagation());
        api.workspace.append(toolbar);
        collapseButton = toolbar.querySelector('[data-action="fold"]');
    }
    function clearDropTarget() { for (const orb of layer.children) orb.classList.remove('drop-target'); }
    function cancelGesture() {
        if (gesture?.type === 'cards') {
            for (const item of gesture.items) { item.card.style.left = `${item.x}px`; item.card.style.top = `${item.y}px`; }
            clearDropTarget(); api.drawEdges();
        }
        if (gesture?.type === 'orb') {
            gesture.group.x = gesture.startX; gesture.group.y = gesture.startY;
            render();
        }
        gesture = null; box?.remove(); box = null;
    }
    window.alcyoneGroups = {
        init(context) {
            context.workspace.addEventListener('click', showToolbarOnClick, true);
            api = context; layer = document.getElementById('alcyoneGroupLayer');
            window.alcyonePlanets.init(api.workspace);
            createToolbar();
            if (api.desktopView) { hitObserver = new MutationObserver(reportHitRegions); hitObserver.observe(api.workspace, { childList:true,subtree:true,attributes:true,attributeFilter:['style','hidden','class'] }); reportHitRegions(); }
            api.workspace.addEventListener('scroll', positionPopover);
            window.addEventListener('resize', positionPopover);
            document.addEventListener('pointerdown', onOutsidePointerDown);
            window.addEventListener('pointercancel', cancelGesture);
            window.addEventListener('blur', cancelGesture);
        },
        sync(state) {
            if (!api) return;
            if (state.configKey !== configKey) {
                clearTimeout(toolbarTimer); toolbarVisible = false;
                clearTransition(); window.alcyonePlanets.clear();
                epoch++; dismissConfirmation(); cancelGesture(); animations.forEach(a => a.cancel()); animations.clear();
                cards().forEach(c => c.getAnimations().forEach(a => a.cancel()));
                api.world.classList.remove('group-transition');
                configKey = state.configKey; selected.clear(); busy = false;
                selectedGroups.clear(); pendingSelection = null;
                lastSelectionEnd = null;
                groups = []; layer.replaceChildren();
            }
            active = new Set(state.currentStateIds || []);
            for (const group of groups) {
                const orb = [...layer.children].find(node => node.dataset.groupId === group.id);
                if (orb) {
                    const running = group.stateIds.some(id => active.has(id));
                    orb.classList.toggle('is-running', running);
                    window.alcyonePlanets.setActive(orb.querySelector('canvas'), running);
                }
            }
            if (!busy && !gesture) groups = structuredClone(state.groups || []);
            if (!applyPendingSelection()) return;
            // render() rebuilds group objects and resets orb coordinates. During a
            // drag the gesture owns those objects; runtime ticks must only update activity.
            if (!busy && !gesture) render();
        },
        pointerDown(event) {
            if (event.target.closest?.('.alcyone-fold-popover,.orb-name,.canvas-selection-toolbar')) return true;
            if (busy) return true;
            const card = event.target.closest?.('.state-card');
            if (event.button === 0 && card && !event.shiftKey && !event.target.closest('button,input,select,textarea,.port')) {
                const id = card.dataset.stateId;
                if (!selected.has(id)) { selected.clear(); selected.add(id); }
                selectedGroups.clear();
                gesture = { type: 'cards', start: point(event), moved: false,
                    items: [...selected].map(cardFor).filter(c => c && !c.hidden).map(c => ({ card: c, id: c.dataset.stateId, x: parseFloat(c.style.left) || 0, y: parseFloat(c.style.top) || 0 })) };
                controls(); event.preventDefault(); return true;
            }
            const orb = event.target.closest?.('.alcyone-orb');
            if (orb && event.button === 0) {
                const group = groups.find(g => g.id === orb.dataset.groupId);
                gesture = { type: 'orb', orb, group, start: point(event), startX: group.x, startY: group.y, moved: false, previous: structuredClone(groups) };
                event.preventDefault(); return true;
            }
            const background = ['workspace', 'world', 'stateLayer', 'edgeSvg', 'worldScale', 'alcyoneGroupLayer'].includes(event.target.id);
            if (event.button === 0 && background) {
                selected.clear(); selectedGroups.clear(); gesture = { type: 'box', start: point(event), moved: false };
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
                selectedGroups.clear();
                for (const card of cards()) if (!card.hidden && card.offsetLeft < x + w && card.offsetLeft + card.offsetWidth > x && card.offsetTop < y + h && card.offsetTop + card.offsetHeight > y) selected.add(card.dataset.stateId);
                for (const group of groups.filter(collapsed)) if (group.x < x + w && group.x + 180 > x && group.y < y + h && group.y + 200 > y) selectedGroups.add(group.id);
                controls();
            } else if (g.type === 'cards') {
                const dx = Math.max(-Math.min(...g.items.map(item => item.x)), p.x - g.start.x);
                const dy = Math.max(-Math.min(...g.items.map(item => item.y)), p.y - g.start.y);
                if ((Math.abs(dx) + Math.abs(dy)) * api.zoom() > 4) g.moved = true;
                if (g.moved) {
                    for (const item of g.items) { item.card.style.left = `${item.x + dx}px`; item.card.style.top = `${item.y + dy}px`; }
                    g.positions = g.items.map(item => ({ id: item.id, x: item.x + dx, y: item.y + dy }));
                    g.targetId = groups.find(group => collapsed(group) && Math.hypot(p.x - group.x - 90, p.y - group.y - 86) < 100)?.id;
                    clearDropTarget();
                    if (g.targetId) [...layer.children].find(orb => orb.dataset.groupId === g.targetId)?.classList.add('drop-target');
                    api.drawEdges();
                }
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
            reportHitRegions();
            if (g.type === 'box' && g.moved && selected.size) {
                lastSelectionEnd = event ? point(event) : g.start;
                controls();
            }
            if (g.type === 'cards' && g.moved) {
                clearDropTarget();
                if (g.targetId) void collapse(g.targetId, g.positions);
                else void editSelection('move', g.positions);
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
            if (event.target.closest?.('.alcyone-fold-popover,.canvas-selection-toolbar')) return true;
            if (busy || event.target.closest?.('.alcyone-orb')) return true;
            const card = event.target.closest?.('.state-card');
            if (event.shiftKey && card && !event.target.closest('button,input,select,.port')) {
                const id = card.dataset.stateId;
                if (selected.has(id)) selected.delete(id); else selected.add(id);
                controls(); return true;
            }
            if (!card && !event.target.closest?.('button,.port,[data-edge-id]')) { selected.clear(); selectedGroups.clear(); controls(); }
            return false;
        },
        keyDown(event) {
            if (event.key === 'Escape') { dismissConfirmation(); cancelGesture(); selected.clear(); selectedGroups.clear(); controls(); return false; }
            if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'g') { event.preventDefault(); requestCollapse(); return true; }
            if ((selected.size || selectedGroups.size) && event.key === 'Delete') { event.preventDefault(); void editSelection('delete'); return true; }
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
        interplanetEdge(edge) {
            // Wait until folding finishes so ships never appear on internal wires.
            if (transition) return false;
            const from = groupFor(edge.fromStateId), to = groupFor(edge.toStateId);
            return !!from && !!to && from.id !== to.id;
        },
        internalEdge(edge) {
            if (transition && (transition.members.has(edge.fromStateId) || transition.members.has(edge.toStateId))
                && transition.group.stateIds.includes(edge.fromStateId) && transition.group.stateIds.includes(edge.toStateId)) return false;
            const group = groupFor(edge.fromStateId); return group && group.stateIds.includes(edge.toStateId);
        },
        edgeOpacity(edge) {
            if (transition && (transition.members.has(edge.fromStateId) || transition.members.has(edge.toStateId))
                && transition.group.stateIds.includes(edge.fromStateId) && transition.group.stateIds.includes(edge.toStateId)) return Math.pow(1 - transition.progress, 2);
            return 1;
        },
        dispose() {
            clearTimeout(toolbarTimer); toolbarTimer = 0; toolbarVisible = false;
            api?.workspace.removeEventListener('click', showToolbarOnClick, true);
            hitObserver?.disconnect(); hitObserver = null; window.alcyoneAnimation.cancelFrame(hitFrame); hitFrame = 0; lastHitRegions = '';
            clearTransition(); window.alcyonePlanets.dispose();
            epoch++; dismissConfirmation(); cancelGesture(); animations.forEach(a => a.cancel()); animations.clear();
            if (api) cards().forEach(c => { c.hidden = false; c.classList.remove('box-selected'); c.getAnimations().forEach(a => a.cancel()); });
            toolbar?.removeEventListener('click', onToolbarClick); toolbar?.remove(); toolbar = null;
            api?.workspace.removeEventListener('scroll', positionPopover);
            window.removeEventListener('resize', positionPopover);
            document.removeEventListener('pointerdown', onOutsidePointerDown);
            window.removeEventListener('pointercancel', cancelGesture); window.removeEventListener('blur', cancelGesture);
            layer?.replaceChildren(); groups = []; selected.clear(); selectedGroups.clear(); pendingSelection = null; configKey = null; busy = false; api = null;
        }
    };
})();
