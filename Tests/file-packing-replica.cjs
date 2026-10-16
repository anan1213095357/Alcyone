const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const wraps = [], bags = [], sent = [], animated = [];
let receive;
class Element {
    constructor() { this.style = {}; this.children = []; this.nodes = new Map(); this.classList = { add() {}, remove() {} }; }
    append(...nodes) { this.children.push(...nodes); }
    querySelector(selector) { if (!this.nodes.has(selector)) this.nodes.set(selector, new Element()); return this.nodes.get(selector); }
    remove() { this.removed = true; }
    animate(frames, options) {
        animated.push(this);
        let finished = Promise.resolve();
        if (this.className === 'packing-wrap' && options.duration === 650) finished = new Promise(resolve => wraps.push(resolve));
        if (this.className === 'packing-parcel' && options.duration === 900) finished = new Promise(resolve => bags.push(resolve));
        return { finished };
    }
}
const document = { body: new Element(), createElement: () => new Element(), querySelectorAll: () => [] };
vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../Alcyone/wwwroot/js/file-packing.js'), 'utf8'), {
    document, Image: class {}, devicePixelRatio: 1.5, innerWidth: 1920, innerHeight: 1080,
    setTimeout, clearTimeout,
    window: { external: { receiveMessage(callback) { receive = callback; }, sendMessage(message) { sent.push(message); } } }
});
const tick = () => new Promise(resolve => setImmediate(resolve));
const replicas = () => document.body.children.filter(layer => layer.children[0]?.className === 'packing-original-snapshot');
const send = original => receive('alcyone:desktop:pack-file:' + JSON.stringify({ name: '原文件.txt', x: 300, y: 180, original }));
(async () => {
    const first = { image: 'data:image/png;base64,b3JpZ2luYWwtcGl4ZWxz', left: 240, top: 150, width: 120, height: 90 };
    send(first);
    const firstLayer = replicas()[0], image = firstLayer.children[0];
    assert.equal(image.src, first.image, 'Original image bytes are passed through without redrawing');
    assert.equal(image.style.left, '160px');
    assert.equal(image.style.top, '100px');
    assert.equal(image.style.width, '80px', 'Image retains its physical width at 150% DPI');
    assert.equal(image.style.height, '60px', 'Filename and icon retain their physical height');
    await tick();
    assert.equal(wraps.length, 1);
    assert.ok(!animated.includes(image), 'Replica stays untouched until box wrapping finishes');
    send({ ...first, left: -30, image: 'data:image/png;base64,c2Vjb25kLWljb24=' });
    const secondLayer = replicas()[1], secondImage = secondLayer.children[0];
    assert.equal(secondImage.style.left, '-20px', 'Snapshot position is not clamped or shifted');
    assert.ok(!animated.includes(secondImage), 'Queued files retain their replicas immediately');
    wraps.shift()(); await tick();
    assert.ok(animated.includes(image), 'Replica disappears only after wrapping completes');
    assert.ok(!animated.includes(secondImage), 'Next file remains on desktop while waiting');
    bags.shift()(); await tick();
    assert.equal(firstLayer.removed, true, 'Completed animation cleans up its replica');
    assert.ok(!secondLayer.removed);
    wraps.shift()(); await tick(); bags.shift()(); await tick();
    assert.equal(secondLayer.removed, true);
    assert.deepEqual(sent, [], 'Observed native deletions are never deleted again');
    console.log('PASS: original pixels, DPI, position, queued replicas, wrap timing and cleanup.');
})().catch(error => { console.error(error); process.exitCode = 1; });
