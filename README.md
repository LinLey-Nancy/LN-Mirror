# LN-Mirror

在 Windows 桌面上半透明悬浮显示安卓手机，并用鼠标直接操作它。

- 详细设计：[`Android-Windows投屏方案.md`](Android-Windows投屏方案.md)（路线 A：包装官方 scrcpy）
- 交互草图：[`投屏界面草图.html`](投屏界面草图.html)（浏览器里的概念示意）
- 面向使用者的说明：[`docs/使用说明.md`](docs/使用说明.md)（会随交付包一起分发）
- 版本更新：[`CHANGELOG.md`](CHANGELOG.md)
- 实机验证：[`docs/M0验证清单.md`](docs/M0验证清单.md)

## 当前状态

| 范围 | 状态 |
|---|---|
| 工程、连接管理、透明度、置顶、穿透、工具条、托盘、快捷键、诊断日志 | 已实现（0.1.0） |
| 局域网连接（配对码配对、mDNS 自动发现、手动地址回退、历史设备、断线自动重连、USB 切无线备用入口） | 已实现（0.2.0）；端口变化自动发现、断线重连与已连通退出已于 2026-10-23 实机验证 |
| 悬停显示画面、位置提示、无边框即时切换、悬停把手与自绘定位点、工具条精简与贴边跟随 | 已实现（1.0.0），**待日常使用确认** |
| 音频转发 | 未实现，当前版本不启用 |
| 透明与穿透在真实显卡驱动下的表现 | **未验证**，需要按 `docs/M0验证清单.md` 实机确认 |
| 局域网在真实无线环境下的配对、重连与流畅度 | **未验证**，需要实机确认 |

代码里所有和结论有关的地方都按「已实现」和「已验证」分开表达：进程内的 Win32 自检只证明调用链可用，不等于 scrcpy 画面窗口在你的机器上已验证。

## 目录结构

```
LN-Mirror/
├─ Android-Windows投屏方案.md     详细设计（唯一权威来源）
├─ 投屏界面草图.html / .png        交互概念草图
├─ docs/                          使用说明、M0 实机验证清单
├─ scripts/                       fetch-scrcpy.ps1、build.ps1、make-icon.ps1
├─ tools/                         内置官方 scrcpy（锁定版本 + SHA256 记录）
├─ src/LN-Mirror/                 WPF 主程序（C# / .NET 10）
└─ dist/                          构建产物（便携目录与 ZIP）
```

## 构建

前置：本机装有 .NET SDK 10（`dotnet --version`）。scrcpy 引擎由 `tools/` 提供，不需要单独安装。

```powershell
# 取官方包并按 SHA256 校验后放进 tools/（升级版本时用）
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\fetch-scrcpy.ps1 -Version v5.0.1

# 生成图标（改了图标绘制逻辑时才需要）
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\make-icon.ps1

# 构建便携包：发布 + 包内自检 + 打 ZIP + 输出 SHA256
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1
```

日常开发调试直接用：

```powershell
dotnet build src\LN-Mirror\LN-Mirror.csproj
dotnet run --project src\LN-Mirror -- --smoke-ui
```

命令行开关：

| 开关 | 作用 |
|---|---|
| `--smoke-ui` | 界面冒烟检查：显示主窗口与工具条 3 秒后自动退出，退出码 0 表示启动无异常。**不会连接手机、不会写回配置**，窗口固定出现在主显示器上 |
| `--exit-after <秒>` | 跑起来 N 秒后走和托盘"退出"完全同一条路径退出，用来自动验证退出流程（会按设置正常连接手机） |
| `--self-test` | 弹窗形式的环境自检（引擎、adb、配置目录、整窗 alpha 读写往返） |
| `--self-test --report <路径>` | 同上，但把报告写到文件，不弹窗，便于自动核对 |
| `--version` | 显示版本号 |

## 关键设计

- **不重画画面**：画面始终由 scrcpy 自己解码显示，WPF 只做壳（连接管理、Win32 外观操作、工具条、托盘）。不抓取 scrcpy 窗口重画，也不做第二套输入坐标换算。
- **窗口识别用进程号**：通过 `EnumWindows` + `GetWindowThreadProcessId` 找到本次启动的进程的画面窗口，不依赖窗口标题匹配。
- **透明度**：`WS_EX_LAYERED` + `SetLayeredWindowAttributes`，0–100 映射到 alpha 0–255。这是唯一需要实机确认的一环，配套提供了渲染后端切换（`auto` / `direct3d` / `opengl` / `software`）用于定位不兼容组合。
- **鼠标穿透**：`WS_EX_TRANSPARENT`，并且提供**差分验证**：在同一批屏幕采样点上分别问系统「最上面是谁」（关闭穿透 vs 开启穿透），只有关闭穿透时采样点确实落在画面窗口上，这次比较才成立；否则如实显示「未确认」，不假装通过。
- **0% 安全**：画面完全看不见时自动不接收点击（等价于临时穿透），但**不会改掉用户勾选的穿透状态**，调回非 0 后穿透照旧；三条恢复入口（工具条 / 主窗口 / 托盘 / 全局快捷键）互相独立；只记住上次**非零**的不透明度，避免重启后画面静默不可见。
- **悬停显示**：画面常态完全透明，鼠标移入画面范围才按设定的不透明度显示，靠 60ms 一次的光标位置轮询判定，不做跨进程子类化也不装全局钩子。画面不可见时的位置提示有「不提示 / 淡边框 / 角标」三档可选。
- **画面外观即时生效**：系统边框（`WS_CAPTION` / `WS_THICKFRAME`）与任务栏图标（`WS_EX_TOOLWINDOW`）都在运行时直接改窗口样式，改完立刻可见，不用重连 scrcpy。
- **不挡画面**：拖动把手要等鼠标在画面上停一会儿才出现，平时不占画面边缘的点击；鼠标停在画面上不动时系统箭头会隐藏，改由程序画一个小定位点。
- **切换前先释放输入**：模式切换前检查鼠标按钮是否松开，未松开则等待或放弃，并给画面窗口补发 `WM_CANCELMODE`，避免手机端卡在长按或拖动。
- **版本组合锁定**：只启动 `tools/` 里自带的 scrcpy，通过 `ADB` 与 `SCRCPY_SERVER_PATH` 环境变量固定电脑端与手机端 server 的对应关系。
- **只结束自己的会话**：结束时先给画面窗口发 `WM_CLOSE` 让 scrcpy 走正常清理流程（移除手机端 server、恢复设备状态），超时才强制结束；不使用全局 `adb kill-server`，不会影响你其它正在用的 scrcpy 或 adb。
- **日志边界**：只记连接状态、版本、错误与性能诊断；不记画面、输入内容、剪贴板、配对码和 ADB 私钥。

## 许可提示

随包附带的是 scrcpy 官方发布的二进制，未做二次编译。版本、来源与 SHA256 记录在 `tools/SCRCPY-VERSION.txt`；第三方组件许可说明与对外分发前的核对事项见该文件的说明。