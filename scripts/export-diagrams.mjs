#!/usr/bin/env node
// Exports every docs/diagrams/*.drawio file to a light-mode PNG next to it.
//
// Uses the official draw.io image (jgraph/drawio) as the renderer and a local
// headless Chrome to drive its export page, so the PNGs match what draw.io
// itself draws. No npm dependencies: Node >= 22 (global WebSocket, fetch).
//
//   docker run -d --rm --name drawio -p 127.0.0.1:18080:8080 jgraph/drawio
//   node scripts/export-diagrams.mjs            # all diagrams
//   node scripts/export-diagrams.mjs erd        # only files whose name matches
//
// Env: DRAWIO_URL (default http://127.0.0.1:18080), CHROME (default google-chrome),
//      SCALE (default 2).

import { spawn } from 'node:child_process';
import { mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const DRAWIO_URL = process.env.DRAWIO_URL ?? 'http://127.0.0.1:18080';
const CHROME = process.env.CHROME ?? 'google-chrome';
const SCALE = Number(process.env.SCALE ?? 2);
const PORT = 9333;

const diagramsDir = resolve(dirname(fileURLToPath(import.meta.url)), '../docs/diagrams');
const filter = process.argv[2];
const files = readdirSync(diagramsDir)
  .filter((f) => f.endsWith('.drawio') && (!filter || f.includes(filter)))
  .sort();

if (files.length === 0) {
  console.error(`No .drawio files in ${diagramsDir}${filter ? ` matching "${filter}"` : ''}`);
  process.exit(1);
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const profile = mkdtempSync(join(tmpdir(), 'drawio-export-'));
const chrome = spawn(CHROME, [
  '--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check',
  `--remote-debugging-port=${PORT}`, `--user-data-dir=${profile}`, 'about:blank',
], { stdio: 'ignore' });

try {
  const wsUrl = await waitForDebugger();
  const cdp = await connect(wsUrl);
  const { targetId } = await cdp.send('Target.createTarget', { url: 'about:blank' });
  const { sessionId } = await cdp.send('Target.attachToTarget', { targetId, flatten: true });
  const page = (method, params) => cdp.send(method, params, sessionId);

  for (const file of files) {
    const xml = readFileSync(join(diagramsDir, file), 'utf8');
    await page('Page.navigate', { url: `${DRAWIO_URL}/export3.html` });
    await evaluate(page, waitFor('typeof render === "function" && document.readyState === "complete"'));

    const data = { xml, format: 'png', scale: SCALE, border: 20, theme: 'light', w: 0, h: 0 };
    await evaluate(page, `render(${JSON.stringify(data)}); true`);
    await evaluate(page, waitFor('document.getElementById("LoadingComplete") != null'));
    const bounds = JSON.parse(await evaluate(page,
      'document.getElementById("LoadingComplete").getAttribute("bounds")'));

    const width = Math.ceil(bounds.width);
    const height = Math.ceil(bounds.height);
    await page('Emulation.setDeviceMetricsOverride',
      { width, height, deviceScaleFactor: 1, mobile: false });
    const { data: png } = await page('Page.captureScreenshot',
      { format: 'png', clip: { x: 0, y: 0, width, height, scale: 1 }, captureBeyondViewport: true });

    const out = join(diagramsDir, `${basename(file, '.drawio')}.png`);
    writeFileSync(out, Buffer.from(png, 'base64'));
    console.log(`${file} -> ${basename(out)} (${width}x${height})`);
  }
  cdp.close();
} finally {
  chrome.kill();
  await sleep(300);
  rmSync(profile, { recursive: true, force: true });
}

function waitFor(condition, timeoutMs = 30000) {
  return `new Promise((ok, fail) => {
    const start = Date.now();
    (function poll() {
      if (${condition}) return ok(true);
      if (Date.now() - start > ${timeoutMs}) return fail(new Error('timeout: ' + ${JSON.stringify(condition)}));
      setTimeout(poll, 100);
    })();
  })`;
}

async function evaluate(page, expression) {
  const { result, exceptionDetails } = await page('Runtime.evaluate',
    { expression, awaitPromise: true, returnByValue: true });
  if (exceptionDetails) {
    throw new Error(exceptionDetails.exception?.description ?? exceptionDetails.text);
  }
  return result.value;
}

async function waitForDebugger() {
  for (let i = 0; i < 100; i++) {
    try {
      const res = await fetch(`http://127.0.0.1:${PORT}/json/version`);
      return (await res.json()).webSocketDebuggerUrl;
    } catch {
      await sleep(100);
    }
  }
  throw new Error(`Chrome did not open its debugger on port ${PORT}`);
}

function connect(url) {
  return new Promise((ok, fail) => {
    const ws = new WebSocket(url);
    const pending = new Map();
    let nextId = 1;
    ws.onerror = () => fail(new Error(`cannot connect to ${url}`));
    ws.onmessage = ({ data }) => {
      const msg = JSON.parse(data);
      const waiter = pending.get(msg.id);
      if (!waiter) return;
      pending.delete(msg.id);
      msg.error ? waiter.fail(new Error(msg.error.message)) : waiter.ok(msg.result);
    };
    ws.onopen = () => ok({
      send(method, params = {}, sessionId) {
        const id = nextId++;
        ws.send(JSON.stringify({ id, method, params, sessionId }));
        return new Promise((ok, fail) => pending.set(id, { ok, fail }));
      },
      close: () => ws.close(),
    });
  });
}
