# UI 黑盒验收

`scripts/capture-ui.ps1` 用真实鼠标、Win32 窗口尺寸和屏幕像素执行桌面 UI 验收，不依赖应用内部测试接口。脚本应在 Windows 已解锁的交互式桌面会话中运行。

## 运行

先完成 `dist\CodexUsageWidget.exe` 发布，再在项目目录执行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\capture-ui.ps1
```

默认综合验收要求先关闭现有 `CodexUsageWidget.exe`。脚本会备份设置，临时强制
默认圆环、自动收起和非固定状态，独占启动 `dist` 中的程序，验收后结束进程并按
原始字节恢复设置。验收前应确认 `dist` 来自本轮最终源码，避免把旧进程、旧截图
或旧报告当作本轮证据。若希望保留脚本启动的程序：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\capture-ui.ps1 `
  -LeaveRunning
```

也可以明确附加到主流程已经启动的实例：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\capture-ui.ps1 `
  -TargetProcessId 12345
```

脚本源文件只包含 ASCII 字符，可兼容 Windows PowerShell 5.1 和 PowerShell 7。坐标按目标窗口 DPI 缩放；基准验收环境为 1600×1000、96 DPI，同时支持多显示器的负坐标虚拟桌面。

运行前不要锁屏、切换用户或用远程会话遮挡目标窗口。屏幕截图使用 `CopyFromScreen`，其他置顶窗口会影响视觉证据和标签选中像素判断。

完整综合验收同时依赖真实鼠标和 UI Automation，应始终在已解锁且无遮挡的交互式桌面中执行。性能脚本的 `-PrimeWeeklyQuotaOverlay -PrimeFromPinnedStartup` 是独立的静默资源路径，不替代本页的真实悬停、拖动、移出和边缘锚点验收。

## 自动断言

脚本共有 16 个用例，按顺序检查：

1. `executable-and-process`：发现或启动目标路径的最终 EXE。
2. `main-window-discovery`：找到唯一主窗并记录 HWND。
3. `display-and-dpi`：记录虚拟桌面、工作区和目标窗口 DPI。
4. `collapsed-80x80`：默认圆环待机态逻辑尺寸为 80×80。
5. `hover-expanded-420x540`：真实鼠标悬停后展开为 420×540，并默认选中“今日”。
6. `tab-today`：点击并以屏幕像素确认“今日”唯一选中。
7. `tab-last-7-days`：点击并确认“近 7 日”唯一选中。
8. `tab-current-month`：点击并确认“本月”唯一选中。
9. `tab-all-time`：点击并确认“累计”唯一选中。
10. `weekly-quota-overlay`：通过 AutomationId `WeeklyQuotaTrendButton` 点击全局顶部唯一入口，确认窗内遮罩发生视觉变化，找到 7 个唯一的 `WeeklyQuotaDay_*` UIA 元素；再把真实鼠标移到一个日期节点，等待悬浮提示并断言进程未退出、主窗仍为 420×540，随后截图并通过 `WeeklyQuotaCloseButton` 关闭。
11. `pin-holds-expanded`：点击固定后移出鼠标，至少 5.5 秒仍为展开态。
12. `drag-changes-position`：在展开态标题区执行真实拖动，位置变化至少 30 个逻辑像素。
13. `unpin-auto-collapses`：取消固定并移出鼠标，0～150 毫秒内恢复 80×80；计时从指针移出前开始。
14. `edge-expansion-restores-anchor`：把锚点拖到当前工作区右缘，确认面板向左展开且完全留在工作区，再次收起后回到同一锚点。
15. `settings-open-close`：设置窗口以 460×590 打开，确认 System / Light / Dark
    主题、语言、待机样式以及 `GlassTransparencySlider` 的稳定 UI Automation
    选择器仍可访问，完成截图和关闭后主窗仍存在。
16. `final-collapsed-state`：最终回到 80×80 收起态。

脚本对圆环、自动收起和非固定状态的改动仅存在于验收期间；无论成功或失败，
结束后都恢复原设置。若使用 `-TargetProcessId` 显式附加已有实例，则不改设置，
调用方必须自行保证实例处于圆环、自动收起且非固定状态。

## 双待机样式专项验收

圆环/胶囊不作为过渡动画，而是两种稳定桌面待机形态。运行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\audit-idle-styles.ps1
```

脚本备份并恢复设置，分别验证：

- 默认圆环：80×80 待机，悬停直接进入 420×540，移出回到 80×80。
- 胶囊：208×80 待机，悬停直接进入 420×540，移出回到 208×80。
- 以 10 毫秒间隔采样同一个顶层 HWND；只允许所选待机尺寸和 420×540，不允许
  80×80、208×80 与展开态之间出现可观测中间窗口尺寸。

当前 [`idle-style-audit.json`](idle-style-audit.json) 为 2/2 PASS，测试环境
192 DPI。脚本以渐进式真实指针轨迹分别从 80×80 圆环和 208×80 胶囊触发
展开，完整流程耗时 229 毫秒与 185 毫秒；10 毫秒窗口采样均直接观测到
420×540，两者的 `intermediateTopLevelSizes` 均为空，没有出现曾经可捕捉的
420×80 分步尺寸。报告记录的最终 EXE SHA-256 为
`A2FB25D6C5CE204A5D3B2AD0A53575C163B7D399A19FCB1F39BE5E6F40124027`。

## 补充人工验收

以下项目涉及数据内容、系统主题事件或多显示器布局，需结合截图和实际状态补充确认：

- 任务超过 9 个时只显示前 9 个真实任务和第 10 行“其他”；“其他”没有归档徽标、任务 tooltip 或明细交互，且其条形比例可成为全表最大值。
- 排行、汇总卡片和 tooltip 中的 Token 均使用“万 / 亿 / 万亿”等中文单位，不再出现 `K / M / B`。
- 设置中的“跟随系统 / 浅色 / 深色”三种模式可切换，主窗、设置窗和 tooltip 同步更新；“跟随系统”应在 Windows 主题变化后通过事件响应。设置和主界面均不再出现皮肤选择器。
- 设置页按“外观 → 常驻行为 → 数据来源 → 维护”排列；主题、语言、待机样式位于
  最前面的外观分组，玻璃透明度紧随其后。语言支持“跟随系统 / 简体中文 /
  English”，待机样式支持“圆环 / 胶囊”。稳定选择器分别为
  `ThemeModeComboBox`、`LanguageModeComboBox`、`CollapsedModeComboBox` 和
  `GlassTransparencySlider`。
- 玻璃透明度范围为 0～100，数值越高背景越透明；滑动时只即时预览玻璃背景，
  文字、图标和状态色不得一起变淡。取消设置应恢复打开设置时的数值，保存后应在
  下次启动继续使用；调整过程不得触发日志读取、周期统计或数据库刷新。
- 单一 Vision Glass 界面不启用原生 Acrylic。加载、收起/展开、主题或 DPI 变化、
  拖动结束和手动刷新时，只抓取一次组件下方桌面；软件模糊在后台线程完成，结果
  冻结后填充到玻璃表面。静置时不得持续抓屏、持续模糊或运行渲染循环。
- 圆环、胶囊与展开面板外侧不得出现矩形底板、双层描边或额外装饰边框。
  80×80 / 208×80 / 420×540 状态切换和 DPI 变化后，快照只能出现在对应圆形或
  圆角区域内，透明角落不能残留方形玻璃底。
- 胶囊不得使用越过圆角遮罩的 `DropShadowEffect`；桌面快照和玻璃底应由独立
  圆角背景层裁切。浅色胶囊使用适合亮背景的深色文字与状态色，深色胶囊使用适合
  暗背景的高对比前景，不得复用一套导致其中一个主题看不清的固定颜色。
- 胶囊第二行不得固定显示“用量平稳”。70% / 30% / 10% / 0% 是动态状态边界，
  无实时数据时显示“正在同步”或“等待数据”；文字与波形同步使用绿、黄、橙、
  红状态色。圆环进度使用同一策略，且该派生状态不得新增计时器或轮询。
- 左侧空间不足时优先向右展开，右侧空间不足时向左展开；下方空间不足时向上展开。应至少覆盖一个负坐标显示器和 200% DPI。
- 固定展开后静置时排行不自行刷新；点击任一标签或重复点击当前标签时，只刷新目标周期。
- 每日周额度严格取 exact `limit_id=codex`、`window_minutes=10080` 的每天最后一次直接观测；七天柱状差值是相邻观测日净变化，不应标注成“当日新增”。无直接观测的日期明确显示无数据。
- 周额度浮层必须是主窗口内部 Overlay。主题验收应确认打开前后顶层 HWND 数量为 `1 → 1`，在浮层内移动鼠标不会触发主窗离开。
- 托盘和 EXE 应使用原创空心三环线稿图标；ICO 内含多种尺寸，在文件管理器、
  任务栏、托盘和高 DPI 缩放下保持清晰，且不使用第三方品牌图标文件。

三主题与浮层可独立运行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\capture-themes.ps1 `
  -CollapsedMode Capsule `
  -OutputDirectory .\docs\screenshots\capsule-themes
```

当前胶囊主题 `theme-audit.json` 应同时记录 System / Light / Dark 为 PASS、胶囊
208×80、展开 420×540、目标 DPI、`weeklyQuotaDayCount=7`、
`topLevelHwndUnchanged=true`，并为每个主题生成待机态、普通面板和 Overlay
截图。

100% 透明端点使用独立输出目录，避免覆盖普通透明度证据：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\capture-themes.ps1 `
  -CollapsedMode Capsule `
  -GlassTransparencyPercent 100 `
  -OutputDirectory .\docs\screenshots\capsule-transparent-100
```

## 输出与判定

成功时退出码为 `0`，终端应输出 16 个 `[PASS]`；任一必需项失败或跳过时退出码为 `1`，输出 `[FAIL]` / `[SKIP]`。完整机器可读报告：

```text
docs\screenshots\ui-audit.json
```

报告中的可执行文件路径、SHA-256 对应的发布批次、完成时间、窗口尺寸和收起耗时必须与本轮最终包一致。

当前 [`ui-audit.json`](screenshots/ui-audit.json) 记录 2026-07-19
Vision Glass 综合验收为 16/16 PASS：80×80 收起、420×540 展开、四标签、
窗内周额度 Overlay、真实日期悬停不崩溃、固定、拖动、65 毫秒自动收起、右缘
向左展开、锚点恢复、460×590 单一玻璃设置页及最终收起均通过。当前
[`胶囊主题报告`](screenshots/capsule-themes/theme-audit.json) 记录 System /
Light / Dark 均为 PASS；三种模式均验证 208×80 / 420×540、7 个周额度日期元素
和顶层 HWND `1 → 1`。另一个
[`100% 透明端点报告`](screenshots/capsule-transparent-100/theme-audit.json)
记录 `glassTransparencyPercent=100`，System / Light / Dark 同样全部 PASS。
综合 UI、主题、透明端点、语言和双待机机器报告都自带被测 EXE 路径、大小、
SHA-256 与时间；任何后续重新发布都必须整套重跑，不能跨哈希合并结论。

当前 [`localization-audit.json`](localization-audit.json) 为 PASS，完成
“简体中文 → English → 简体中文”双向切换，确认关键主界面及设置文字真实可见、
设置持久化且原设置已恢复。UI、主题、本地化和双待机报告均记录同一最终 EXE：
164,086,767 字节（156.49 MiB），SHA-256
`A2FB25D6C5CE204A5D3B2AD0A53575C163B7D399A19FCB1F39BE5E6F40124027`。

主要视觉证据：

```text
docs\screenshots\collapsed.png
docs\screenshots\expanded.png
docs\screenshots\expanded-today.png
docs\screenshots\expanded-last-7-days.png
docs\screenshots\expanded-current-month.png
docs\screenshots\expanded-all-time.png
docs\screenshots\weekly-quota-overlay.png
docs\screenshots\pinned-expanded.png
docs\screenshots\dragged-expanded.png
docs\screenshots\unpinned-collapsed.png
docs\screenshots\edge-collapsed.png
docs\screenshots\edge-expanded-contained.png
docs\screenshots\edge-anchor-restored.png
docs\screenshots\settings.png
docs\screenshots\final-collapsed.png
docs\screenshots\capsule-themes\theme-system.png
docs\screenshots\capsule-themes\theme-system-overlay.png
docs\screenshots\capsule-themes\theme-system-collapsed.png
docs\screenshots\capsule-themes\theme-light.png
docs\screenshots\capsule-themes\theme-light-overlay.png
docs\screenshots\capsule-themes\theme-light-collapsed.png
docs\screenshots\capsule-themes\theme-dark.png
docs\screenshots\capsule-themes\theme-dark-overlay.png
docs\screenshots\capsule-themes\theme-dark-collapsed.png
docs\screenshots\capsule-transparent-100\theme-system-collapsed.png
docs\screenshots\capsule-transparent-100\theme-light-collapsed.png
docs\screenshots\capsule-transparent-100\theme-dark-collapsed.png
docs\screenshots\idle-circle-collapsed.png
docs\screenshots\idle-circle-expanded.png
docs\screenshots\idle-capsule-collapsed.png
docs\screenshots\idle-capsule-expanded.png
```

失败时还会保存 `failure-01.png`、`failure-02.png` 等全虚拟桌面截图，便于区分窗口未出现、尺寸错误、被遮挡或交互坐标异常。

该脚本会切换当前标签页并移动悬浮窗位置，这是位置持久化验收所需要的真实用户操作。
