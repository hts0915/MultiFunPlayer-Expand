import fs from 'node:fs';
import library from 'ayvajs/src/util/tempest-stroke-library.js';
const golden = JSON.parse(fs.readFileSync('golden.json', 'utf8'));
const value = (p, angle) => {
  const scale = 0.5 * (p.to - p.from), mid = 0.5 * (p.to + p.from);
  const a = angle + 0.5 * Math.PI * p.phase;
  return mid - scale * Math.cos(a + p.ecc * Math.sin(a));
};
for (const [name, data] of Object.entries(golden)) {
  const stroke = library[name];
  // 用引擎自己的角度区间：连续采样的相邻两点若角度回退，说明遇到生成器换轮次（跳变），跳过该点
  let best = { err: Infinity, offset: 0 }, worst = 0, count = 0;
  for (let offset = -0.05; offset <= 0.0501; offset += 0.002) {
    let err = 0;
    for (const s of data.samples) {
      const angle = (data.bpm * (s.t + offset) * 2 * Math.PI / 60) % (2 * Math.PI);
      for (const [axis, p] of Object.entries(stroke)) {
        const expected = s.values[axis];
        if (typeof expected !== 'number') continue;
        err = Math.max(err, Math.abs(value(p, angle) - expected));
      }
    }
    if (err < best.err) best = { err, offset };
  }
  console.log(`${name.padEnd(20)} 最大偏差 ${best.err.toExponential(2)}（偏移 ${(best.offset*1000).toFixed(0)} ms）`);
}
