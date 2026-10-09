(() => {
    // A shared scheduler and cached surface maps keep rotation independent of Blazor renders.
    const size = 192, radius = 75, mapWidth = 256, mapHeight = 128;
    const records = new Map(), cache = new Map(), projection = [];
    const reduced = matchMedia('(prefers-reduced-motion: reduce)');
    let frame = 0, observer = null;
    const appearanceKeys = ['ocean', 'rust', 'ice', 'violet', 'sand', 'jade', 'rock', 'lava', 'blue', 'rose', 'forest', 'silver'];
    const palettes = [
        [[8, 41, 84], [48, 108, 83], [207, 223, 226]], // ocean
        [[105, 40, 22], [212, 126, 67], [241, 192, 137]], // rust
        [[26, 71, 105], [135, 202, 214], [237, 249, 255]], // ice
        [[73, 42, 104], [157, 117, 185], [234, 201, 228]], // violet gas
        [[94, 69, 39], [198, 160, 108], [239, 221, 179]], // ochre gas
        [[14, 72, 71], [67, 158, 144], [194, 232, 211]], // jade
        [[34, 32, 37], [121, 111, 104], [195, 186, 170]], // rocky
        [[96, 27, 15], [235, 76, 26], [255, 175, 58]], // volcanic
        [[35, 48, 103], [94, 126, 207], [198, 217, 250]], // blue gas
        [[100, 50, 64], [211, 137, 135], [253, 217, 188]], // rose gas
        [[43, 68, 47], [130, 158, 83], [219, 217, 154]], // forest
        [[58, 79, 92], [167, 181, 185], [244, 239, 220]] // silver
    ];
    const mix = (a, b, t) => a + (b - a) * t;
    const clamp = value => Math.max(0, Math.min(1, value));
    function seedOf(id) {
        let seed = 2166136261;
        for (const char of id) seed = Math.imul(seed ^ char.charCodeAt(0), 16777619);
        return seed >>> 0;
    }
    function surface(id) {
        if (!appearanceKeys.includes(id)) id = 'ocean';
        if (cache.has(id)) {
            const found = cache.get(id); cache.delete(id); cache.set(id, found); return found;
        }
        const seed = seedOf(id), kind = appearanceKeys.indexOf(id), colors = palettes[kind];
        const hash = (x, y, z) => {
            let h = seed ^ Math.imul(x, 374761393) ^ Math.imul(y, 668265263) ^ Math.imul(z, 1274126177);
            h = Math.imul(h ^ (h >>> 13), 1274126177);
            return ((h ^ (h >>> 16)) >>> 0) / 4294967295;
        };
        function noise(x, y, z) {
            const ix = Math.floor(x), iy = Math.floor(y), iz = Math.floor(z);
            let a = x - ix, b = y - iy, c = z - iz;
            a *= a * (3 - 2 * a); b *= b * (3 - 2 * b); c *= c * (3 - 2 * c);
            return mix(mix(mix(hash(ix, iy, iz), hash(ix + 1, iy, iz), a), mix(hash(ix, iy + 1, iz), hash(ix + 1, iy + 1, iz), a), b),
                mix(mix(hash(ix, iy, iz + 1), hash(ix + 1, iy, iz + 1), a), mix(hash(ix, iy + 1, iz + 1), hash(ix + 1, iy + 1, iz + 1), a), b), c);
        }
        function terrain(x, y, z) {
            let value = 0, weight = .55;
            for (let i = 0; i < 4; i++) { value += noise(x, y, z) * weight; x *= 2.05; y *= 2.05; z *= 2.05; weight *= .48; }
            return value;
        }
        const data = new Uint8ClampedArray(mapWidth * mapHeight * 3);
        const gas = [3, 4, 8, 9].includes(kind);
        for (let y = 0; y < mapHeight; y++) for (let x = 0; x < mapWidth; x++) {
            const latitude = (y / (mapHeight - 1) - .5) * Math.PI, longitude = x / mapWidth * Math.PI * 2;
            const nx = Math.cos(latitude) * Math.cos(longitude), ny = Math.sin(latitude), nz = Math.cos(latitude) * Math.sin(longitude);
            const ground = terrain(nx * 4 + 7, ny * 4 - 2, nz * 4 + 3);
            const cloud = terrain(nx * 11 + 20, ny * 11, nz * 11 + 10);
            const bands = (Math.sin(ny * (25 + seed % 17) + ground * 12) + 1) * .5;
            let blend = gas ? .2 + bands * .65 : clamp((ground - .37) * 4.6);
            let clouds = gas ? clamp((cloud - .54) * 3) : clamp((cloud - .5) * 5);
            if ([1, 6, 7].includes(kind)) clouds *= .12;
            if (kind === 2) clouds = Math.max(clouds, clamp((Math.abs(ny) - .55) * 2));
            const at = (y * mapWidth + x) * 3;
            for (let channel = 0; channel < 3; channel++) {
                let color = mix(colors[0][channel], colors[1][channel], blend);
                color = mix(color, colors[2][channel], clouds);
                data[at + channel] = color * (.9 + noise(nx * 80, ny * 80, nz * 80) * .2);
            }
        }
        const result = { data, ringed: [4, 9, 11].includes(kind), ring: colors[1], phase: (seed % 1000) / 1000, direction: seed % 5 === 0 ? -1 : 1 };
        cache.set(id, result);
        while (cache.size > 24) cache.delete(cache.keys().next().value);
        return result;
    }
    for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) {
        const nx = (x - (size - 1) / 2) / radius, ny = (y - (size - 1) / 2) / radius, r = nx * nx + ny * ny;
        if (r > 1) continue;
        const nz = Math.sqrt(1 - r), light = Math.max(0, nx * -.62 + ny * -.38 + nz * .69);
        projection.push({ at: (y * size + x) * 4, u: Math.atan2(nx, nz) / (2 * Math.PI) + .5,
            v: Math.min(mapHeight - 1, Math.floor((Math.asin(ny) / Math.PI + .5) * mapHeight)),
            light: .045 + .955 * Math.pow(light, .8), rim: Math.pow(1 - nz, 3) * (.12 + light * .55),
            alpha: clamp((1 - Math.sqrt(r)) * radius) * 255 });
    }
    function draw(record) {
        const data = record.pixels.data, texture = record.texture;
        for (const p of projection) {
            const sample = ((p.v * mapWidth) + (Math.floor((p.u + record.phase) * mapWidth) & (mapWidth - 1))) * 3;
            data[p.at] = texture.data[sample] * p.light + 57 * p.rim;
            data[p.at + 1] = texture.data[sample + 1] * p.light + 144 * p.rim;
            data[p.at + 2] = texture.data[sample + 2] * p.light + 242 * p.rim;
            data[p.at + 3] = p.alpha;
        }
        record.ctx.putImageData(record.pixels, 0, 0);
        if (texture.ringed) {
            const ctx = record.ctx;
            ctx.save(); ctx.translate(size / 2, size / 2); ctx.rotate(-.36);
            ctx.strokeStyle = `rgba(${texture.ring.join(',')},.48)`; ctx.lineWidth = 8;
            ctx.globalCompositeOperation = 'destination-over';
            ctx.beginPath(); ctx.ellipse(0, 0, 93, 29, 0, 0, Math.PI * 2); ctx.stroke();
            ctx.globalCompositeOperation = 'source-over'; ctx.strokeStyle = `rgba(${texture.ring.join(',')},.65)`;
            ctx.beginPath(); ctx.ellipse(0, 0, 93, 29, 0, 0, Math.PI); ctx.stroke(); ctx.restore();
        }
    }
    function tick(now) {
        frame = 0;
        const visible = [...records.values()].filter(r => r.visible && r.canvas.isConnected);
        const budgetFps = Math.min(24, Math.max(2, 100 / Math.max(1, visible.length)));
        for (const record of visible) {
            const target = record.active ? .035 : .005;
            const elapsed = record.time ? Math.min((now - record.time) / 1000, .15) : 0;
            record.time = now; record.speed += (target - record.speed) * Math.min(1, elapsed * 3);
            record.phase = (record.phase + elapsed * record.speed * record.texture.direction + 1) % 1;
            if (now - record.drawn >= 1000 / Math.min(record.active ? 24 : 8, budgetFps)) { draw(record); record.drawn = now; }
        }
        if (records.size && !document.hidden && !reduced.matches) frame = requestAnimationFrame(tick);
    }
    function resume() {
        cancelAnimationFrame(frame); frame = 0;
        records.forEach(record => record.time = 0);
        if (records.size && !document.hidden && !reduced.matches) frame = requestAnimationFrame(tick);
    }
    window.alcyonePlanets = {
        init(root) {
            observer?.disconnect();
            observer = new IntersectionObserver(entries => {
                for (const entry of entries) { const record = records.get(entry.target); if (record) { record.visible = entry.isIntersecting; record.time = 0; } }
            }, { root });
            document.addEventListener('visibilitychange', resume); reduced.addEventListener('change', resume);
        },
        attach(canvas, appearance = 'ocean') {
            canvas.width = canvas.height = size;
            const ctx = canvas.getContext('2d'), texture = surface(appearance);
            const record = { canvas, ctx, texture, appearance, pixels: ctx.createImageData(size, size), phase: texture.phase, active: false, speed: .005, visible: true, time: 0, drawn: 0 };
            records.set(canvas, record); observer?.observe(canvas); draw(record);
            if (!frame) resume();
        },
        setActive(canvas, active) { const record = records.get(canvas); if (record) record.active = active; },
        setAppearance(canvas, appearance = 'ocean') {
            const record = records.get(canvas);
            if (!record || record.appearance === appearance) return;
            record.appearance = appearance; record.texture = surface(appearance); draw(record);
        },
        preview(canvas, appearance) {
            canvas.width = canvas.height = size;
            const ctx = canvas.getContext('2d'), texture = surface(appearance);
            draw({ ctx, texture, pixels: ctx.createImageData(size, size), phase: texture.phase });
        },
        detach(canvas) { observer?.unobserve(canvas); records.delete(canvas); if (!records.size) { cancelAnimationFrame(frame); frame = 0; } },
        clear() { observer?.disconnect(); records.clear(); cancelAnimationFrame(frame); frame = 0; },
        dispose() { this.clear(); document.removeEventListener('visibilitychange', resume); reduced.removeEventListener('change', resume); }
    };
})();
