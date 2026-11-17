# Codex 用量面板 · macOS

本分支基于云端 `main` / Windows v1.2.3（`b4c488403a97fff23480dd8a59cb6fa8019fed36`），新增 macOS 原生应用 v1.3.0-mac.6。统计引擎沿用原 C# Core，包括子代理归并、高水位去重、主动重置前后累计和大于 100% 的每日额度消耗。

## 使用

将 `Codex Usage Widget.app` 放入 `~/Applications`，双击启动。菜单栏显示剩余额度；桌面圆环悬停或点击展开，拖动顶部空白区域、额度数字、标题或整个收起态移动；曲线与按钮独立响应点击。右键菜单和菜单栏均可刷新、设置、退出。登录启动默认关闭，可在设置开启；如果系统要求批准，请在“系统设置 → 通用 → 登录项”中允许。

- 三种待机样式：32×32 微光、80×80 圆环、208×80 胶囊。
- 展开和七日图表共用固定 420×540 面板；支持固定展开、置顶和自动收起。
- 今日 / 滚动 168 小时 / 本月 / 累计，前 9 任务加其他；输入、输出、缓存输入、推理输出与总量。
- 七日周额度消耗柱线图：缺失日期留空，`≥` 表示仅可观测部分，右列为较前日变化。
- 默认深色原生毛玻璃，保留原版排版与配色；可选系统 / 浅色 / 深色，系统 / 简体中文 / English，玻璃透明度。
- 原生菜单栏、登录启动、位置和标签持久化、单实例激活、屏幕关闭 / 锁定 / 系统休眠暂停。
- 可选 Codex Home、重建索引、查看数据目录。

macOS 使用系统原生毛玻璃，无需屏幕录制权限。Token 统计按需刷新；额度通过文件系统事件增量尾读，无固定轮询。休眠时停止后台进程，唤醒后恢复监听和额度校准。已索引数据保存在 SQLite，异常终止可恢复。

## 数据与迁移

只读 `sessions/**/*.jsonl`、`archived_sessions/**/*.jsonl`、`session_index.jsonl`，以及所选 Codex Home 下最新可用 `state_*.sqlite` 的任务标题元数据。不读取 `auth.json`，不联网、不上传，不提取聊天正文。账号额度是本机日志最新观测值，不是实时联网查询；没有观测显示 `--`。仅覆盖当前 Mac 能读取的历史日志。Windows 日志若已迁入当前 Codex Home，会按同一规则索引；未迁移的日志不会凭空补齐。

任务名称优先采用桌面应用的显式 `threads.name`，其次沿用 `session_index.jsonl` 的名称；两者缺失时仅接受简短、单行的旧 `threads.title`。不将长提示词或多行内容当作名称，不改写 Codex 数据库。名称在启动、切换周期或手动刷新时重新读取；没有可用名称的记录保留原版回退显示。

设置与索引：`~/Library/Application Support/CodexUsageWidget/`。不同 Codex Home 使用独立数据库。诊断日志为同目录 `diagnostics.log`，到 1 MiB 轮转。后台运行时会把内嵌原生运行库解压到 .NET 默认的用户缓存目录 `~/.net/`，无须另装运行时。

旧 Windows 源码和历史交付保留，Windows WPF 启动路径没有改动。共享引擎另修复了“已确认没有复制前缀的子代理”保存 -1 检查点后，被旧验证器误判为损坏并反复重建历史的缺陷。macOS 不直接复用 Windows 的窗口坐标和启动项，也不把 Windows SQLite 路径指向旧磁盘。

## 构建和验证

需要 macOS、Apple Command Line Tools（Swift 编译器）、.NET 8 SDK 和 Python 3。

```sh
# 共享统计测试（非 Windows 主机自动跳过 3 项 Windows 命名事件测试）
dotnet test tests/CodexUsageWidget.Tests -c Release
# 构建、临时本机签名、签名校验和打包后协议测试
DOTNET_BIN=/path/to/dotnet macos/build.sh
```

默认产物为 Apple Silicon / macOS 13+，`dist/Codex Usage Widget.app`。构建脚本可指定 `ARCH=x64`，但 Intel 机器未经过本次实机验收，不作为本次交付。

```sh
mkdir -p macos/.build
xcrun swiftc macos/Sources/Model.swift macos/Sources/Geometry.swift macos/tests/ModelTests.swift -o macos/.build/model-tests -framework AppKit -framework Combine -framework ServiceManagement
macos/.build/model-tests
xcrun swiftc macos/Sources/Model.swift macos/Sources/Geometry.swift macos/Sources/Views.swift macos/tests/RenderingTests.swift -o macos/.build/rendering-tests -framework AppKit -framework SwiftUI -framework Charts -framework ServiceManagement
macos/.build/rendering-tests /tmp/codex-widget-renders
```

协议测试使用临时合成日志，验证启动、额度、文件监听、四周期、任务名称恢复与改名刷新、去重、周额度、重建、缓存启动和退出。原生测试验证单位、时间戳、额度窗口、边缘定位、锚点与休眠条件；离屏渲染覆盖两种主题、三种待机形态与展开 / 周图表，不修改用户设置。

本地应用使用 ad-hoc 签名，未做 Apple Developer ID 签名和公证。适用于本机迁移交付；面向其他用户分发前应补齐签名、公证及更多 macOS 版本实机测试。

## mac.4 交互回归

对照 Windows 开发对话、v1.1.0–v1.2.3 修复记录和 UI 验收逐项补齐：

- AppKit 鼠标区域使用事件的屏幕坐标移动窗口，避开 SwiftUI 随窗口变化的坐标系；结束后保存原有展开边缘对应的待机锚点，并约束到可见屏幕。
- 顶部整块区域可拖动，曲线和工具按钮以实际布局矩形排除；三种待机态共用整个区域的拖动实现。
- 普通移开在下个输入回合检查收起；仅展开、未固定、允许自动收起、设置未打开且未暂停时启用 125ms 指针复核。微光保留原始触发区直到指针离开，拖动和换样式会清除。
- 待机、固定、隐藏和暂停停止指针复核。鼠标丢失释放事件只在拖动期间通过临时检查收尾。
- 设置关闭后恢复收起判断；暂停期间切换数据源不会意外启动后台；登录项处于待批准状态时不再被开关回调自动注销。
- 启动按保存周期读取缓存，避免周期不匹配触发全日志刷新；显示窗口前先启动后台，避免首次悬停重复启动。
- 归档标识、任务五项用量提示、状态四舍五入、加载阶段提示、实际统计更新时间和周图至少 100% 的刻度与原版对齐。

开发诊断可用 `--interaction-audit` 输出鼠标事件与窗口几何到 stderr；默认关闭。它不输出任务名称、日志内容或凭据。

## mac.5 设置预览与周图

打开设置立即收起桌面面板；设置期间仍支持原来的悬停展开与移开收起。外观中新增“图标大小”滑块，50%–125%、每档 5%；100% 为原始尺寸，三种待机形态等比例缩放，展开面板保持 420×540。调整滑块或切换样式时收起面板，桌面图标实时预览并保存大小。旧设置无需迁移，读取时保留全部选项和位置。

七日周额度消耗直接在每个有效数据点标注百分比，保留部分观测 `≥` 和缺失日期留空，移除下方表格；超过 100% 时自动扩展纵轴并为标签留出空间。

设置窗口与浮窗联动检查：`macos/tests/settings_smoke.sh`。该检查使用不落盘的独立模型，不修改用户设置。

## mac.6 透明度修复

玻璃透明度滑块现在同时更新系统毛玻璃材质层和背景颜色层，按 1% 调节并实时保存。文字、图标和进度线不跟随背景变淡；100% 端点保留少量材质和命中层。原生联动测试验证 0% / 25% / 50% / 100% 对应的实际 NSVisualEffectView alphaValue，桌面滑块两端响应已检查。
