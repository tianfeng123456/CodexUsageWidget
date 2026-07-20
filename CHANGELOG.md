# Changelog / 更新日志

本项目遵循 [Semantic Versioning](https://semver.org/)。
This project follows [Semantic Versioning](https://semver.org/).

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

[1.1.0]: https://github.com/tianfeng123456/CodexUsageWidget/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/tianfeng123456/CodexUsageWidget/releases/tag/v1.0.0
