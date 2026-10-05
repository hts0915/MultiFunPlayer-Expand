import library from 'ayvajs/src/util/tempest-stroke-library.js';
const AXES = ['L0','L1','L2','R0','R1','R2'];
const r = (v) => Math.round(Number(v ?? 0) * 1e4) / 1e4;
const out = {};
for (const [name, stroke] of Object.entries(library)) {
  const axes = {};
  for (const [axis, p] of Object.entries(stroke)) {
    if (!AXES.includes(axis) || !p) continue;
    axes[axis] = [r(p.from), r(p.to), r(p.phase), r(p.ecc)];
  }
  out[name] = axes;
}
console.log(JSON.stringify(out));
