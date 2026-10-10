import { build } from 'esbuild';
import { copyFile, mkdir } from 'node:fs/promises';
const out = '../../Alcyone/wwwroot/editor';
await mkdir(out, { recursive: true });
await build({ entryPoints: { workbench: 'workbench.js', worker: 'node_modules/monaco-editor/esm/vs/editor/editor.worker.js' }, outdir: out, bundle: true, minify: true, format: 'esm', target: 'es2022', loader: { '.ttf': 'file' }, assetNames: '[name]-[hash]', legalComments: 'linked' });
await copyFile('node_modules/monaco-editor/LICENSE', out + '/LICENSE.monaco.txt').catch(() => copyFile('node_modules/monaco-editor/LICENSE.txt', out + '/LICENSE.monaco.txt'));
