import library from 'ayvajs/src/util/tempest-stroke-library.js';
import { Ayva, TempestStroke } from 'ayvajs';

// 真实计时器（和浏览器里一样：sleep 真的等）
class RealTimer { now(){ return performance.now()/1000; }
  sleep(d){ const s = Number.isFinite(d) ? Math.max(0,d) : 0; return new Promise(r => setTimeout(r, s*1000)); } }
class ND { connected=false; async requestConnection(){this.connected=true;} async disconnect(){this.connected=false;} write(){} }

const value = (p, angle) => {
  const scale = 0.5*(p.to-p.from), mid = 0.5*(p.to+p.from);
  const a = angle + 0.5*Math.PI*p.phase;
  return mid - scale*Math.cos(a + p.ecc*Math.sin(a));
};

const name = process.argv[2] ?? 'down-forward';
const stroke = library[name];
const ayva = new Ayva().defaultConfiguration();
const timer = new RealTimer();
ayva.setTimer(timer);
ayva.addOutputDevice(new ND());
const behavior = new TempestStroke(stroke, 120, 0, timer);

const t0 = performance.now();
const rows = [];
const sampler = setInterval(() => {
  const t = (performance.now() - t0) / 1000;
  const angle = behavior.angle;
  const axis = ayva.getAxis('L0')?.value;
  const model = value(stroke.L0, 120 * t * 2 * Math.PI / 60);
  rows.push({ t, angle, axis, model });
}, 100);

const run = ayva.do(behavior);
await new Promise(r => setTimeout(r, 4000));
clearInterval(sampler);
ayva.stop();
try { await run; } catch {}

console.log('t     引擎θ     引擎L0    我的模型L0   偏差');
let maxErr = 0, maxErrAngle = 0;
for (const r of rows) {
  const err = Math.abs((r.axis ?? 0) - r.model);
  const errAngle = Math.abs(((r.angle - 120*r.t*2*Math.PI/60) % (2*Math.PI)));
  maxErr = Math.max(maxErr, err);
  maxErrAngle = Math.min(Math.max(maxErrAngle, 0), errAngle);   // 仅显示
  if (rows.indexOf(r) < 10) console.log(`${r.t.toFixed(3)}  ${r.angle.toFixed(4)}  ${(r.axis??0).toFixed(4)}  ${r.model.toFixed(4)}  ${err.toExponential(2)}`);
}
console.log(`\n采样 ${rows.length} 点，模型与引擎最大偏差 = ${maxErr.toExponential(2)}`);
