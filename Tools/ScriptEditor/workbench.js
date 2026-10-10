import * as monaco from 'monaco-editor/editor/editor.api.js';
import 'monaco-editor/languages/definitions/csharp/register.js';
import 'monaco-editor/editor/contrib/find/browser/findController.js';
import 'monaco-editor/editor/contrib/folding/browser/folding.js';
import 'monaco-editor/editor/contrib/suggest/browser/suggestController.js';
import 'monaco-editor/editor/contrib/hover/browser/hoverContribution.js';
import 'monaco-editor/editor/contrib/bracketMatching/browser/bracketMatching.js';
import 'monaco-editor/editor/contrib/comment/browser/comment.js';
import 'monaco-editor/editor/contrib/wordHighlighter/browser/wordHighlighter.js';
import 'monaco-editor/editor/contrib/links/browser/links.js';
import 'monaco-editor/editor/contrib/linesOperations/browser/linesOperations.js';
import 'monaco-editor/editor/contrib/multicursor/browser/multicursor.js';

self.MonacoEnvironment = { getWorker: () => new Worker(new URL('./worker.js', import.meta.url), { type: 'module' }) };
monaco.editor.defineTheme('alcyone-studio', {
    base: 'vs-dark', inherit: true,
    rules: [{ token: 'comment', foreground: '6F9477' }, { token: 'keyword', foreground: 'C792EA' }, { token: 'string', foreground: 'CE9178' }, { token: 'number', foreground: 'B5CEA8' }, { token: 'type.identifier', foreground: '4EC9B0' }],
    colors: { 'editor.background': '#181C24', 'editor.foreground': '#D4D8E2', 'editorLineNumber.foreground': '#596272', 'editorLineNumber.activeForeground': '#B5C6DF', 'editor.lineHighlightBackground': '#222834', 'editor.selectionBackground': '#36557A80', 'editorCursor.foreground': '#91BFFF', 'editorIndentGuide.background1': '#2A303C', 'editorWidget.background': '#222834', 'editorWidget.border': '#3A4454' }
});
let completion;
export function create(host, status, reference, docs) {
    if (!document.querySelector('link[data-script-editor]')) {
        const link = document.createElement('link'); link.rel = 'stylesheet'; link.href = new URL('./workbench.css', import.meta.url); link.dataset.scriptEditor = ''; document.head.append(link);
    }
    completion?.dispose();
    completion = monaco.languages.registerCompletionItemProvider('csharp', {
        triggerCharacters: ['.'],
        provideCompletionItems(model, position) {
            const word = model.getWordUntilPosition(position);
            const before = model.getLineContent(position.lineNumber).slice(0, word.startColumn - 1);
            const prefix = before.match(/[A-Za-z_][\w.]*\.$/)?.[0] ?? '';
            const items = docs.filter(d => !prefix || d.title.startsWith(prefix)).map(d => {
                const name = d.title.replace(/<T>$/, '');
                const sampleStart = d.insert.indexOf(name);
                const sample = sampleStart >= 0 ? d.insert.slice(sampleStart).split('\n')[0] : d.insert;
                return { label: prefix ? d.title.slice(prefix.length) : d.title,
                    kind: monaco.languages.CompletionItemKind.Method,
                    documentation: d.description, detail: d.signature,
                    insertText: prefix && sample.startsWith(prefix) ? sample.slice(prefix.length) : sample,
                    range: new monaco.Range(position.lineNumber, word.startColumn, position.lineNumber, word.endColumn) };
            });
            if (prefix === 'Api.') for (const name of ['Input', 'Recognition', 'Color', 'Vars'])
                items.push({ label: name, kind: monaco.languages.CompletionItemKind.Module, insertText: name,
                    range: new monaco.Range(position.lineNumber, word.startColumn, position.lineNumber, word.endColumn) });
            return { suggestions: items };
        }
    });
    const editor = monaco.editor.create(host, {
        model: null, theme: 'alcyone-studio', automaticLayout: true, fontSize: 14, lineHeight: 23,
        fontFamily: "Cascadia Code, Consolas, monospace", padding: { top: 18, bottom: 18 },
        minimap: { enabled: true, maxColumn: 90 }, scrollBeyondLastLine: false,
        roundedSelection: false, renderLineHighlight: 'all', smoothScrolling: true,
        bracketPairColorization: { enabled: true }, guides: { indentation: true, bracketPairs: true },
        tabSize: 4, insertSpaces: true, folding: true, glyphMargin: true,
        ariaLabel: 'C# 脚本编辑器', fixedOverflowWidgets: true, stickyScroll: { enabled: false }
    });
    const models = new Map(); let active; let disposed = false;
    const invoke = (method, ...args) => { if (!disposed) reference.invokeMethodAsync(method, ...args).catch(() => {}); };
    editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => invoke('CompileShortcut'));
    editor.addCommand(monaco.KeyCode.F8, () => invoke('CompileShortcut'));
    const positionSubscription = editor.onDidChangeCursorPosition(e => { status.textContent = `行 ${e.position.lineNumber}，列 ${e.position.column}`; });
    const stopKeys = e => e.stopPropagation();
    const surface = host.closest('.studio-overlay');
    surface.addEventListener('keydown', stopKeys);
    const instance = {
        open(path, source) {
            if (active) models.get(active).view = editor.saveViewState();
            let item = models.get(path);
            if (!item) {
                item = { model: monaco.editor.createModel(source, 'csharp', monaco.Uri.parse('inmemory://scripts/' + path.replaceAll('\\', '/'))), original: source, dirty: false };
                item.subscription = item.model.onDidChangeContent(() => {
                    const dirty = item.model.getValue() !== item.original;
                    if (dirty !== item.dirty) { item.dirty = dirty; invoke('DirtyChanged', path, dirty); }
                });
                models.set(path, item);
            }
            active = path; editor.setModel(item.model); if (item.view) editor.restoreViewState(item.view); editor.focus();
        },
        dirtyFiles() { return [...models].filter(([,v]) => v.model.getValue() !== v.original).map(([path,v]) => ({path, source:v.model.getValue(), original:v.original})); },
        markSaved(path, source) { const item = models.get(path); if (item) { item.original = source; item.dirty = item.model.getValue() !== source; invoke('DirtyChanged', path, item.dirty); } },
        reload(path, source) { const item = models.get(path); if (item) { item.original = source; item.model.setValue(source); item.dirty = false; invoke('DirtyChanged', path, false); } },
        action(id) { editor.getAction(id)?.run(); },
        insert(code) { const selection = editor.getSelection(); if (selection) { editor.executeEdits('api-example', [{range: selection, text: code, forceMoveMarkers:true}]); editor.focus(); } },
        readonly(value) { editor.updateOptions({readOnly:value}); },
        reveal(line, column) { editor.setPosition({lineNumber:line,column}); editor.revealLineInCenter(line); editor.focus(); },
        diagnostics(errors) {
            for (const [path,item] of models) {
                monaco.editor.setModelMarkers(item.model, 'compile', errors.filter(e => e.file === path || e.file === path.split(/[\\/]/).pop()).map(e => ({severity:monaco.MarkerSeverity.Error,message:e.message,startLineNumber:e.line,startColumn:e.column,endLineNumber:e.line,endColumn:e.column+1})));
            }
        },
        dispose() { disposed = true; positionSubscription.dispose(); surface.removeEventListener('keydown',stopKeys); editor.dispose(); for(const item of models.values()) {item.subscription.dispose();item.model.dispose();} completion?.dispose(); }
    };
    return instance;
}
