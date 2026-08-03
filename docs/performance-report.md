# 低资源性能验证

更新时间：2026-08-03（Asia/Shanghai）

## 当前发布包

```text
文件：dist\CodexUsageWidget.exe
大小：164,493,889 字节（156.87 MiB）
SHA-256：1211FA8FC6B8E316E94C537D5BFFD1922C885241B67AC36E48DCEE2FC390FFB7
```

## 当前圆环待机样本

测试先预热 12 秒，再要求 SQLite 连续稳定 5 秒，随后正式观察 65.013 秒。窗口在
观察开始和结束时均硬校验为 80×80、192 DPI。

| 项目 | 结果 |
|---|---:|
| CPU 增量 | 0.015625 秒 |
| 单核口径平均 CPU | 0.024% |
| 16 线程整机口径平均 CPU | 0.0015% |
| 私有内存 | 81,842,176 字节（78.05 MiB） |
| 峰值私有内存 | 81,969,152 字节（78.17 MiB） |
| 工作集 | 173,805,568 字节（165.75 MiB） |
| 正式观察期数据库变化 | 0 |
| SQLite 写入 | `databaseWritesObserved=false` |
| 验证结果 | `validationPassed=true` |

机器可读结果见 [`idle-performance.json`](idle-performance.json)。

CPU 口径：

```text
单核 CPU% = 进程 TotalProcessorTime 增量 / 墙钟时间 × 100
整机 CPU% = 单核 CPU% / 逻辑处理器数
```

短样本会受调度粒度、缓存和系统负载影响，主要硬标准是：

- 收起态没有固定 30 秒统计轮询；
- 正式观察期 SQLite DB / WAL / SHM 无变化；
- 窗口始终保持目标待机尺寸；
- 进程没有退出，CPU 没有持续活动回归。

## 真实索引负载

以 854 个文件、18,114,323,308 字节的真实 Codex Home 做全新隔离重建。读取期间
活动日志继续追加，因此首轮实际处理 18,114,366,919 字节。索引采用
单线程顺序读取，进度回调每个 64 KiB 读取块最多一次，UI 事件进一步限流为约每
100 毫秒一次。隔离重建期间数据库持续增长、进程持续响应，最终：

```text
quick_check = ok
foreign_key_violations = 0
initial_index_complete = 1
outdated_accounting_rows = 0
```

完成后的增量复核只处理 2 个变化文件、73,788 个新增字节，总量由
9,288,668,988 增至 9,290,110,767。活动日志有追加时只处理变化文件的新增尾部。

## 复测命令

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\measure-idle.ps1 `
  -CollapsedMode Circle `
  -DurationSeconds 65
```

可分别把 `CollapsedMode` 改为 `Glow` 或 `Capsule`。若要先打开周额度浮层，再测试
收起后的静默状态，可加 `-PrimeWeeklyQuotaOverlay`；该路径依赖真实指针，必须在
解锁的交互桌面运行。

## 证据边界

本脚本采集进程 CPU、私有内存、工作集、窗口尺寸/DPI 和 SQLite 文件变化；它不
采集 GPU、ETW 内核唤醒或能耗计量。因此结论限于上述实测指标，不外推为“绝对零
资源消耗”。
