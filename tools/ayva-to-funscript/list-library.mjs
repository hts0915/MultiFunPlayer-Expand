import library from 'ayvajs/src/util/tempest-stroke-library.js';
const names = Object.keys(library);
console.log(`内置行程库共 ${names.length} 条：`);
for (const name of names) {
  const axes = Object.entries(library[name])
    .filter(([k, v]) => v && typeof v === 'object' && ['L0','L1','L2','R0','R1','R2'].includes(k))
    .map(([k, v]) => { const moving = Number(v.from) !== Number(v.to); return moving ? k : null; })
    .filter(Boolean);
  console.log(`  ${name.padEnd(26)} 会动的轴: ${axes.join(',') || '（无）'}`);
}
