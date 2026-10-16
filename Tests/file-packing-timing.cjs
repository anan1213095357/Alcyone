const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

// Exercise the real animation with a held bagging animation and a native reply.
let receive, releaseBag, releaseFlight;
const sent = [], timers = new Map();
let timerId = 0;
class Element {
    constructor() { this.style = {}; this.children = []; this.nodes = new Map(); this.classList = { add() {}, remove() {} }; }
    append(...nodes) { this.children.push(...nodes); }
    querySelector(selector) {
        if (!this.nodes.has(selector)) this.nodes.set(selector, new Element());
        return this.nodes.get(selector);
    }
    remove() {}
    animate(frames, options) {
        let finished = Promise.resolve();
        if (this.className === 'packing-parcel' && options.duration === 900)
            finished = new Promise(resolve => { releaseBag = resolve; });
        if (this.className === 'packing-ship' && frames.length === 91 && releaseBag)
            finished = new Promise(resolve => { releaseFlight = resolve; });
        return { finished };
    }
}
const document = { createElement: () => new Element(), body: new Element(), querySelectorAll: () => [] };
vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../Alcyone/wwwroot/js/file-packing.js'), 'utf8'), {
    document, Image: class {}, devicePixelRatio: 1, innerWidth: 1920, innerHeight: 1080,
    setTimeout(callback) { const id = ++timerId; timers.set(id, callback); return id; },
    clearTimeout(id) { timers.delete(id); },
    window: { external: { receiveMessage(callback) { receive = callback; }, sendMessage(message) { sent.push(message); } } }
});
const tick = () => new Promise(resolve => setImmediate(resolve));
(async () => {
    receive('alcyone:desktop:pack-file:' + JSON.stringify({ id: 'timing-check', name: 'test.txt', x: 500, y: 500 }));
    await tick();
    assert.equal(typeof releaseBag, 'function', 'Animation reaches the bagging phase');
    assert.deepEqual(sent, [], 'No deletion request before the file is fully bagged');
    releaseBag();
    await tick();
    assert.deepEqual(sent, ['alcyone:desktop:pack-commit:timing-check'], 'Deletion requested once after bagging finishes');
    assert.equal(releaseFlight, undefined, 'Ship waits for native deletion result');
    receive('alcyone:desktop:pack-result:' + JSON.stringify({ id: 'timing-check', success: true }));
    await tick();
    assert.equal(typeof releaseFlight, 'function', 'Ship departs after native confirmation');
    releaseFlight();
    await tick();
    assert.equal(timers.size, 0, 'Native reply clears its timeout');
    console.log('PASS: deletion follows completed bagging; departure follows native confirmation.');
})().catch(error => { console.error(error); process.exitCode = 1; });
