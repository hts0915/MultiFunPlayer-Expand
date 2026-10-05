#!/usr/bin/env node
/**
 * Ayva Stroker Lite 的 tempest-stroke → MultiFunPlayer 的 .funscript
 *
 * 输入（三选一）：
 *   1. --library            直接用 ayvajs 内置行程库（42 条，与 Ayva Stroker Lite 内置的一致）
 *   2. Ayva Stroker Lite 导出的 ayva-behaviors.json（JSON 数组，每项 {name, type, data}）
 *   3. 从 localStorage 复制出来的 {"名字": {"type": "...", "data": {...}}} 对象
 * 输出：每个动作轴一个 .funscript（MFP 直接可用）：
 *         <名字>.funscript        ← 主轴（stroke / L0）
 *         <名字>.L1.funscript     ← surge
 *         <名字>.L2.funscript     ← sway
 *         <名字>.R0.funscript     ← twist
 *         <名字>.R1.funscript     ← roll
 *         <名字>.R2.funscript     ← pitch
 *       把 <名字> 改成和你视频同名，MFP 就会自动配上；也可以直接当"预设"用（不需要视频）。
 *
 * 用法：
 *   node convert.js --library --out out --duration 180 --bpm 120
 *   node convert.js --input ayva-behaviors.json --duration 180
 *   node convert.js --library --list
 *
 * 说明：tempest-stroke 是"无限循环"的运动描述，所以必须指定生成多长（--duration 秒）。
 * 转换用虚拟时钟跑（不等真实时间），42 条 × 3 分钟大约几秒钟就跑完。
 */
import fs from 'node:fs';
import path from 'node:path';
import { Ayva, TempestStroke } from 'ayvajs';

const ALL_AXES = ['L0', 'L1', 'L2', 'R0', 'R1', 'R2'];
const AXIS_ALIAS = { L0: 'stroke', L1: 'surge', L2: 'sway', R0: 'twist', R1: 'roll', R2: 'pitch' };

/**
 * 虚拟时钟：ayvajs 用 timer.now()/sleep() 推进动作，把时间改成"瞬间流逝"后
 * 整段动作可以在毫秒级算完（不用真的等 3 分钟）。now() 单位是秒。
 */
class VirtualTimer {
  time = 0;

  onStep = null;

  limit = Infinity;

  stopRequested = false;

  now() {
    return this.time;
  }

  sleep(delay) {
    const seconds = Number.isFinite(delay) ? Math.max(0, delay) : 0;
    this.time += seconds;
    if (this.onStep) this.onStep(this.time);
    return Promise.resolve();
  }
}

/** 不真的输出到设备的空设备。 */
class NullDevice {
  connected = false;

  async requestConnection() {
    this.connected = true;
  }

  async disconnect() {
    this.connected = false;
  }

  write() {}
}

function parseArgs(argv) {
  const options = {
    input: null,
    out: '.',
    duration: 180,
    bpm: 120,
    sample: 20,
    only: null,
    axes: null,
    library: false,
    list: false,
    keepCollinear: false,
  };

  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i];
    const next = () => argv[i + 1];
    switch (arg) {
      case '--input': case '-i': options.input = next(); i += 1; break;
      case '--out': case '-o': options.out = next(); i += 1; break;
      case '--duration': case '-d': options.duration = Number(next()); i += 1; break;
      case '--bpm': case '-b': options.bpm = Number(next()); i += 1; break;
      case '--sample': options.sample = Number(next()); i += 1; break;
      case '--only': options.only = next(); i += 1; break;
      case '--axes': options.axes = next().split(',').map((s) => s.trim().toUpperCase()); i += 1; break;
      case '--library': options.library = true; break;
      case '--list': options.list = true; break;
      case '--keep-collinear': options.keepCollinear = true; break;
      case '--help': case '-h': options.help = true; break;
      default:
        if (arg.startsWith('--')) throw new Error(`未知参数：${arg}`);
    }
  }

  return options;
}

/** 读入并统一成 [{name, type, data}] 数组。 */
function loadBehaviors(file) {
  const text = fs.readFileSync(file, 'utf8');
  const json = JSON.parse(text);

  const entries = [];
  if (Array.isArray(json)) {
    for (const item of json) {
      if (item && typeof item === 'object' && item.name && item.type) {
        entries.push({ name: String(item.name), type: String(item.type), data: item.data });
      }
    }
  } else if (json && typeof json === 'object') {
    for (const [key, value] of Object.entries(json)) {
      let inner = value;
      if (typeof inner === 'string') {
        try { inner = JSON.parse(inner); } catch { continue; }
      }
      if (inner && typeof inner === 'object' && inner.type && inner.data) {
        entries.push({ name: key, type: String(inner.type), data: inner.data });
        continue;
      }
      if (inner && typeof inner === 'object') {
        for (const [name, behavior] of Object.entries(inner)) {
          if (behavior && typeof behavior === 'object' && behavior.type && behavior.data) {
            entries.push({ name, type: String(behavior.type), data: behavior.data });
          }
        }
      }
    }
  }

  if (entries.length === 0) {
    throw new Error('没解析出任何脚本：请确认文件是 Ayva Stroker Lite 导出的 ayva-behaviors.json（或 localStorage 里的 custom-behaviors--all）');
  }

  return entries;
}

/** ayvajs 内置行程库（就是 Ayva Stroker Lite 里那套内置 TempestStroke）。 */
async function loadLibrary() {
  const library = (await import('ayvajs/src/util/tempest-stroke-library.js')).default;
  return Object.entries(library).map(([name, data]) => ({ name, type: 'tempest-stroke', data }));
}

/**
 * 用虚拟时钟跑一段 tempest-stroke，按 sampleMs 采样各轴位置（0..1）。
 * @returns {Promise<{samples: Array<{t:number, values:Record<string, number>}>, available: string[]}>}
 */
async function recordStroke(config, bpm, durationSeconds, sampleMs, axes) {
  const ayva = new Ayva().defaultConfiguration();
  const timer = new VirtualTimer();
  ayva.setTimer(timer);
  ayva.addOutputDevice(new NullDevice());

  const available = axes.filter((axis) => ayva.getAxis(axis) != null);
  const samples = [];
  const minStep = sampleMs / 1000;
  let lastSample = -Infinity;

  const sample = (t) => {
    const values = {};
    for (const axis of available) {
      const value = ayva.getAxis(axis)?.value;
      if (typeof value === 'number') values[axis] = value;
    }
    samples.push({ t, values });
  };

  sample(0);
  lastSample = 0;

  timer.onStep = (t) => {
    if (timer.stopRequested) return;

    if (t - lastSample >= minStep) {
      lastSample = t;
      sample(t);
    }

    if (t >= durationSeconds) {
      timer.stopRequested = true;
      ayva.stop();
    }
  };

  const behavior = new TempestStroke(config, bpm, 0, timer);
  try {
    await ayva.do(behavior);
  } catch {
    // 到时长后 stop() 会让行为提前结束，属于正常
  }

  if (samples[samples.length - 1]?.t < durationSeconds) sample(durationSeconds);
  return { samples, available };
}

/** 采样点 → funscript actions（毫秒 + 0..100），并去掉重复点与共线的中间点。 */
function toActions(samples, axis, keepCollinear) {
  const actions = [];
  for (const { t, values } of samples) {
    const value = values[axis];
    if (typeof value !== 'number') continue;

    const pos = Math.round(Math.min(1, Math.max(0, value)) * 100);
    const at = Math.round(t * 1000);
    const last = actions[actions.length - 1];
    if (last && last.pos === pos) continue;
    actions.push({ at, pos });
  }

  if (keepCollinear || actions.length < 3) return actions;

  // 丢掉与前后共线的点：funscript 本来就是线性插值，不影响结果，只让文件更小
  const filtered = [actions[0]];
  for (let i = 1; i < actions.length - 1; i += 1) {
    const a = filtered[filtered.length - 1];
    const b = actions[i];
    const c = actions[i + 1];
    const slope1 = (b.pos - a.pos) / (b.at - a.at || 1);
    const slope2 = (c.pos - b.pos) / (c.at - b.at || 1);
    if (Math.abs(slope1 - slope2) > 1e-6) filtered.push(b);
  }
  filtered.push(actions[actions.length - 1]);
  return filtered;
}

function writeFunscript(file, actions, axis, name, bpm) {
  const funscript = {
    version: '1.0',
    inverted: false,
    range: 100,
    actions,
    metadata: {
      creator: 'ayva-to-funscript',
      source: `Ayva Stroker Lite tempest-stroke「${name}」(${AXIS_ALIAS[axis] ?? axis})`,
      bpm,
    },
  };

  fs.writeFileSync(file, JSON.stringify(funscript), 'utf8');
}

async function main() {
  const options = parseArgs(process.argv.slice(2));

  if (options.help || (!options.input && !options.library)) {
    console.log(`Ayva Stroker Lite 的 tempest-stroke → MultiFunPlayer .funscript

用法:
  node convert.js --library --out out --duration 180 --bpm 120     # 转内置库（42 条）
  node convert.js --input ayva-behaviors.json --out out            # 转站点导出的文件

选项:
      --library         使用 ayvajs 内置行程库（与 Ayva Stroker Lite 内置的一致）
  -i, --input <文件>    ayva-behaviors.json（站点导出）或 localStorage 里复制的 JSON
  -o, --out <目录>      输出目录（默认当前目录）
  -d, --duration <秒>   生成多长（默认 180；tempest-stroke 本身是无限循环的）
  -b, --bpm <数值>      节拍（默认 120，与站点里设置的 bpm 对应）
      --sample <毫秒>   采样间隔（默认 20ms = 50Hz，和 ayvajs 内部频率一致）
      --only <名字>     只转换其中一个脚本
      --axes <列表>     只输出这些轴，例如 L0,R0（默认输出所有会动的轴）
      --list            只列出有哪些脚本，不转换
      --keep-collinear  不去掉共线的中间点（默认会去掉，文件更小）
`);
    return;
  }

  const entries = options.library ? await loadLibrary() : loadBehaviors(options.input);
  if (options.library) console.log(`使用 ayvajs 内置行程库：${entries.length} 条`);

  const tempest = entries.filter((e) => e.type === 'tempest-stroke');
  const others = entries.filter((e) => e.type !== 'tempest-stroke');

  if (!options.library) {
    console.log(`读入 ${entries.length} 个脚本：tempest-stroke ${tempest.length} 个` +
      (others.length ? `，其它类型 ${others.length} 个（${others.map((o) => `${o.name}(${o.type})`).join('、')}，本工具暂不转换）` : ''));
  }

  if (options.list) {
    for (const entry of tempest) {
      const axes = Object.keys(entry.data ?? {}).filter((k) => ALL_AXES.includes(k.toUpperCase()));
      console.log(`  - ${entry.name}  [轴: ${axes.join(', ') || '?'}]`);
    }
    return;
  }

  let targets = tempest;
  if (options.only) {
    targets = tempest.filter((e) => e.name === options.only);
    if (targets.length === 0) throw new Error(`没有找到名字为「${options.only}」的 tempest-stroke，先用 --list 看看`);
  }

  fs.mkdirSync(options.out, { recursive: true });

  const started = performance.now();
  let files = 0;
  let totalActions = 0;

  for (const entry of targets) {
    const { samples, available } = await recordStroke(entry.data, options.bpm, options.duration, options.sample, ALL_AXES);
    const wanted = options.axes ? available.filter((a) => options.axes.includes(a)) : available;
    const written = [];

    for (const axis of wanted) {
      const actions = toActions(samples, axis, options.keepCollinear);
      if (new Set(actions.map((a) => a.pos)).size < 2) continue;

      const suffix = axis === 'L0' ? '' : `.${axis}`;
      const file = path.join(options.out, `${entry.name}${suffix}.funscript`);
      writeFunscript(file, actions, axis, entry.name, options.bpm);
      written.push(`${axis}(${actions.length}点)`);
      files += 1;
      totalActions += actions.length;
    }

    console.log(`  ${entry.name.padEnd(30)} ${written.join('  ') || '（没有任何轴会动，跳过）'}`);
  }

  const seconds = ((performance.now() - started) / 1000).toFixed(1);
  console.log(`\n完成：${targets.length} 个脚本 → ${files} 个文件（共 ${totalActions} 个动作点），耗时 ${seconds}s`);
  console.log(`输出目录：${path.resolve(options.out)}`);
  console.log('在 MultiFunPlayer 里两种用法：①「脚本预设」面板选这些文件直接播放（不需要视频）；②改成和视频同名放在一起。');
}

main().catch((error) => {
  console.error(`出错：${error.message}`);
  process.exitCode = 1;
});
