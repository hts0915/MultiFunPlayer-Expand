# ayva-to-funscript —— 把 Ayva Stroker Lite 的脚本转成 MultiFunPlayer 能用的 funscript

[Ayva Stroker Lite](https://www.ayva-stroker-lite.io/) 里的「脚本」其实是两种东西，**都不是 funscript**：

| 类型 | 是什么 |
|---|---|
| `tempest-stroke` | 一套行程参数（每个轴 `from / to / phase / ecc` 等），可无限循环的运动描述 |
| `ayvascript` | 一段 JavaScript 行为代码 |

本工具把 **`tempest-stroke`** 按真实时间跑一遍（用官方 [ayvajs](https://github.com/ayvasoftware/ayvajs) 引擎，保证动作一致），
采样各轴位置并生成 `.funscript`。`ayvascript` 不支持。

## 一、先从网站导出脚本

1. **站内导出**（推荐）：在站点里导出，会得到 **`ayva-behaviors.json`**（JSON 数组，每项 `{name, type, data}`）
2. **手动取**：浏览器 F12 → **Application / 应用** → **Local Storage** → `https://www.ayva-stroker-lite.io`
   → 找 `custom-behaviors--all`（以及旧的 `custom-tempest-strokes--all`）→ 复制它的 JSON 值
   （本工具两种格式都认：数组、或 `{"名字": {"type": ..., "data": ...}}` 对象）

## 二、转换

```bash
# 先看看文件里有哪些脚本
node convert.js --input ayva-behaviors.json --list

# 全部转换成 funscript（默认每个脚本生成 120 秒）
node convert.js --input ayva-behaviors.json --duration 120 --bpm 120 --out out

# 只转其中一个
node convert.js --input ayva-behaviors.json --only "down-forward" --duration 60

# 只输出主轴和旋转轴
node convert.js --input ayva-behaviors.json --axes L0,R0
```

| 参数 | 说明 |
|---|---|
| `-i, --input` | `ayva-behaviors.json` 或从 localStorage 复制的 JSON |
| `-o, --out` | 输出目录（默认当前目录） |
| `-d, --duration` | 生成多长（秒，默认 **120**）—— tempest-stroke 本身是无限循环的，必须给个长度 |
| `-b, --bpm` | 节拍（默认 **120**，与你在站点里设置的 bpm 对应） |
| `--sample` | 采样间隔毫秒（默认 **20** = 50Hz，和 ayvajs 内部频率一致） |
| `--only` | 只转换某个脚本 |
| `--axes` | 只输出这些轴，如 `L0,R0` |
| `--list` | 只列出脚本，不转换 |
| `--keep-collinear` | 保留共线的中间点（默认会去掉，文件更小） |

> 转换按**真实时间**跑，所以 `--duration 120` 大约需要 120 秒。Ctrl+C 可随时中断。

## 三、输出

轴名与 MultiFunPlayer 完全一致（`L0`=stroke / `L1`=surge / `L2`=sway / `R0`=twist / `R1`=roll / `R2`=pitch）：

```
<名字>.funscript        主轴 L0（stroke）
<名字>.L1.funscript     surge
<名字>.R0.funscript     twist
...
```

全程不动的轴不会生成文件。

## 四、在 MultiFunPlayer 里用

两种方式：

1. **当预设用（不需要视频）**：把 `.funscript` 放进一个文件夹，在 MFP 的「脚本预设」面板点
   「选择脚本文件…」选中它们（文件名决定轴：`xxx.funscript` → 默认轴、`xxx.L1.funscript` → L1）→ 播放
2. **配视频用**：把文件改成和视频同名放在一起 —— `video.mp4` + `video.funscript` / `video.L1.funscript`，
   MFP 会自动加载

## 依赖

```bash
pnpm add ayvajs     # 或 npm i ayvajs
```

需要 Node.js 18+（用到了 `performance.now()` 与顶层 `import`）。
