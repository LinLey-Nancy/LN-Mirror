<div align="center">

<img src="docs/images/main-window.png" width="720" alt="LN-Mirror 主界面" />

# LN-Mirror

**把安卓手机变成 Windows 桌面上的一块“玻璃”：半透明悬浮、鼠标直接操作、悬停显形、随手隐去。**

<a href="https://github.com/LinLey-Nancy/LN-Mirror/releases/latest"><img src="https://img.shields.io/badge/%E6%9C%80%E6%96%B0%E7%89%88%E6%9C%AC-v1.0.0-success" alt="最新版本 v1.0.0" /></a>
<img src="https://img.shields.io/badge/Windows-10%2B%20x64-blue" alt="Windows 10+ x64" />
<img src="https://img.shields.io/badge/%E5%86%85%E7%BD%AE%20scrcpy-5.0.1-orange" alt="内置 scrcpy 5.0.1" />
<img src="https://img.shields.io/badge/.NET-10-purple" alt=".NET 10" />

**[下载便携包](https://github.com/LinLey-Nancy/LN-Mirror/releases/latest)** ·
[使用说明](docs/使用说明.md) ·
[设计文档](docs/Android-Windows投屏方案.md) ·
[更新日志](CHANGELOG.md)

*免安装，解压即用；自带官方 scrcpy 引擎，目标电脑不需要 .NET 运行时。*

</div>

## 30 秒上手

1. 从 [Release](https://github.com/LinLey-Nancy/LN-Mirror/releases/latest) 下载 zip，解压到任意目录
2. 手机打开「开发者选项 → 无线调试」（或插 USB 线；首次用 Wi-Fi 需按提示用配对码配对一次）
3. 画面出现在桌面上——透明度、穿透、悬停，用画面旁的工具条和主界面随手调

## 它解决什么

手机回消息、扫一眼聊天、查个小应用，都得解锁、切屏、再回来——来回横跳几次思路就断了。LN-Mirror 把手机画面**常驻在桌面上**：半透明铺在角落不打断当前工作，需要时鼠标挪过去直接点、滑、打字，用完继续写代码。

| | |
|---|---|
| **悬停显形** | 平时几乎完全透明（1%），鼠标停在它所在的位置才浮现；完全不可见时不挡鼠标、不入 Alt+Tab |
| **半透明也能操作** | 不透明度 0–100% 连续可调；半透明时点击仍旧生效，穿透开关独立控制 |
| **置顶 + 无边框** | 画面悬浮在所有窗口上；勾选无边框立即生效，无需重连 |
| **位置提示** | 画面完全隐藏后可选「淡边框 / 角标」在原地提示——不会找不到 |
| **工具条跟随** | 不透明度、「操控手机」、验证穿透、置顶、找回画面，全在画面旁一点即达 |
| **全局快捷键** | `Ctrl+Alt+Shift+R` 一键恢复画面 · `Ctrl+Alt+Shift+T` 切换穿透 |
| **无线优先** | 配对码配对、mDNS 自动发现、断线自动重连（端口变了也认得）；USB 插线可作备用连接 |

## 工作原理

两条写进代码底线的原则：

- **不重画画面，只做壳** —— 画面始终由官方 scrcpy 引擎解码显示，程序只负责连接管理和 Win32 外观操作（分层窗口透明度、鼠标穿透、边框样式、工具条、托盘）。没有第二套绘制与坐标换算，也就没有第二处可能出错的地方。
- **永远锁官方引擎** —— 只启动随包自带的 scrcpy 5.0.1；电脑端与手机端 server 的版本组合被环境变量固定，杜绝混用。

更完整的设计（会话生命周期、进程可靠识别、退出顺序、悬停判定与自绘把手）：[docs/Android-Windows投屏方案.md](docs/Android-Windows投屏方案.md)。

## 从源码构建

前置：本机装有 .NET SDK 10（`dotnet --version`）。scrcpy 引擎由仓库 `tools/` 提供，不需要单独安装。

```powershell
# 拉取官方 scrcpy 并按 SHA256 校验（升级引擎版本时用）
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\fetch-scrcpy.ps1 -Version v5.0.1

# 构建便携包：发布 + 包内自检 + 打 ZIP + 输出 SHA256
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1

# 或者日常开发直接跑
dotnet build src\LN-Mirror\LN-Mirror.csproj
dotnet run --project src\LN-Mirror -- --smoke-ui
```

生成应用图标（改了图标绘制逻辑时才需要）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\make-icon.ps1
```

## 命令行开关

| 开关 | 作用 |
|---|---|
| `--smoke-ui` | 界面冒烟检查：显示主窗口与工具条 3 秒后自动退出，退出码 0 表示启动无异常。**不会连接手机、不会写回配置** |
| `--exit-after <秒>` | 跑起来 N 秒后走和托盘「退出」完全同一条路径退出，用于自动验证退出流程（会按设置连接手机） |
| `--self-test` | 弹窗形式的环境自检（引擎、adb、配置目录、整窗 alpha 读写往返） |
| `--self-test --report <路径>` | 同上，但把报告写到文件，便于自动核对 |
| `--version` | 显示版本号 |

## 验证现状

这个仓库坚持把**「写了」和「在真机上确认过」分开说**，不接受“代码里看着没问题”：

| 范围 | 现状 |
|---|---|
| 局域网连接：配对、自动发现、断线重连、连着手机退出 | **已实机验证**（2026-10-23） |
| 退出后进程残留（死锁根因 + 看门狗兜底） | **已实机验证** |
| 悬停显示、位置提示、无边框切换、把手与工具条跟随 | 已实现，**待日常使用确认** |
| 音频转发 | 未实现，当前版本不启用 |
| 透明 / 穿透在真实显卡驱动下的表现 | **未验证**，需按 [docs/M0验证清单.md](docs/M0验证清单.md) 逐项实机确认 |
| 局域网真实无线环境下的配对与流畅度 | **未验证**，同上 |

## 文档

| 文档 | 用途 |
|---|---|
| [docs/使用说明.md](docs/使用说明.md) | 面向使用者：安装、配对、日常操作与排障 |
| [docs/Android-Windows投屏方案.md](docs/Android-Windows投屏方案.md) | 详细设计（唯一权威来源） |
| [docs/M0验证清单.md](docs/M0验证清单.md) | 实机验证清单：透明、穿透、快捷键、多机型 |
| [docs/投屏界面草图.html](docs/投屏界面草图.html) | 交互概念草图（浏览器打开） |
| [CHANGELOG.md](CHANGELOG.md) | 每个版本改了什么 |

## 隐私与日志

- 程序不收集、不上报任何数据——唯一的网络活动是与**你自己的手机**建立 scrcpy / adb 连接
- 日志只记录连接状态、版本、错误与性能诊断；**不记录**画面内容、输入内容、剪贴板、配对码和 ADB 私钥
- 日志位置：`%APPDATA%\LN-Mirror\logs`

## 许可

随包分发的 scrcpy 为官方原版二进制（未二次编译），版本、来源与 SHA256 记录在 [`tools/SCRCPY-VERSION.txt`](tools/SCRCPY-VERSION.txt)，其 Apache-2.0 许可副本随引擎目录一并附带。第三方组件许可说明与对外分发前的核对事项见该文件的说明。
