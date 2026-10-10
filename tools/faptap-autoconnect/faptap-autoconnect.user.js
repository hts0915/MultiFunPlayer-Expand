// ==UserScript==
// @name         faptap 自动连接设备（Intiface / MultiFunPlayer）
// @namespace    multifunplayer-expand
// @version      1.0.0
// @description  打开 faptap.net 后，若设备尚未连接则自动点击一次「连接」按钮；已连接则什么都不做。只在本站生效。
// @author       MultiFunPlayer-Expand
// @match        https://faptap.net/*
// @run-at       document-idle
// @grant        none
// @noframes
// ==/UserScript==

/*
 * 设计说明（为什么这么写）
 * ---------------------------------------------------------------------------
 * 1) 不按按钮文字匹配：
 *    用户开着网页翻译时，DOM 里的文字是「连接」而不是「Connect」，
 *    按文字匹配会失效。这里改为按结构匹配（设备面板里的蓝色主按钮）。
 * 2) 不靠"等固定秒数"判断网站是否加载完：
 *    改为轮询等待元素出现（默认最多等 60 秒），网络慢也不怕。
 * 3) 点完要复查：
 *    元素先渲染、事件后绑定的情况很常见，点一次可能白点。
 *    因此点完等 3 秒复查，仍未连接才再点，最多 3 次。
 * 4) 幂等 + 安全失败：
 *    已连接绝不点击；找不到按钮就安静放弃（只写控制台日志），绝不乱点。
 * 5) 只在 faptap.net 生效（@match），无网络请求、无权限（@grant none）。
 * ---------------------------------------------------------------------------
 * 第一次使用请保持 DRY_RUN = true（只打印"我将会点击…"，不真点），
 * 在 F12 控制台确认找对了元素之后，再改成 false。
 */

(() => {
    'use strict';

    // ======== 配置 ========
    const DEFAULTS = {
        dryRun: true,              // true = 只打印，不真点（第一次请保持 true）
        debug: true,               // 控制台日志
        pollIntervalMs: 500,       // 轮询间隔
        findTimeoutMs: 60000,      // 等按钮出现的上限（网络慢也没关系）
        verifyDelayMs: 3000,       // 点完多久复查
        maxClicks: 3,              // 每次页面加载最多点几次
        panelSelector: 'div.rounded-md.fixed.bg-zinc-700',   // 设备面板（浮动层）
        primarySelector: 'button.bg-blue-500',                // 面板里的主按钮
        connectRe: /^\s*(connect|连接|連接|接続|verbind\w*|подключ\w*)\s*$/i,
        connectedRe: /connected|已连接|已連接|接続済|verbunden|подключено/i,
        disconnectRe: /disconnect|断开|切断|接続解除|desconect|отключ\w*/i,
    };

    // 允许测试注入配置（浏览器里没人会设置这个）
    const CFG = Object.assign({}, DEFAULTS, (typeof globalThis !== 'undefined' && globalThis.__FAPTAP_AUTOCONNECT__) || {});

    const TAG = '[faptap自动连接]';
    const log = (...args) => { if (CFG.debug) console.log(TAG, ...args); };
    const warn = (...args) => console.warn(TAG, ...args);

    let clicks = 0;
    let stopped = false;
    let lastClickAt = 0;

    // ======== 元素查找 ========
    const findPanel = () => document.querySelector(CFG.panelSelector);

    function findPrimaryButton() {
        const panel = findPanel();
        if (!panel) return null;

        // 面板里的蓝色主按钮；找不到就退化为"面板里唯一的满宽按钮"
        const byClass = panel.querySelector(CFG.primarySelector);
        if (byClass) return byClass;

        return [...panel.querySelectorAll('button')].find(b => b.classList.contains('w-full')) || null;
    }

    /** 面板里是否已经出现"断开"类按钮（= 已连接） */
    function hasDisconnectButton() {
        const panel = findPanel();
        if (!panel) return false;
        return [...panel.querySelectorAll('button')]
            .some(b => CFG.disconnectRe.test(b.textContent || ''));
    }

    /**
     * 是否已连接。
     * 注意 connectRe 必须"整串匹配"：'Connected' 里也含 'connect'，
     * 用 /connect/i 会把它误判成"未连接"从而反复点击（这是测试抓出来的真 bug）。
     */
    function isConnected() {
        if (hasDisconnectButton()) return true;

        const primary = findPrimaryButton();
        if (!primary) return false;

        const text = (primary.textContent || '').trim();
        if (!text) return false;

        return CFG.connectedRe.test(text) || !CFG.connectRe.test(text);
    }

    // ======== 主流程 ========
    function attempt() {
        if (stopped) return;

        if (isConnected()) {
            log('检测到设备已连接，脚本不做任何事 ✓');
            stopped = true;
            return;
        }

        const button = findPrimaryButton();
        if (!button) return;   // 还没渲染出来，交给轮询继续等

        // 安全阀：只有主按钮文字确实是"连接"类才点。
        // 面板选择器可能被站点的其它弹窗复用，靠这一条避免点错；
        // 若翻译成了我没有覆盖的语言 ⇒ 安全失败（不点），只提示你把这个文字发我。
        const label = (button.textContent || '').trim();
        if (!CFG.connectRe.test(label)) {
            warn(`主按钮文字「${label || "(空)"}」不像"连接"，为安全起见不点 ✗。若确认它就是连接按钮，请把这段文字发我，我加进识别列表。`);
            stopped = true;
            return;
        }

        if (clicks >= CFG.maxClicks) {
            warn(`已尝试 ${clicks} 次仍未连接，放弃（不打扰你）✗。若按钮找错了，请把控制台这段发我。`);
            stopped = true;
            return;
        }

        const now = Date.now();
        if (now - lastClickAt < CFG.pollIntervalMs) return;

        clicks++;
        lastClickAt = now;

        if (CFG.dryRun) {
            log(`[试运行] 我将会点击这个按钮：「${label}」`, button);
        } else {
            log(`点击连接按钮（第 ${clicks} 次）：「${label}」`);
            try {
                button.click();
            } catch (e) {
                warn('点击失败：', e);
                stopped = true;
                return;
            }
        }

        setTimeout(attempt, CFG.verifyDelayMs);   // 复查：可能"点了没反应"
    }

    // 等待面板出现（轮询，不用固定等待）
    const startedAt = Date.now();
    const waitTimer = setInterval(() => {
        if (stopped) { clearInterval(waitTimer); return; }

        if (findPanel()) {
            clearInterval(waitTimer);
            log('设备面板已出现，开始检查连接状态');
            attempt();
            return;
        }

        if (Date.now() - startedAt > CFG.findTimeoutMs) {
            clearInterval(waitTimer);
            stopped = true;
            warn(`等了 ${Math.round(CFG.findTimeoutMs / 1000)} 秒仍未找到设备面板（选择器：${CFG.panelSelector}）⇒ 安静地不做任何事 ✗`);
        }
    }, CFG.pollIntervalMs);

    // SPA 内部跳转：地址变了就再看一次（已连接时不会重复点）
    let lastHref = location.href;
    const onNav = () => {
        if (location.href === lastHref) return;
        lastHref = location.href;
        if (stopped) return;
        log('地址变化，重新检查一次');
        attempt();
    };
    for (const fn of ['pushState', 'replaceState']) {
        const original = history[fn];
        if (typeof original === 'function') {
            history[fn] = function (...args) { const r = original.apply(this, args); onNav(); return r; };
        }
    }
    addEventListener('popstate', onNav);

    log('脚本已加载（' + (CFG.dryRun ? '试运行模式：只打印不点击' : '正式模式') + '）');
})();
