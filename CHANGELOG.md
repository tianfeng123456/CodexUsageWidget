# Changelog / 更新日志

本项目遵循 [Semantic Versioning](https://semver.org/)。
This project follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [1.2.1] - 2026-08-16

### 修复 / Fixed

- 修复面板收起会取消已经开始的日志索引补读，导致任务排行和周额度每日消耗长期
  停留在旧数据甚至显示为空的问题。收起现在只取消对应的查询与界面更新，已经触发
  的增量索引会继续完成；切换 Codex Home、息屏、锁屏和应用退出仍会及时停止读取。
- Fixed panel collapse cancelling an already-started log catch-up, which could
  leave task rankings and daily weekly-quota usage stale or empty. Collapse now
  cancels only the associated query and UI update while the triggered
  incremental index completes. Home changes, display dormancy, session lock,
  and shutdown still stop file work promptly.
- 修复 Codex 持续追加日志时，解析位置可能短暂超过缓存文件长度的竞态。新索引会
  保存解析后的最新文件信息；旧版留下的此类检查点会在启动时原地校正并保留统计，
  不再触发整库清空与重建。
- Fixed a concurrent-append race where the durable parser offset could exceed a
  cached file length. New checkpoints persist refreshed file metadata, and
  affected legacy rows are repaired in place without clearing valid usage data.
- 修复悬浮窗靠近屏幕边缘时，透明圆角或偶发丢失的鼠标离开事件可能让展开面板无法
  自动收起的问题。正常离开仍立即响应；展开且未固定时仅启用短时指针复核，收起或
  固定后立即停止，不增加待机态轮询。
- Fixed an expanded widget occasionally remaining open near a screen edge when
  a rounded transparent corner or a lost pointer-leave event prevented the
  final collapse. Ordinary leave remains immediate; a lightweight pointer
  fallback runs only while expanded and unpinned.

### 验证 / Validation

- 349 项自动化测试全部通过，Release 严格构建为 0 警告、0 错误。
- 真实本机索引验证返回今日 7、近 7 日 28、本月 88、累计 182 个任务，并读取到
  最近窗口的 10 天周额度观测；四个周期查询均保持毫秒级。
- 349 automated tests passed, and the strict Release build completed with zero
  warnings and zero errors.
- A live local-index audit returned 7 Today, 28 Last 7 Days, 88 This Month,
  and 182 All Time tasks plus 10 recent weekly-quota observation days, with
  millisecond-level period queries.

## [1.2.0] - 2026-08-03

### 修复 / Fixed

- 修复原生 SQLite 传递依赖命中高危安全公告的问题；显式固定到包含 SQLite
  3.50.2 修复的 `SQLitePCLRaw.bundle_e_sqlite3 2.1.12`，升级测试工具链，并让
  NuGet 的直接与传递依赖安全公告在后续构建中自动阻止已知漏洞回归。
- Fixed a high-severity advisory in the transitive native SQLite dependency by
  pinning `SQLitePCLRaw.bundle_e_sqlite3 2.1.12`, upgrading the test toolchain,
  and making future direct or transitive NuGet advisories fail the build.
- 修复退出、锁屏、休眠恢复或重复启动唤回恰好发生在 WPF Dispatcher 关闭期间时，
  非关键 UI 通知可能反向中断索引或清理流程的竞态；后台任务异常现在统一观察并写入
  本地轮转诊断日志。
- Fixed a shutdown race where non-critical UI notifications from indexing,
  power/session events, or duplicate-instance activation could interrupt
  indexing or cleanup after the WPF Dispatcher began shutting down. Background
  failures are now observed and recorded in a rotating local diagnostic log.
- UI 审计脚本现在会在锁屏桌面提前停止；失败运行会恢复原截图与正式报告，并把
  失败证据单独保存，避免把环境无效误写成产品回归。
- The UI audit now stops before touching settings on a locked desktop and
  restores prior evidence after a failed run instead of overwriting a valid
  report with an environment-induced failure.

- 修复主窗口隐藏后再次启动程序只提示“已在运行”、却无法找回窗口的问题；第二个
  进程现在会唤回并置前已有实例，随后立即退出，不会重复启动后台监控。
- Fixed the unreachable hidden-instance state. Launching the executable again
  now restores and foregrounds the existing widget, then exits the duplicate
  process without starting another monitor.
- 修复一次性索引迁移可能被面板收起、标签切换或调用方取消停在半途的问题；
  未完成的迁移会转交来源生命周期继续，重启后也会自动续建。
- Fixed one-time index migration getting stranded after panel collapse, tab
  changes, or caller cancellation. Incomplete work now continues under the
  source lifecycle and resumes after restart.
- 累计 Token 改为逐字段单调高水位增量，忽略并发日志中的轻微累计回落，避免一次
  回落被放大成数亿 Token；文件截断或改写仍通过文件连续性校验触发完整重建。
- Token accounting now uses per-field monotonic high-water deltas. Small
  cumulative rollbacks from concurrent log snapshots no longer become large
  phantom usage, while actual file replacement still triggers a clean reparse.
- 旧索引在下一次明确统计刷新时一次性完整重算，并保存算法版本；普通启动、收起
  静默态和后续增量刷新不增加固定轮询。
- Existing indexes are rebuilt once on the next explicit statistics refresh
  and persist the accounting version, without adding background polling.
- 修复根级子代理分叉日志把父任务历史再次计入累计的问题。受影响的分叉会与来源任务
  逐条比对累计 Token 序列，剔除最长复制前缀；即使触发标记也来自复制历史，
  仍能准确保留分叉后的真实新增量。父任务尚未入库时会暂缓该分叉，避免写入错数。
- Fixed root-level subagent fork rollouts replaying parent-task history into
  lifetime totals. Affected forks now trim the longest matching cumulative-token prefix
  against their source task, including copied trigger markers, and wait for the
  parent index when needed instead of committing an unverified total.

### 新增 / Added

- 首次使用或索引升级时，空数据区会显示按已处理日志字节计算的真实进度、百分比
  和本机处理说明；索引完成后会明确切换到时段汇总阶段，避免长时间读取被误认为
  程序无响应。
- First use and index upgrades now show determinate byte-based progress, a
  percentage, and a local-processing note in the empty dashboard. After
  indexing, the UI explicitly switches to the period-summary stage.
- 待机样式新增 32×32“微光”：不显示数字，仅以静态光圈和状态色表达剩余额度；
  设置选项按“微光、圆环、胶囊”排列，新安装仍默认圆环。
- Added a 32×32 Glow idle style that communicates remaining quota with a
  static halo and status color, without a number. Options are ordered Glow,
  Circle, Capsule, while new installations still default to Circle.
- 移除与实际桌面材质不一致的透明度模拟样片和实时预览文案；透明度在保存设置后应用。
- Removed the misleading simulated transparency sample and live-preview copy;
  transparency changes now apply after Save.

## [1.1.1] - 2026-07-20

### 修复 / Fixed

- 修正子代理日志继承根任务历史事件导致的累计 Token 重复统计。现在只排除首个
  顶层 `inter_agent_communication_metadata` 之前的继承回放，保留边界之后
  的首轮与全部后续用量；旧索引会在下一次明确刷新时一次性迁移。
- Fixed cumulative token overcounting caused by child rollouts replaying root
  history. Only inherited events before the first top-level communication
  boundary are excluded; the first real turn and all later child usage remain
  counted. Existing indexes migrate once on the next explicit refresh.

## [1.1.0] - 2026-07-20

### 新增 / Added

- 新增默认开启的“息屏或锁屏时暂停后台监控”。应用通过 Windows 显示、电源和
  会话事件进入休眠，不轮询屏幕状态；亮屏并解锁后重建监听并只校准一次最新额度。
- Added an event-driven, default-on dormant mode while the display is off,
  the session is locked, or the system is suspended. Monitoring is rebuilt
  after display-on unlock with one latest-quota calibration.

### 改进 / Changed

- 应用与托盘图标改为原创六瓣交织空心线稿，加粗高对比轮廓，并以右上角状态点
  表达剩余额度状态，在通知区域小尺寸下更清晰。
- Redesigned the original app and tray icon as a clearer six-lobe interlocking
  hollow-line mark with a theme-aware status dot.
- 修正玻璃透明度语义：`0%` 为完全不透明，`50%` 精确保留原始玻璃效果并作为
  新默认值；`100%` 精确对应旧版 `99%` 的视觉结果，保留非零安全材质与命中层，
  确保拖动和鼠标识别可靠。旧配置会一次性迁移到新刻度，升级后外观不突变。
- Corrected glass-transparency semantics: `0%` is fully opaque, `50%` exactly
  preserves the original glass look and is the new default, while `100%`
  exactly maps to the previous `99%` visual result with a nonzero safety layer. Existing settings
  migrate once so upgrades retain their prior appearance.
- 透明度滑杆提供实时预览；取消设置恢复原值，保存后持久化。预览只改变内存中的
  玻璃合成强度，不读取日志、查询统计或写入 SQLite。
- Added live transparency preview with Cancel rollback and persisted Save,
  without log reads, statistics queries, or SQLite writes.
- 周额度七日图改为显示各本地自然日观测到的消耗百分点。算法按服务端重置周期
  维护单调高水位，仅累计上升量，忽略并发旧快照和重置下降；相差 60 秒内的
  `reset_at` 会归并为同一逻辑周期。每天采用当天最后一次有效额度观测所属的
  时间线，并只计算该时间线的日内高水位增量，避免并行日志把同一次消耗重复
  相加，也不会把整个周期累计值归到今天。缺少当天起始基线时以“至少”标记
  可观测下限，不输出伪精确的日间对比。
- Changed the seven-day weekly-quota chart to reconstructed daily observed
  consumption. Per-reset-window monotonic high-water marks ignore stale
  concurrent snapshots and reset drops. `reset_at` values within 60 seconds
  are treated as one logical window. Each day follows the timeline from its
  final valid quota observation and reports only that timeline's intraday
  high-water increase, so parallel logs cannot duplicate usage or assign an
  entire window total to today. Days missing a start baseline are labeled as
  lower bounds instead of reporting false precision.

### 兼容性 / Compatibility

- 现有主题、语言、待机样式、窗口位置和索引继续兼容；缺少新增设置字段的旧配置
  会使用推荐默认值。
- Existing theme, language, idle-style, window-position, and index settings
  remain compatible. Older settings files use the recommended default for the
  new dormant-monitoring option.

## [1.0.0] - 2026-07-19

- 首次公开版本。
- First public release.

[1.2.1]: https://github.com/tianfeng123456/CodexUsageWidget/compare/v1.2.0...v1.2.1
[1.2.0]: https://github.com/tianfeng123456/CodexUsageWidget/compare/v1.1.1...v1.2.0
[1.1.1]: https://github.com/tianfeng123456/CodexUsageWidget/compare/v1.1.0...v1.1.1
[1.1.0]: https://github.com/tianfeng123456/CodexUsageWidget/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/tianfeng123456/CodexUsageWidget/releases/tag/v1.0.0
