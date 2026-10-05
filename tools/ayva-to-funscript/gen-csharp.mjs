/**
 * 从 ayvajs 生成两样东西：
 *   1. library-cs.txt —— 42 条行程的 C# 数据表
 *   2. golden.json    —— JS 引擎算出的参考值（用于校验 C# 移植是否逐值一致）
 */
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { Ayva, TempestStroke } from 'ayvajs';
import library from 'ayvajs/src/util/tempest-stroke-library.js';

const AXES = ['L0', 'L1', 'L2', 'R0', 'R1', 'R2'];
const outDir = path.dirname(fileURLToPath(import.meta.url));

// ---- 1) 看看到底用到哪些参数键 ----
const keys = new Set();
for (const stroke of Object.values(library)) {
  for (const params of Object.values(stroke)) {
    if (params && typeof params === 'object') Object.keys(params).forEach((k) => keys.add(k));
  }
}
console.log('参数键集合:', [...keys].join(', '));

// ---- 2) 生成 C# 表 ----
const num = (v) => {
  const n = Number(v);
  if (!Number.isFinite(n)) return '0';
  return Number.isInteger(n) ? `${n}` : n.toFixed(6).replace(/0+$/, '').replace(/\.$/, '');
};

let cs = '// 由 tools/ayva-to-funscript/gen-csharp.mjs 从 ayvajs 的 tempest-stroke-library.js 生成，勿手改。\n';
cs += 'private static readonly Dictionary<string, Dictionary<string, StrokeAxisParameter>> StrokeLibrary = new()\n{\n';
for (const [name, stroke] of Object.entries(library)) {
  cs += `    ["${name}"] = new()\n    {\n`;
  for (const [axis, params] of Object.entries(stroke)) {
    if (!AXES.includes(axis) || !params || typeof params !== 'object') continue;
    const from = num(params.from ?? 0.5);
    const to = num(params.to ?? 0.5);
    const phase = num(params.phase ?? 0);
    const ecc = num(params.ecc ?? 0);
    const shift = num(params.shift ?? 0);
    const frequency = num(params.frequency ?? 0);
    const center = num(params.center ?? 0);
    const noise = typeof params.noise === 'number' ? num(params.noise) : '0';
    cs += `        ["${axis}"] = new(${from}, ${to}, ${phase}, ${ecc}, ${shift}, ${frequency}, ${center}, ${noise}),\n`;
  }
  cs += '    },\n';
}
cs += '};\n';
fs.writeFileSync(path.join(outDir, 'library-cs.txt'), cs, 'utf8');
console.log(`已生成 library-cs.txt（${Object.keys(library).length} 条行程）`);

// ---- 3) 生成黄金参考值：用真引擎按每 20ms 采样几条行程 ----
class VirtualTimer {
  time = 0;
  onStep = null;
  now() { return this.time; }
  sleep(delay) {
    const s = Number.isFinite(delay) ? Math.max(0, delay) : 0;
    this.time += s;
    if (this.onStep) this.onStep(this.time);
    return Promise.resolve();
  }
}
class NullDevice {
  connected = false;
  async requestConnection() { this.connected = true; }
  async disconnect() { this.connected = false; }
  write() {}
}

async function record(config, bpm, seconds, sampleMs) {
  const ayva = new Ayva().defaultConfiguration();
  const timer = new VirtualTimer();
  ayva.setTimer(timer);
  ayva.addOutputDevice(new NullDevice());
  const samples = [];
  let last = -Infinity;
  const take = (t) => {
    const values = {};
    for (const axis of AXES) {
      const v = ayva.getAxis(axis)?.value;
      if (typeof v === 'number') values[axis] = v;
    }
    samples.push({ t: Number(t.toFixed(4)), values });
  };
  take(0);
  timer.onStep = (t) => {
    if (t - last >= sampleMs / 1000) { last = t; take(t); }
    if (t >= seconds) ayva.stop();
  };
  try { await ayva.do(new TempestStroke(config, bpm, 0, timer)); } catch { /* stop 正常 */ }
  return samples;
}

const golden = {};
for (const name of ['down-forward', 'orbit-grind', 'long-stroke-3', 'left-right-tease']) {
  golden[name] = {
    bpm: 120,
    samples: await record(library[name], 120, 6, 100),
  };
  console.log(`黄金参考：${name} → ${golden[name].samples.length} 个采样`);
}

fs.writeFileSync(path.join(outDir, 'golden.json'), JSON.stringify(golden, null, 1), 'utf8');
console.log('已生成 golden.json');
