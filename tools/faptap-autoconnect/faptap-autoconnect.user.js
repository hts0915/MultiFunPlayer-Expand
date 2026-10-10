// ==UserScript==
// @name         faptap 自动连接设备（Intiface / MultiFunPlayer）
// @namespace    multifunplayer-expand
// @version      2.0.0
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

        // ① 打开面板的入口按钮：蓝色主色、带 svg 图标、且不是满宽（满宽那个是面板里的连接按钮）
        entryButtonSelector: 'button.bg-blue-500:not(.w-full)',
        // ② Intiface 列表项：靠它独一无二的 logo 路径识别（文字会被翻译，不可靠）
        intifaceLogoSvgSelector: 'svg path[d^="M71.3 106.2"]',
        // ③ 面板里的连接按钮
        connectButtonSelector: 'button.bg-blue-500.w-full',

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
        done: false,
        stopped: false,
    };

    const STEP_LIMITS = { entry: 2, intiface: 3, connect: 3 };

    // ======== 元素查找 ========
    const text = (el) => (el?.textContent || '').trim();

    function findEntryButton() {
        const buttons = [...document.querySelectorAll(CFG.entryButtonSelector)];
        // 优先"带图标且文字像连接"的；退化为第一个带图标的
        return buttons.find(b => b.querySelector('svg') && (CFG.connectRe.test(text(b)) || !text(b)))
            || buttons.find(b => b.querySelector('svg'))
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

    function tick() {
        if (state.done || state.stopped) return;

        // 达成目标
        if (isConnected()) {
            log('✓ 设备已连接，脚本收手（不再点击）');
            state.done = true;
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
            if (entry) { act('entry', entry, '打开设备面板'); return; }

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
    }

    // ======== 启动 ========
    const startedAt = Date.now();
    log(`脚本已加载 v2.0.0（${CFG.dryRun ? '试运行模式：只打印不点击' : '正式模式'}）`);

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
