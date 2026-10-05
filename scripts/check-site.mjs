import { readFile, access } from 'node:fs/promises';
import path from 'node:path';
const root = new URL('../site/', import.meta.url);
const html = await readFile(new URL('index.html', root), 'utf8');
for (const asset of [...html.matchAll(/(?:src|href)="([^"#]+)"/g)].map(m => m[1]).filter(s => !s.includes(':') && s !== './')) {
  if (!asset.startsWith('/') && !asset.includes('..')) await access(new URL(asset, root));
}
for (const theme of ['dark', 'light']) for (const prefix of ['regent-demo', 'widget-demo', 'widget-demo-hover']) await access(new URL(`assets/${prefix}-${theme}.png`, root));
if (!html.includes('not Authenticode signed') || !html.includes('Interactive illustration')) throw new Error('Missing preview or illustration disclosure');
const sources = await Promise.all(['index.html', 'app.js', 'style.css'].map(p => readFile(new URL(p, root), 'utf8')));
if (sources.some(s => /C:[/\\]Users|\.codex-local|sk-[a-zA-Z0-9]{20}/.test(s))) throw new Error('Private content in public site');
console.log('Site assets, both themes, preview disclosures and private-path checks passed.');
