import fs from 'node:fs';
import library from 'ayvajs/src/util/tempest-stroke-library.js';
const golden = JSON.parse(fs.readFileSync('golden.json', 'utf8'));

const value = (p, angle) => {
  const scale = 0.5 * (p.to - p.from);
  const mid = 0.5 * (p.to + p.from);
  const a = angle + 0.5 * Math.PI * p.phase;
  return mid - scale * Math.cos(a + p.ecc * Math.sin(a));
};

// 插件引擎的等价解析式：θ 在 [0,π) 内循环（每个上下行程 π 弧度）
const angleAt = (t, bpm) => (bpm * t * 2 * Math.PI / 60) % Math.PI;

for (const [name, data] of Object.entries(golden)) {
  const stroke = library[name];
  let best = { err: Infinity, offset: 0 };
  for (let offset = -0.05; offset <= 0.0501; offset += 0.002) {
    let err = 0;
    for (const s of data.samples) {
      const angle = angleAt(s.t + offset, data.bpm);
      for (const [axis, p] of Object.entries(stroke)) {
        const expected = s.values[axis];
        if (typeof expected !== 'number') continue;
        err = Math.max(err, Math.abs(value(p, angle) - expected));
      }
    }
    if (err < best.err) best = { err, offset };
  }
  console.log(`${name.padEnd(20)} 最大偏差 ${best.err.toExponential(2)}   最优时间偏移 ${(best.offset*1000).toFixed(0)} ms`);
}
