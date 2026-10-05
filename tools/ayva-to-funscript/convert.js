#!/usr/bin/env node
/**
 * Ayva Stroker Lite 的 tempest-stroke → MultiFunPlayer 的 .funscript
 *
 * 输入：Ayva Stroker Lite 导出的 ayva-behaviors.json（JSON 数组，每项 {name, type, data}），
 *       或者直接从 localStorage 复制出来的 {"名字": {"type": "...", "data": {...}}} 对象。
 * 输出：每个动作轴一个 .funscript（MFP 直接可用）：
 *         <名字>.funscript        ← 主轴（stroke / L0）
 *         <名字>.L1.funscript     ← surge
 *         <名字>.L2.funscript     ← sway
 *         <名字>.R0.funscript     ← twist
 *         <名字>.R1.funscript     ← roll
 *         <名字>.R2.funscript     ← pitch
 *       把 <名字> 改成和你视频同名，MFP 就会自动配上。
 *
 * 用法：
 *   node convert.js --input ayva-behaviors.json --duration 120 --bpm 120
 *   node convert.js --input ayva-behaviors.json --only "down-forward" --duration 60
 *   node convert.js --list --input ayva-behaviors.json       只列出里面有哪些脚本
 *
 * 说明：tempest-stroke 是"无限循环"的运动描述，所以必须指定生成多长（--duration 秒，
 * 默认 120）。生成过程按真实时间采样（50Hz），所以 120 秒的脚本大约需要跑 120 秒。
 */
import fs from 'node:fs';
import path from 'node:path';
import { Ayva, TempestStroke } from 'ayvajs';

const ALL_AXES = ['L0', 'L1', 'L2', 'R0', 'R1', 'R2'];
const AXIS_ALIAS = { L0: 'stroke', L1: 'surge', L2: 'sway', R0: 'twist', R1: 'roll', R2: 'pitch' };

/** ayvajs 在浏览器里用 Web Worker 计时，Node 下要给它一个自己的计时器（now() 返回秒）。 */
class NodeTimer {
  now() {
    return performance.now() / 1000;
  }

  sleep(delay) {
    // ayvajs 收尾时偶尔会传 NaN，兜一下，避免 Node 打 TimeoutNaNWarning
    const seconds = Number.isFinite(delay) ? Math.max(0, delay) : 0;
    return new Promise((resolve) => setTimeout(resolve, seconds * 1000));
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
    duration: 120,
    bpm: 120,
    sample: 20,
    only: null,
    axes: null,
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
    // localStorage 里 {"custom-behaviors--all": "{\"名字\":{\"type\":...,\"data\":{...}}}"}
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
    throw new Error('没解析出任何脚本：请确认文件是 Ayva Stroker Lite 导出的 ayva-behaviors.json');
  }

  return entries;
}

/**
 * 按真实时间跑一段 tempest-stroke，采样各轴的位置（0..1）。
 * @returns {Promise<Array<{t:number, values:Record<string, number>}>>}
 */
async function recordStroke(entries, bpm, durationSeconds, sampleMs, axes) {
  const ayva = new Ayva().defaultConfiguration();
  const timer = new NodeTimer();
  ayva.setTimer(timer);
  ayva.addOutputDevice(new NullDevice());

  const available = axes.filter((axis) => ayva.getAxis(axis) != null);
  const behavior = new TempestStroke(entries, bpm, 0, timer);

  const samples = [];
  const started = performance.now();

  const sample = () => {
    const values = {};
    for (const axis of available) {
      const value = ayva.getAxis(axis)?.value;
      if (typeof value === 'number') values[axis] = value;
    }
    samples.push({ t: (performance.now() - started) / 1000, values });
  };

  sample();
  const sampler = setInterval(sample, sampleMs);
  const running = ayva.do(behavior);

  await new Promise((resolve) => setTimeout(resolve, durationSeconds * 1000));

  clearInterval(sampler);
  ayva.stop();
  try { await running; } catch { /* stop() 会让行为提前结束，正常 */ }

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
    if (last && last.pos === pos && last.at === at) continue;
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

  if (options.help || !options.input) {
    console.log(`Ayva Stroker Lite 的 tempest-stroke → MultiFunPlayer .funscript

用法:
  node convert.js --input ayva-behaviors.json [选项]

选项:
  -i, --input <文件>    ayva-behaviors.json（站点导出）或 localStorage 里复制的 JSON
  -o, --out <目录>      输出目录（默认当前目录）
  -d, --duration <秒>   生成多长（默认 120；tempest-stroke 本身是无限循环的）
  -b, --bpm <数值>      节拍（默认 120，与站点里设置的 bpm 对应）
      --sample <毫秒>   采样间隔（默认 20ms = 50Hz，和 ayvajs 内部频率一致）
      --only <名字>     只转换其中一个脚本
      --axes <列表>     只输出这些轴，例如 L0,R0（默认输出所有会动的轴）
      --list            只列出文件里有哪些脚本，不转换
      --keep-collinear  不去掉共线的中间点（默认会去掉，文件更小）
`);
    return;
  }

  const entries = loadBehaviors(options.input);
  const tempest = entries.filter((e) => e.type === 'tempest-stroke');
  const others = entries.filter((e) => e.type !== 'tempest-stroke');

  console.log(`读入 ${entries.length} 个脚本：tempest-stroke ${tempest.length} 个` +
    (others.length ? `，其它类型 ${others.length} 个（${others.map((o) => `${o.name}(${o.type})`).join('、')}，本工具暂不转换）` : ''));

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

  for (const entry of targets) {
    console.log(`\n=== 转换「${entry.name}」：bpm=${options.bpm}，时长=${options.duration}s ===`);
    const { samples, available } = await recordStroke(entry.data, options.bpm, options.duration, options.sample, ALL_AXES);

    const wanted = options.axes ? available.filter((a) => options.axes.includes(a)) : available;
    let written = 0;

    for (const axis of wanted) {
      const actions = toActions(samples, axis, options.keepCollinear);
      const positions = new Set(actions.map((a) => a.pos));
      if (positions.size < 2) {
        console.log(`  ${axis}(${AXIS_ALIAS[axis]}): 全程不动，跳过`);
        continue;
      }

      const suffix = axis === 'L0' ? '' : `.${axis}`;
      const file = path.join(options.out, `${entry.name}${suffix}.funscript`);
      writeFunscript(file, actions, axis, entry.name, options.bpm);
      console.log(`  ${axis}(${AXIS_ALIAS[axis]}): ${actions.length} 个点 → ${path.basename(file)}` +
        `  [位置范围 ${Math.min(...positions)}..${Math.max(...positions)}]`);
      written += 1;
    }

    if (written === 0) console.log('  这个脚本没有任何轴会动，没生成文件');
  }

  console.log('\n完成。把生成的文件名改成和你的视频同名（例如 video.mp4 + video.funscript / video.L1.funscript），MultiFunPlayer 就会自动加载。');
}

main().catch((error) => {
  console.error(`出错：${error.message}`);
  process.exitCode = 1;
});
