// ==UserScript==
// @name         faptap 自动连接设备（Intiface / MultiFunPlayer）
// @namespace    multifunplayer-expand
// @version      2.5.0
// @description  打开 faptap.net 后自动完成「打开设备面板 → 选 Intiface → 点连接」三步；已连接则什么都不做。只在本站生效。
// @author       MultiFunPlayer-Expand
// @match        https://faptap.net/*
// @match        https://*.faptap.net/*
// @run-at       document-idle
// @grant        none
// @noframes
// ==/UserScript==

/*
 * 真实操作流程（用户实测，必须按顺序三步）：
 *   ① 点页面上那个带图标的蓝色「连接」按钮   → 才会出现设备面板（浮动弹窗）
 *   ② 在设备列表里点「Intiface」那一项        → 面板里才会出现连接按钮
 *   ③ 点面板里的蓝色「连接」按钮              → 真正连接设备
 *
 * 关键设计（都是踩过的坑）：
 * - 不按文字匹配：用户开着网页翻译，DOM 里是「连接」「内脸」等中文，
 *   官网原文是 Connect / Intiface。这里改为按【结构 + Intiface 的 logo 路径】匹配。
 * - 面板是"要用时才挂进 DOM"的浮动弹窗 ⇒ 用 MutationObserver + 轮询等待，随时出现随时继续。
 * - 每一步都幂等 + 冷却，避免重复点击；已连接绝不点击。
 * - 安全失败：找不到入口/选项就安静放弃，只写控制台日志，绝不乱点。
 *
 * 第一次使用可直接用（默认 dryRun: false 会自动完成任务）；
 * 若想先观察，把 dryRun 改成 true 就只打印每一步"我将会点击…"而不真点。
 */

(() => {
    'use strict';

    // ======== 配置 ========
    const DEFAULTS = {
        dryRun: false,              // false = 真正自动连接；想排查时改成 true 只打印不点击
        debug: true,                // 控制台日志
        pollIntervalMs: 400,        // 检查间隔
        stepCooldownMs: 1200,       // 同一步骤的最小间隔（防重复点击）
        verifyDelayMs: 2500,        // 点完连接后多久复查
        findTimeoutMs: 90000,       // 整体等待上限（网络慢也不怕）
        maxRounds: 3,               // 整流程最多尝试几轮
        maxPageLoads: 3,            // 同一标签页最多自动运行几次（防止点击导致页面重载死循环）
        reloadSessionWindowMs: 60000,   // 距上次运行超过这么久，视为新的浏览会话，计数重新开始
        closePanelAfterConnect: true,   // 连接成功后顺手把设备面板收起来（无感连接）
        hidePanelDuringConnect: true,   // 连接过程中把面板隐形（opacity:0，不改布局、不影响点击）
        tryEscFirst: false,             // 实测该站点不响应 Esc ⇒ 默认跳过，省掉一次等待
        closeVerifyMs: 300,             // 收起面板每一步的确认等待（同步收起，300ms 足够）

        // ① 打开面板的入口按钮：蓝色主色、带 svg 图标、且不是满宽（满宽那个是面板里的连接按钮）
        entryButtonSelector: 'button.bg-blue-500:not(.w-full)',
        // ② Intiface 列表项：靠它独一无二的 logo 路径识别（文字会被翻译，不可靠）
        intifaceLogoSvgSelector: 'svg path[d^="M71.3 106.2"]',
        // ③ 面板里的连接按钮
        connectButtonSelector: 'button.bg-blue-500.w-full',
        // 面板容器本身（用户 Copy selector 的第一段）—— 用来判断"面板是否还开着"
        panelSelector: 'div.rounded-md.fixed.bg-zinc-700',

        connectRe: /^\s*(connect|连接|連接|接続|verbind\w*|подключ\w*)\s*$/i,
        connectedRe: /connected|已连接|已連接|接続済|verbunden|подключено/i,
        disconnectRe: /disconnect|断开|切断|接続解除|desconect|отключ\w*/i,
    };

    const CFG = Object.assign({}, DEFAULTS, (typeof globalThis !== 'undefined' && globalThis.__FAPTAP_AUTOCONNECT__) || {});

    const TAG = '[faptap自动连接]';
    const log = (...a) => { if (CFG.debug) console.log(TAG, ...a); };
    const warn = (...a) => console.warn(TAG, ...a);

    const state = {
        rounds: 0,
        lastStepAt: { entry: 0, intiface: 0, connect: 0 },
        clicks: { entry: 0, intiface: 0, connect: 0 },   // 每步点击上限，防止"反复开关面板"
        connectedOnce: false,       // 本次页面里是否成功连上过（用于识别"关了面板就掉线"）
        closeTried: 0,
        closing: false,             // 收起面板流程进行中（期间 tick 必须让路）
        done: false,
        stopped: false,
    };

    const STEP_LIMITS = { entry: 2, intiface: 3, connect: 3 };

    // ======== 元素查找 ========
    const text = (el) => (el?.textContent || '').trim();

    function findEntryButton() {
        const buttons = [...document.querySelectorAll(CFG.entryButtonSelector)];

        // ⚠️ 必须严格：只认"文字像连接"的按钮。
        // 之前有一条"随便找个带 svg 的蓝色按钮"的兜底，结果点到了站点别的按钮
        // （例如"刷新"），导致页面反复重载、脚本反复重启 ⇒ 死循环。这里去掉兜底。
        const looksLikeConnect = (b) => CFG.connectRe.test(text(b))
            || (b.querySelector('span') && CFG.connectRe.test(b.querySelector('span').textContent || ''));

        return buttons.find(b => b.querySelector('svg') && looksLikeConnect(b))
            || buttons.find(looksLikeConnect)
            || null;
    }

    function findIntifaceItem() {
        const logo = document.querySelector(CFG.intifaceLogoSvgSelector);
        if (!logo) return null;
        return logo.closest('div.cursor-pointer') || logo.closest('button, li, [role="button"]') || logo.parentElement;
    }

    function findConnectButton() {
        const btn = document.querySelector(CFG.connectButtonSelector);
        return btn || null;
    }

    /** 面板是否还开着。注意不能用"连接按钮"判断：连上之后它会变成 Disconnect */
    function isPanelOpen() {
        return !!document.querySelector(CFG.panelSelector);
    }

    function hasDisconnectButton() {
        return [...document.querySelectorAll('button')].some(b => CFG.disconnectRe.test(text(b)));
    }

    /** 已连接判定：① 出现"断开"按钮；② 面板连接按钮文字已不是"连接" */
    function isConnected() {
        if (hasDisconnectButton()) return true;
        const btn = findConnectButton();
        if (!btn) return false;
        const t = text(btn);
        if (!t) return false;
        return CFG.connectedRe.test(t) || !CFG.connectRe.test(t);
    }

    // ======== 执行 ========
    function coolDownPassed(step) {
        const now = Date.now();
        if (now - state.lastStepAt[step] < CFG.stepCooldownMs) return false;
        state.lastStepAt[step] = now;
        return true;
    }

    function act(step, element, description) {
        if (!coolDownPassed(step)) return false;

        if (state.clicks[step] >= STEP_LIMITS[step]) {
            if (!state.limitWarned?.[step]) {
                state.limitWarned = state.limitWarned || {};
                state.limitWarned[step] = true;
                warn(`「${description}」已点击 ${state.clicks[step]} 次仍无进展 ⇒ 停止这一步（避免反复开关面板）✗`);
            }
            return false;
        }

        const label = text(element) || '(无文字)';

        if (CFG.dryRun) {
            log(`[试运行] ${description}：「${label}」`, element);
            return false;   // 试运行不推进状态，方便反复观察
        }

        state.clicks[step]++;
        log(`${description}：「${label}」（第 ${state.clicks[step]} 次）`);
        try {
            element.click();
        } catch (e) {
            warn(`点击 ${description} 失败：`, e);
            state.stopped = true;
        }
        return true;
    }

    let verifyTimer = null;

    /** 让设备面板"隐形"（opacity:0 只影响视觉：不改变布局，也不影响 .click() 点它）。
     *  在点入口之前就注入 ⇒ 面板从出现那一刻起就是隐形的，连闪一下都没有。 */
    const HIDE_STYLE_ID = '__faptap_hide_panel';
    function hidePanelStyle(on) {
        if (!CFG.hidePanelDuringConnect) return;
        try {
            const existing = document.getElementById(HIDE_STYLE_ID);
            if (on) {
                if (existing) return;
                const style = document.createElement('style');
                style.id = HIDE_STYLE_ID;
                style.textContent = `${CFG.panelSelector}{opacity:0 !important;}`;
                (document.head || document.documentElement).appendChild(style);
            } else if (existing) {
                existing.remove();
            }
        } catch { /* 忽略 */ }
    }

    /** 选一个"面板矩形之外"的坐标（优先面板右侧 —— 和用户平时的操作习惯一致）。
     *  只用来填事件的 clientX/clientY，事件本身仍派发在 body 上，因此不会误触任何控件。 */
    function pickOutsidePoint() {
        const w = window.innerWidth || document.documentElement.clientWidth || 1000;
        const h = window.innerHeight || document.documentElement.clientHeight || 800;
        const panel = document.querySelector(CFG.panelSelector);

        if (!panel) return { x: Math.round(w - 20), y: Math.round(h / 2) };

        try {
            const r = panel.getBoundingClientRect();
            if (r.right + 40 < w) return { x: Math.round((r.right + w) / 2), y: Math.round(h / 2) };  // 面板右侧
            if (r.left > 40) return { x: Math.round(r.left / 2), y: Math.round(h / 2) };              // 面板左侧
        } catch { /* 忽略 */ }

        return { x: Math.round(w - 20), y: Math.round(h - 20) };   // 兜底：右下角
    }

    /** 连接成功后把设备面板收起来。
     *  ① Esc（无害）
     *  ② 在 <body> 上派发一整套指针事件（pointerdown → mousedown → pointerup → mouseup → click），
     *     坐标取在"面板矩形之外、优先右侧"，等价于"点了面板右边外面的空白处"：
     *     · 事件目标是 body 本身，不是任何按钮 ⇒ 绝无误触别的控件的可能
     *     · 带坐标 + 成对 down/up ⇒ 兼容"按坐标判断"或"要求按下抬起成对"的实现
     *  ③ 仍不行就换成在 <html> 上派发
     *  全程最多 3 步，之后收手。
     */
    function tryClosePanel() {
        const maxAttempts = CFG.tryEscFirst ? 3 : 2;   // 跳过 Esc 时只剩 body / html 两种手段
        if (state.closeTried >= maxAttempts) {
            log('收起面板的尝试已用完，脚本收手（面板还开着也不影响使用）');
            state.closing = false;
            state.done = true;
            cleanup();
            return;
        }

        state.closeTried++;
        // tryEscFirst=false 时，第 1 次尝试直接用"点面板外面"（该站点实测要这样才能收起）
        const step = CFG.tryEscFirst ? state.closeTried : state.closeTried + 1;
        state.closing = true;

        if (step === 1) {
            log('收起设备面板（第 1 步）：按 Esc');
            for (const target of [document, window]) {
                try {
                    target.dispatchEvent(new KeyboardEvent('keydown', {
                        key: 'Escape', code: 'Escape', keyCode: 27, which: 27, bubbles: true,
                    }));
                } catch { /* 忽略 */ }
            }
        } else {
            const host = step === 2 ? document.body : document.documentElement;
            const point = pickOutsidePoint();
            log(`收起设备面板（第 ${step} 步）：在 <${host.tagName.toLowerCase()}> 上派发"点空白处"事件（坐标 ${point.x},${point.y}）`);

            for (const type of ['pointerdown', 'mousedown', 'pointerup', 'mouseup', 'click']) {
                try {
                    const isPointer = type.startsWith('pointer');
                    const EventCtor = (isPointer && window.PointerEvent) || window.MouseEvent || MouseEvent;
                    host.dispatchEvent(new EventCtor(type, {
                        bubbles: true, cancelable: true, composed: true,
                        clientX: point.x, clientY: point.y,
                        screenX: point.x, screenY: point.y,
                        button: 0, buttons: type.endsWith('up') || type === 'click' ? 0 : 1,
                    }));
                } catch { /* 忽略 */ }
            }
        }

        setTimeout(() => {
            if (state.done || state.stopped) return;

            if (!isPanelOpen()) {
                log(`✓ 面板已收起（第 ${step} 步生效）`);
                state.closing = false;
                state.done = true;
                cleanup();
                return;
            }

            tryClosePanel();   // 还有下一步可用
        }, CFG.closeVerifyMs);
    }

    function tick() {
        if (state.done || state.stopped) return;

        // 达成目标
        if (isConnected()) {
            // ⚠️ 收起面板流程进行中时，这里必须让路：
            // 否则下一轮 tick 会立刻判定"已完成"并 cleanup，把收起的后续步骤全部掐断
            if (state.closing) return;

            if (!state.connectedOnce) {
                state.connectedOnce = true;
                log('✓ 设备已连接');
                try { sessionStorage.removeItem(LOAD_KEY); } catch { /* 忽略 */ }   // 清掉重载计数

                if (CFG.closePanelAfterConnect) {
                    tryClosePanel();   // 收起面板后再收手
                    return;
                }
            }

            log('✓ 设备已连接，脚本收手（不再点击）');
            state.done = true;
            cleanup();
            return;
        }

        // 曾经连上过、现在又检测到断开 ⇒ 很可能是"收起面板把连接也关掉了"
        // ⇒ 立刻停手，绝不再连（否则会陷入 连上→收起→断开→再连 的死循环）
        if (state.connectedOnce) {
            warn('连接曾成功，但现在检测到已断开 ⇒ 停止（可能是"收起面板"顺带断开了连接）✗');
            warn('请把这段日志发我；也可以把脚本里的 closePanelAfterConnect 改成 false，脚本就只管连接、不动面板。');
            state.stopped = true;
            cleanup();
            return;
        }

        // 超时保护
        if (Date.now() - startedAt > CFG.findTimeoutMs) {
            warn(`等了 ${Math.round(CFG.findTimeoutMs / 1000)} 秒仍未连接成功 ⇒ 放弃（不打扰你）✗`);
            warn(`诊断：入口按钮=${findEntryButton() ? '已找到' : '未找到'}、Intiface 项=${findIntifaceItem() ? '已找到' : '未找到'}、面板连接按钮=${findConnectButton() ? '已找到' : '未找到'}`);
            state.stopped = true;
            cleanup();
            return;
        }

        // ① 还没有面板连接按钮：
        //    先看设备列表项是否已出现（说明面板已打开）⇒ 选 Intiface；
        //    都没有才去点入口（入口按钮一直在页面上，不能无条件先点它，否则会反复开关面板）
        if (!findConnectButton()) {
            const item = findIntifaceItem();
            if (item) { act('intiface', item, '选择 Intiface'); return; }

            const entry = findEntryButton();
            if (entry) {
                hidePanelStyle(true);   // 先让面板隐形，再点入口 ⇒ 面板从出现起就看不见（无感）
                act('entry', entry, '打开设备面板');
                return;
            }

            return;   // 都在等（面板未渲染），交给观察器
        }

        // ② 面板里已有连接按钮但还没连上 ⇒ 点它（若列表项还在，先选它更保险）
        const item = findIntifaceItem();
        const connect = findConnectButton();

        if (connect && CFG.connectRe.test(text(connect))) {
            const clicked = act('connect', connect, '点击连接');
            if (clicked) {
                if (verifyTimer) clearTimeout(verifyTimer);
                verifyTimer = setTimeout(() => {
                    if (state.done || state.stopped) return;
                    if (isConnected()) return;      // 下一轮 tick 会判定完成
                    state.rounds++;
                    if (state.rounds >= CFG.maxRounds) {
                        warn(`已尝试 ${state.rounds} 轮仍未连接成功 ⇒ 放弃 ✗（若按钮找错了，请把控制台这几行发我）`);
                        state.stopped = true;
                        cleanup();
                        return;
                    }
                    log(`未连上，准备第 ${state.rounds + 1} 轮…`);
                    tick();
                }, CFG.verifyDelayMs);
            }
            return;
        }

        // ③ 连接按钮文字不是"连接"（可能已连接、或是别的弹窗）⇒ 交给 isConnected 判定，这里不动手
        if (item && !connect) act('intiface', item, '选择 Intiface');
    }

    function cleanup() {
        clearInterval(timer);
        observer.disconnect();
        if (verifyTimer) clearTimeout(verifyTimer);
        hidePanelStyle(false);   // 收工后一定把"隐形"样式摘掉，免得你手动开面板时看不见
    }

    // ======== 防"重载死循环" ========
    // 万一某次点击导致页面重载，脚本会重新启动、计数清零，就会无限重载。
    // 这里把"页面加载次数"记在 sessionStorage 里，超过上限就彻底停手。
    const LOAD_KEY = '__faptap_autoconnect_loads';
    function countLoadAndCheck() {
        try {
            const now = Date.now();
            let count = 0;
            const raw = sessionStorage.getItem(LOAD_KEY);
            if (raw) {
                try {
                    const o = JSON.parse(raw);
                    count = o.count || 0;
                    // 距上次运行很久（默认 60 秒）⇒ 当作新的浏览会话，重新计数
                    if (!o.ts || now - o.ts > CFG.reloadSessionWindowMs) count = 0;
                } catch { count = parseInt(raw, 10) || 0; }
            }

            count++;
            sessionStorage.setItem(LOAD_KEY, JSON.stringify({ count, ts: now }));
            return count;
        } catch {
            return 1;   // 读不到存储就当第一次，不做限制
        }
    }
    const loadCount = countLoadAndCheck();

    // ======== 启动 ========
    const startedAt = Date.now();

    if (loadCount > CFG.maxPageLoads) {
        warn(`本标签页里脚本已启动 ${loadCount - 1} 次（超过上限 ${CFG.maxPageLoads}）⇒ 怀疑点击导致页面反复重载，已彻底停手 ✗`);
        warn('请把控制台里前面的 [faptap自动连接] 日志发我，我据此修正按钮识别。');
        return;   // 直接结束，不注册任何观察器/定时器
    }

    log(`脚本已加载 v2.5.0（${CFG.dryRun ? '试运行模式：只打印不点击' : '正式模式'}，本次页面第 ${loadCount} 次运行）`);

    const observer = new MutationObserver(() => tick());
    observer.observe(document.documentElement, { childList: true, subtree: true });

    const timer = setInterval(tick, CFG.pollIntervalMs);
    tick();

    // SPA 内部跳转：地址变了再检查一次（已连接则不会重复点）
    let lastHref = location.href;
    const onNav = () => {
        if (location.href === lastHref) return;
        lastHref = location.href;
        if (state.done || state.stopped) return;
        log('地址变化，重新检查');
        tick();
    };
    for (const fn of ['pushState', 'replaceState']) {
        const original = history[fn];
        if (typeof original === 'function') {
            history[fn] = function (...args) { const r = original.apply(this, args); onNav(); return r; };
        }
    }
    addEventListener('popstate', onNav);
})();
