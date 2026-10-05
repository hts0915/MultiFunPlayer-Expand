import library from 'ayvajs/src/util/tempest-stroke-library.js';
import { Ayva, TempestStroke } from 'ayvajs';

class VT { time = 0; onStep = null; now(){ return this.time; }
  sleep(d){ const s = Number.isFinite(d) ? Math.max(0,d) : 0; this.time += s; if (this.onStep) this.onStep(this.time); return Promise.resolve(); } }
class ND { connected=false; async requestConnection(){this.connected=true;} async disconnect(){this.connected=false;} write(){} }

const value = (p, angle) => {
  const scale = 0.5*(p.to-p.from), mid = 0.5*(p.to+p.from);
  const a = angle + 0.5*Math.PI*p.phase;
  return mid - scale*Math.cos(a + p.ecc*Math.sin(a));
};

const name = process.argv[2] ?? 'down-forward';
const stroke = library[name];
const ayva = new Ayva().defaultConfiguration();
const timer = new VT();
ayva.setTimer(timer);
ayva.addOutputDevice(new ND());
const behavior = new TempestStroke(stroke, 120, 0, timer);

const rows = [];
timer.onStep = (t) => {
  if (t >= 1.2) { ayva.stop(); return; }
  if (rows.length && t - rows[rows.length-1].t < 0.09) return;
  rows.push({ t, angle: behavior.angle, L0: ayva.getAxis('L0')?.value, formula: value(stroke.L0, behavior.angle) });
  if (rows.length > 10) { ayva.stop(); }
};
try { await ayva.do(behavior); } catch {}
console.log('t        angle    引擎L0    公式(angle)   diff');
for (const r of rows.slice(0, 8)) {
  console.log(`${r.t.toFixed(3)}   ${r.angle.toFixed(4)}   ${(r.L0 ?? 0).toFixed(4)}   ${r.formula.toFixed(4)}   ${Math.abs((r.L0??0)-r.formula).toExponential(2)}`);
}
