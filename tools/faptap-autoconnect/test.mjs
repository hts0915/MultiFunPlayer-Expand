// faptap 自动连接脚本的单元测试（jsdom）—— 针对真实的三步流程
// 运行： node test.mjs
import { JSDOM } from "jsdom";
import { readFileSync } from "node:fs";

const SCRIPT = readFileSync(new URL("./faptap-autoconnect.user.js", import.meta.url), "utf8");

// ===== 以下四段 HTML 全部来自用户实际页面（Copy element），文字为网页翻译后的结果 =====
// ① 打开设备面板的入口按钮（带 svg 图标、非满宽）
const entry = (label = "连接") =>
  `<button class="bg-blue-500 hover:bg-blue-600 active:bg-blue-700 block rounded-md px-3 py-1.5 text-center font-semibold md:text-sm overflow-hidden disabled:opacity-75 cursor-pointer disabled:cursor-not-allowed flex items-center gap-x-1.5 shrink-0"><svg viewBox="0 0 24 24" fill="none" class="h-5 w-5 text-white mx-auto"><path fill="currentColor" d="M12 5.5C12 3.567 10.433 2 8.5 2C6.567 2 5 3.567 5 5.5H12Z"></path></svg><span><font dir="auto"><font dir="auto">${label}</font></font></span></button>`;

// ② 设备列表里的 Intiface 项（靠 logo 路径识别；文字被翻译成「内脸」）
const intifaceItem = (label = "内脸") =>
  `<div class="flex py-1.5 md:py-1.5 px-3 mx-1 my-0.5 rounded items-center gap-3 hover:bg-blue-500 cursor-pointer text-lg md:text-base group"><svg viewBox="0 0 256 256" fill="none" class="h-6 md:h-5 md:w-5 shrink-0"><path d="M71.3 106.2V83.52h73.08v132.3H191v22.68H65v-22.68h51.66V106.2H71.3z" fill="currentColor"></path><circle cx="126.74" cy="39.42" r="21.42" fill="currentColor"></circle></svg><div class="flex-1 min-w-0 truncate"><div class="flex items-center justify-between"><font dir="auto"><font dir="auto">${label}</font></font></div></div><svg viewBox="0 0 24 24" class="h-5 w-5 shrink-0"><path fill="currentColor" d="M8.59,16.58L13.17,12L8.59,7.41L10,6L16,12L10,18L8.59,16.58Z"></path></svg></div>`;

// ③ 面板里的连接按钮（满宽）
const connectButton = (label = "连接") =>
  `<button class="bg-blue-500 hover:bg-blue-600 active:bg-blue-700 block rounded-md px-3 py-1.5 text-center font-semibold md:text-sm overflow-hidden disabled:opacity-75 cursor-pointer disabled:cursor-not-allowed w-full"><font dir="auto"><font dir="auto">${label}</font></font></button>`;

const disconnect = (label = "Disconnect") =>
  `<button class="bg-red-500 block rounded-md px-3 py-1.5 w-full">${label}</button>`;

const panelShell = (inner) =>
  `<div class="rounded-md fixed bg-zinc-700 z-50"><div class="py-1 overflow-hidden max-h-screen overflow-y-auto rounded"><div><div><div class="flex flex-col items-center text-center text-white gap-y-3 mt-4 md:mt-0 px-3 py-2">${inner}</div></div></div></div></div>`;

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

/**
 * 搭一个"像真站点"的页面：
 *   点入口 → 插入面板（先只有 Intiface 列表项）
 *   点 Intiface 项 → 面板里出现连接按钮
 *   点连接 → 按钮文字变 Connected 并出现 Disconnect
 */
function setup({ alreadyConnected = false, omitEntry = false, intifaceLabel = "内脸", connectLabel = "连接", entryLabel = "连接", config = {} } = {}) {
  const body = omitEntry ? "" : entry(entryLabel);
  const dom = new JSDOM(`<!doctype html><html><body>${body}</body></html>`, {
    url: "https://faptap.net/", runScripts: "outside-only", pretendToBeVisual: true,
  });
  const { window } = dom;
  window.__FAPTAP_AUTOCONNECT__ = {
    dryRun: false, debug: false, pollIntervalMs: 5, stepCooldownMs: 10,
    verifyDelayMs: 20, findTimeoutMs: 400, maxRounds: 3,
    ...config,
  };

  const seq = [];
  const doc = window.document;

  if (alreadyConnected) {
    doc.body.insertAdjacentHTML("beforeend", panelShell(connectButton("Connected") + disconnect()));
  } else if (!omitEntry) {
    // 点入口 ⇒ 出现面板（先只有 Intiface 列表项）
    doc.querySelector("button.bg-blue-500:not(.w-full)").addEventListener("click", () => {
      if (doc.querySelector("div.cursor-pointer")) return;   // 避免重复插入
      doc.body.insertAdjacentHTML("beforeend", panelShell(intifaceItem(intifaceLabel)));
    });
  }

  doc.addEventListener("click", (e) => {
    const btn = e.target?.closest?.("button");
    const item = e.target?.closest?.("div.cursor-pointer");

    if (btn?.classList.contains("w-full") && btn.classList.contains("bg-blue-500")) {
      seq.push("connect");
      const inner = btn.querySelector("font font") || btn;
      inner.textContent = "Connected";
      if (!doc.querySelector("button.bg-red-500")) btn.insertAdjacentHTML("afterend", disconnect());
      return;
    }
    if (btn && btn.classList.contains("bg-blue-500")) { seq.push("entry"); return; }
    if (item) {
      seq.push("intiface");
      if (!doc.querySelector("button.bg-blue-500.w-full"))
        doc.querySelector("div.fixed")?.insertAdjacentHTML("beforeend", connectButton(connectLabel));
    }
  }, true);

  window.eval(SCRIPT);
  return { dom, window, seq };
}

const results = [];
async function test(name, fn, expect) {
  try {
    const got = await fn();
    const ok = expect(got);
    results.push({ name, ok, got: JSON.stringify(got) });
  } catch (e) {
    results.push({ name, ok: false, got: `异常: ${e.message}` });
  }
}

// ① 完整三步流程（文字全是翻译版：「连接」/「内脸」）⇒ 顺序必须是 entry → intiface → connect
await test("完整流程（全翻译文字）⇒ entry → intiface → connect",
  async () => { const t = setup(); await sleep(400); return t.seq; },
  (s) => s.join(",") === "entry,intiface,connect");

// ② 已连接 ⇒ 一次都不点
await test("已连接 ⇒ 不点",
  async () => { const t = setup({ alreadyConnected: true }); await sleep(300); return t.seq; },
  (s) => s.length === 0);

// ③ 找不到入口按钮 ⇒ 安全失败
await test("没有入口按钮 ⇒ 不点、不报错",
  async () => { const t = setup({ omitEntry: true }); await sleep(500); return t.seq; },
  (s) => s.length === 0);

// ④ 入口点了但列表项不出现（Intiface 项缺失）⇒ 入口最多点 2 次就收手，绝不去点连接
await test("列表项缺失 ⇒ 入口最多 2 次、不点连接",
  async () => {
    const dom = new JSDOM(`<!doctype html><html><body>${entry()}<div id="nowhere"></div></body></html>`, {
      url: "https://faptap.net/", runScripts: "outside-only", pretendToBeVisual: true,
    });
    const seq = [];
    dom.window.__FAPTAP_AUTOCONNECT__ = { dryRun: false, debug: false, pollIntervalMs: 5, stepCooldownMs: 10, findTimeoutMs: 200, maxRounds: 2 };
    dom.window.document.addEventListener("click", (e) => {
      const b = e.target?.closest?.("button");
      if (!b) return;
      seq.push(b.classList.contains("w-full") ? "connect" : "entry");
    }, true);
    dom.window.eval(SCRIPT);
    await sleep(400);
    return seq;
  },
  (s) => s.filter(x => x === "entry").length <= 2 && !s.includes("connect"));

// ⑤ 试运行 ⇒ 一次都不点
await test("DRY_RUN ⇒ 不点",
  async () => { const t = setup({ config: { dryRun: true } }); await sleep(300); return t.seq; },
  (s) => s.length === 0);

// ⑥ 面板里是别的弹窗（满宽按钮写着「保存」）⇒ 安全阀拦住
await test("满宽按钮文字是「保存」⇒ 不点",
  async () => {
    const dom = new JSDOM(`<!doctype html><html><body>${panelShell(connectButton("保存"))}</body></html>`, {
      url: "https://faptap.net/", runScripts: "outside-only", pretendToBeVisual: true,
    });
    const seq = [];
    dom.window.__FAPTAP_AUTOCONNECT__ = { dryRun: false, debug: false, pollIntervalMs: 5, findTimeoutMs: 200 };
    dom.window.document.addEventListener("click", () => seq.push("any"), true);
    dom.window.eval(SCRIPT);
    await sleep(300);
    return seq;
  },
  (s) => s.length === 0);

// ⑦ 英文原文（未翻译）同样要能跑通
await test("未翻译（Connect / Intiface）⇒ 三步照常",
  async () => { const t = setup({ entryLabel: "Connect", intifaceLabel: "Intiface", connectLabel: "Connect" }); await sleep(400); return t.seq; },
  (s) => s.join(",") === "entry,intiface,connect");

console.log("\n================ 测试结果 ================");
let pass = 0;
for (const r of results) {
  console.log(`${r.ok ? "✓ 通过" : "✗ 失败"}  ${r.name}\n         点击序列: ${r.got}`);
  if (r.ok) pass++;
}
console.log("-----------------------------------------");
console.log(`${pass}/${results.length} 通过`);
process.exit(pass === results.length ? 0 : 1);
