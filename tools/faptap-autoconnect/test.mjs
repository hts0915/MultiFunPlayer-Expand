// faptap 自动连接脚本的单元测试（jsdom）
// 运行： node test.mjs
import { JSDOM } from "jsdom";
import { readFileSync } from "node:fs";

const SCRIPT = readFileSync(new URL("./faptap-autoconnect.user.js", import.meta.url), "utf8");

// 用户提供的真实结构（面板 = 浮动层 div.rounded-md.fixed.bg-zinc-700）
const panel = (buttons) => `<div class="rounded-md fixed bg-zinc-700 z-50">
  <div class="py-1 overflow-hidden max-h-screen overflow-y-auto rounded"><div><div>
    <div class="flex flex-col items-center text-center text-white gap-y-3 mt-4 md:mt-0 px-3 py-2">${buttons}</div>
  </div></div></div>
</div>`;

// 主按钮：用户给的真实 class；文字可能被网页翻译改掉
const primary = (label) =>
  `<button class="bg-blue-500 hover:bg-blue-600 active:bg-blue-700 block rounded-md px-3 py-1.5 text-center font-semibold md:text-sm overflow-hidden disabled:opacity-75 cursor-pointer disabled:cursor-not-allowed w-full"><font dir="auto"><font dir="auto">${label}</font></font></button>`;

const disconnect = (label = "Disconnect") =>
  `<button class="bg-red-500 block rounded-md px-3 py-1.5 w-full">${label}</button>`;

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function setup({ html, delayPanelMs = 0, config = {}, simulateSuccess = false }) {
  const body = delayPanelMs ? "" : html;
  const dom = new JSDOM(`<!doctype html><html><body>${body}</body></html>`, {
    url: "https://faptap.net/",
    runScripts: "outside-only",
    pretendToBeVisual: true,
  });
  const { window } = dom;
  window.__FAPTAP_AUTOCONNECT__ = {
    dryRun: false, debug: false,
    pollIntervalMs: 5, findTimeoutMs: 200, verifyDelayMs: 20, maxClicks: 3,
    ...config,
  };

  const stats = { total: 0, primary: 0, decoy: 0 };

  // 像真站点一样：点了"连接"之后，主按钮变 Connected 并多出 Disconnect
  function wireSuccess() {
    const btn = window.document.querySelector("button.bg-blue-500");
    if (!btn) return;
    btn.addEventListener("click", () => {
      const inner = btn.querySelector("font font") || btn;
      inner.textContent = "Connected";
      if (!window.document.querySelector("button.bg-red-500"))
        btn.insertAdjacentHTML("afterend", disconnect());
    });
  }

  if (delayPanelMs) {
    setTimeout(() => {
      window.document.body.insertAdjacentHTML("beforeend", html);
      if (simulateSuccess) wireSuccess();
    }, delayPanelMs);
  } else if (simulateSuccess) {
    wireSuccess();
  }

  // 统计点击：区分"面板内主按钮"和"面板外的诱饵"
  window.document.addEventListener("click", (e) => {
    const btn = e.target?.closest?.("button");
    if (!btn) return;
    stats.total++;
    if (btn.classList.contains("bg-blue-500")) stats.primary++;
    else stats.decoy++;
  }, true);

  window.eval(SCRIPT);
  return { dom, window, stats };
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

// ① 未连接 + 文字被翻译成「连接」；点完会成功 ⇒ 恰好点一次
await test("未连接 + 翻译成「连接」⇒ 点一次",
  async () => { const t = setup({ html: panel(primary("连接")), simulateSuccess: true }); await sleep(250); return t.stats; },
  (s) => s.primary === 1 && s.decoy === 0);

// ② 未连接 + 日文 ⇒ 同样能点一次
await test("未连接 + 日文「接続」⇒ 点一次",
  async () => { const t = setup({ html: panel(primary("接続")), simulateSuccess: true }); await sleep(250); return t.stats; },
  (s) => s.primary === 1);

// ③ 已连接（出现 Disconnect）⇒ 一次都不点
await test("已连接（有 Disconnect）⇒ 不点",
  async () => { const t = setup({ html: panel(primary("Connected") + disconnect()) }); await sleep(250); return t.stats; },
  (s) => s.total === 0);

// ④ 已连接（只有主按钮显示 Connected）⇒ 不点（这是刚才抓到的真 bug）
await test("已连接（主按钮 Connected）⇒ 不点",
  async () => { const t = setup({ html: panel(primary("Connected")) }); await sleep(250); return t.stats; },
  (s) => s.total === 0);

// ⑤ 网络慢：面板 100ms 后才出现 ⇒ 等到后点一次
await test("面板延迟 100ms 出现 ⇒ 等到后点一次",
  async () => { const t = setup({ html: panel(primary("Connect")), delayPanelMs: 100, simulateSuccess: true }); await sleep(500); return t.stats; },
  (s) => s.primary === 1);

// ⑥ 面板始终不出现 ⇒ 不点、不报错
await test("面板始终不存在 ⇒ 不点、不报错",
  async () => { const t = setup({ html: "<div>nothing here</div>" }); await sleep(350); return t.stats; },
  (s) => s.total === 0);

// ⑦ 点了没反应（站点真的连不上）⇒ 最多点 3 次后收手
await test("点击无效 ⇒ 最多点 3 次后放弃",
  async () => { const t = setup({ html: panel(primary("Connect")), config: { maxClicks: 3, verifyDelayMs: 30 } }); await sleep(450); return t.stats; },
  (s) => s.primary === 3);

// ⑧ 面板外有诱饵 Connect 按钮 ⇒ 绝不点它
await test("面板外诱饵按钮 ⇒ 只点面板内（诱饵 0 次）",
  async () => {
    const t = setup({ html: `<button class="bg-blue-500 w-full" id="decoy">Connect</button>` + panel(primary("Connect")), simulateSuccess: true });
    await sleep(250);
    return t.stats;
  },
  (s) => s.decoy === 0);

// ⑨ SPA 跳转两次（已连接）⇒ 不点
await test("SPA 跳转两次（已连接）⇒ 不点",
  async () => {
    const t = setup({ html: panel(primary("Connected") + disconnect()) });
    await sleep(80);
    t.window.history.pushState({}, "", "/a"); await sleep(80);
    t.window.history.pushState({}, "", "/b"); await sleep(150);
    return t.stats;
  },
  (s) => s.total === 0);

// ⑩ 试运行模式 ⇒ 一次都不点
await test("DRY_RUN 模式 ⇒ 一次都不点",
  async () => { const t = setup({ html: panel(primary("Connect")), config: { dryRun: true } }); await sleep(250); return t.stats; },
  (s) => s.total === 0);

// ⑪ 网站改版：面板选择器失效 ⇒ 安全失败（不点、不报错）
await test("面板结构变了（找不到面板）⇒ 安全失败，不点",
  async () => {
    const t = setup({ html: `<div class="totally-different"><button class="bg-blue-500 w-full">Connect</button></div>` });
    await sleep(350);
    return t.stats;
  },
  (s) => s.total === 0);

// ⑫ 面板被别的弹窗复用（主按钮写着"保存"）⇒ 安全阀必须拦住，绝不点
await test("面板里是别的弹窗（文字「保存」）⇒ 不点",
  async () => { const t = setup({ html: panel(primary("保存")) }); await sleep(300); return t.stats; },
  (s) => s.total === 0);

console.log("\n================ 测试结果 ================");
let pass = 0;
for (const r of results) {
  console.log(`${r.ok ? "✓ 通过" : "✗ 失败"}  ${r.name}\n         实际: ${r.got}`);
  if (r.ok) pass++;
}
console.log("-----------------------------------------");
console.log(`${pass}/${results.length} 通过`);
process.exit(pass === results.length ? 0 : 1);
