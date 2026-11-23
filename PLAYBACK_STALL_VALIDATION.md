# 验证记录

日期：2026-11-23（UTC+8）。环境：Windows + .NET SDK 11.0.100（`D:\dotnet`），x64 Debug / Release，Dev 包族 `Richasy.BiliCopilot.Dev_g7ew4fxhv1f46`。

**本文只写实际跑过的东西。** 每个「未验证 / 未做」都单列在最后一节，不做模糊表述。所有数字都是当次命令/日志的真实输出，没有估算。

## 1. 构建

| 目标 | 结果 |
|---|---|
| `BiliCopilot.UI.csproj` -c Debug -p:Platform=x64 | **成功，0 警告 0 错误**（多次；最后一次约 51s） |
| `BiliCopilot.UI.csproj` -c Release -p:Platform=x64 | **成功，0 警告 0 错误**（约 71s） |

构建命令（含必须的 E 盘临时目录）：

```powershell
$env:TMP="E:\build-temp-dsh"; $env:TEMP="E:\build-temp-dsh"
cd E:\Documents\deepseek-harness\default-workspace\bili-copilot-v2\src
D:\dotnet\dotnet.exe build Desktop\BiliCopilot.UI\BiliCopilot.UI.csproj -c Debug -p:Platform=x64 `
  "-p:SolutionDir=E:\Documents\deepseek-harness\default-workspace\bili-copilot-v2\src\"
```

### 环境限制（如实记录，未尝试）

本机**没有 Visual Studio / MSVC 链接器**，所以 `-p:GenerateAppxPackageOnBuild=true`（会触发 `PublishAot`）必然失败于 `Platform linker not found` / `vswhere.exe not found`。**没有尝试打包 MSIX，也没有为此改动任何 AOT 设置。** 验证走的是 `Add-AppxPackage -Register`（Dev 包、非打包部署），与项目现有的调试工作流一致。

### 构建期踩到的两个坑（记下来省下次）

1. `PRI210 0x800704c8 File move failed`：应用进程还活着时 `resources.pri` 被锁。处理：先 `taskkill` 掉 `*BiliCopilot*`，再把 `bin\resources.pri` 改名，重跑。
2. 忘了 `taskkill` 会得到 MSB3026/MSB3027「文件被 BiliCopilot.UI (PID) 锁定」，重试 10 次后失败。**这不是代码问题**，杀掉进程重跑即成功。

## 2. 单元测试

新增 `tests/BiliCopilot.Diagnostics.Tests`（xunit 2.7.0 / Microsoft.NET.Test.Sdk 17.10.0 / xunit.runner.visualstudio 2.5.7 —— 全部是 `src/Directory.Packages.props` 里已钉的版本，没有引入新包版本）。诊断逻辑以 `<Compile Include>` 复用同一份源码，不引用任何 WinUI / 日志包，所以能在 `net9.0` 下直接跑。

```powershell
D:\dotnet\dotnet.exe test tests\BiliCopilot.Diagnostics.Tests\BiliCopilot.Diagnostics.Tests.csproj
```

| 时点 | 结果 |
|---|---|
| 只有提交 1 的树 | **通过 18 / 失败 0**，0 警告 |
| 提交 1 + 提交 2 的树 | **通过 23 / 失败 0**，0 警告，约 0.7s |

覆盖清单（全部用可注入的假时钟，没有一次真等 180 秒）：

| 要求 | 测试 |
|---|---|
| 60ms 阈值边界 | `ReadStallBelowThresholdDoesNotFire`（59ms → None）、`ReadStallAtThresholdFires`（60ms → Dump） |
| 位置停滞阈值边界（本仓新增判据） | `ProgressGapBelowThresholdDoesNotFire`（599ms）、`ProgressGapAtThresholdFires`（600ms） |
| GC 变化跳过 | `GcDuringStallSkipsTheDump`（判为停滞、`SkippedGc`、不占名额） |
| 暂停不误采 | `PausedReadDoesNotDump` |
| 稳定期抑制 | `SettlingPeriodSuppressesTheDump` |
| 180s 限流 | `BudgetEnforcesMinimumInterval`（179999ms 拒、180000ms 准） |
| 最多 5 次 | `BudgetStopsAfterMaximumAttempts` |
| 跳过原因可区分 | `RateLimitIsReportedAsSkipped` |
| 同一停滞不重复 | `SameStallIsNotReportedTwice` |
| 停止后不再采 + 不 Join | `WatchStopsWithoutJoiningAndLogsSummary`（`Stop()` 必须 <90ms 返回、`Completion` 5s 内完成、0 dump、有 `watch-stopped` 行） |
| dump 失败清理 | `FailedDumpWriteReportsErrorAndLeavesNothing`（返回 false、有错误串、目录里无 `stall-*.dmp`） |
| dump 真实落盘 | `DumpWritesFileWithSidecarAndCleanupRemovesIt`（真调 `MiniDumpWriteDump`，断言 >4096 字节、侧车存在、`Cleanup` 能删） |
| 保留策略 | `PruneKeepsNewestDumpsAndRemovesSidecars`（12 个 → 剩 10 个，最旧的 `.dmp`+`.txt` 一起删） |
| 快照一致性 | `ProbeKeepsSnapshotCoherentAcrossReadBoundaries`、`ProbeOnlyBumpsAdvanceWhenPositionMoves`、`ProbeCountsEveryPublishSoHeartbeatCanProveLiveness` |
| 弹幕重建代号失效 | `NewerRebuildMakesOlderResultStale`、`FreshGenerationIsCurrent`、`StaleAndFailedAreIndependentFlags` |
| 端到端 ≠ 分段和 | `SegmentSumIsNotTheTotal`（7.25 vs 40.0） |

> 说明：要求里的「弹幕重建取消与旧结果失效」在本仓只做到**诊断层的代号语义**——本仓的重建流程没有 `CancellationToken`，也没有渲染侧 epoch（见 `DANMAKU_REBUILD_DIAGNOSTICS.md` 第 4 节）。测试据实只验证「新代号会让旧结果标记为过期」，没有假装渲染结果会被撤销。
>
> 「60ms 阈值边界」上游参数在保留 60ms 的前提下原样验证；本仓新增的 600ms 判据也做了同样的边界验证。

## 3. Release 隔离（静态）

对 `src\_build\x64\{Debug,Release}\BiliCopilot.UI\bin\BiliCopilot.UI.dll` 做**字节模式检索**（UTF-16 字面量与 UTF-8 元数据分别搜；直接按字节找，避免 UTF-16 奇偶偏移导致漏检 —— 这个坑我踩过一次，`Unicode.GetString` 整文件解码会漏掉落在奇数偏移的字符串）。

| 符号 | Debug | Release |
|---|---|---|
| `STALL \| watch-started`（字面量） | ✅ 有 | ❌ 无 |
| `STALL \| heartbeat` | ✅ 有 | ❌ 无 |
| `STALL \| dump session=` | ✅ 有 | ❌ 无 |
| `STALL \| watch-stopped` | ✅ 有 | ❌ 无 |
| `PlaybackStallWatch`（线程名字面量） | ✅ 有 | ❌ 无 |
| `stall-`（文件名前缀） | ✅ 有 | ❌ 无 |
| `PlaybackStall`（元数据） | ✅ 有 | ❌ 无 |
| `MiniDumpWriteDump`（元数据） | ✅ 有 | ❌ 无 |
| `DANMAKU \| rebuild`（弹幕计时，**两个配置都应有**） | ✅ 有 | ✅ 有 |
| `queue-ms` | ✅ 有 | ✅ 有 |

结论：Release 产物里**监视器类型、P/Invoke、全部日志字面量都不存在**；弹幕重建计时按设计保留在 Release。

## 4. Release 运行验证

1. 把 Release 包 `AppxManifest.xml` 版本改成 `1.0.27.0`（高于已装的 `1.0.26.0`），`Add-AppxPackage -Register`。
2. 确认运行的是 Release 产物：`Get-Process BiliCopilot.UI | Select Path` →
   `...\src\_build\x64\Release\BiliCopilot.UI\bin\BiliCopilot.UI.exe`。
3. 启动 → 打开历史记录 → 打开一个视频 → 播放约 45 秒。
4. 过滤本次运行时间窗（17:12:00 之后）的日志：

| 检查项 | 结果 |
|---|---|
| `STALL` 行 | **0 条** |
| `DANMAKU \| rebuild` 行 | **2 条**（说明弹幕计时确实在 Release 里工作） |
| `LocalState\Diagnostics` 目录 | 未被创建 |

## 5. Debug 集成测试（真实运行）

包版本 `1.0.26.0`（Debug 产物，绝对路径：`...\src\_build\x64\Debug\BiliCopilot.UI\bin\BiliCopilot.UI.exe`）。

### 5.1 实际跑过的操作次数（如实）

| 项目 | 目标 | **实际** |
|---|---|---|
| 连续播放长跑 | 30 分钟 | **未做**。最长连续会话 4 分 05 秒（17:07:02 → 17:11:07，位置推进到 113.5s） |
| 暂停 / 恢复 | 30 次 | **30 次**（脚本返回 `DONE=30`） |
| Seek | 30 次 | **30 次**（15 次 `→` + 15 次 `←`） |
| 切视频 | 20 次 | **1 次**（播放器内推荐位 `RootCard` 回车切换成功；之后推荐位不可见，脚本报 `NO_RECOMMEND_CARD`，未继续） |
| 从历史记录打开视频 | — | **2 次**（用作会话切换样本） |
| 弹幕开关 | 30 次 | **0 次**（见 5.4） |
| 样式切换 | 20 次 | **0 次**（见 5.4） |
| 高密度弹幕 | — | **未专门构造** |
| 关播放器时存在未完成重建 | — | **未构造** |
| 应用内故障注入 | — | **未做**（见第 7 节） |

### 5.2 会话与监视器配对

今日日志统计：`watch-started` **5** 条 / `watch-stopped` **3** 条。

差异的原因**不是漏停**，而是：

- 16:52:31 与 16:58:15 这两个会话是用**当时的旧构建**（心跳还没加）跑的，中途我用 `taskkill` 强杀进程验证构建，进程直接消失 → 自然没有 `watch-stopped`。
- 之后的 3 个会话全部成对：`s1-1` 17:07:02 → 17:08:54 停（切视频）；`s1-2` 17:08:54 → 17:08:59 停（切视频重入）；`s1-3` 17:08:59 → **17:11:07 关窗时停**。

**关窗后停止的证据**（原文）：

```
2026-11-23 17:11:07.094 [INF] STALL | watch-stopped session=s1-3-20261123T090859Z stalls=0 dumps=0
        skipped-gc=0 skipped-settling=0 skipped-rate=0 attempts=0 remaining=5
```

### 5.3 假阳性 dump：0

整个 Debug 集成期间（3 场播放、30 次暂停/恢复、30 次 seek、1 次切视频）：

| 指标 | 结果 |
|---|---|
| `STALL \| dump` 行 | **0** |
| `STALL \| dump-failed` 行 | **0** |
| `STALL \| skipped` 行 | **0** |
| `heartbeat` 里的 `stalls` 计数 | **0**（三次心跳都是 `stalls=0 dumps=0 skipped-*=0`） |
| `LocalState\Diagnostics` 目录 | **从未被创建**（没有任何 dump） |

### 5.4 弹幕开关 / 样式切换为什么是 0 次

`IsShowDanmaku` 与弹幕样式都在播放器覆盖层右侧的「弹幕显示选项」弹出面板里（`Controls/Danmaku/DanmakuDisplayOptions.xaml`），入口按钮没有 `AutomationId`、名字为空，需要先让覆盖层悬停展开再用鼠标点，属于不可靠的 UIA 路径。**我没有把「没做到」包装成「做到了」**：这两项记为 0 次，功能本身由单元测试与代码审查覆盖，但**真实 UI 路径的反复切换未验证**。

### 5.5 弹幕重建计时的真实样本

```
17:07:14.535  DANMAKU | rebuild session=s1-1-20261123T090702Z reason=visibility events=33
              queue-ms=500.7 build-ms=4.6 write-ms=0.6 style-ms=0.0 select-ms=0.0 resume-ms=0.1
              work-ms=5.4 total-ms=507.5 stale=true failed=false tick=301284318121 utc=2026-11-23T09:07:14Z
17:07:14.541  DANMAKU | rebuild session=s1-1-20261123T090702Z reason=items events=33
              queue-ms=510.1 build-ms=0.1 write-ms=0.7 style-ms=0.0 select-ms=0.4 resume-ms=0.0
              work-ms=1.3 total-ms=511.3 stale=false failed=false tick=301284440586 utc=2026-11-23T09:07:14Z
```

可读出的信息：

- `events=33`：真实弹幕条目数；`build/write` 都是毫秒以下到几毫秒级，**重建本身不是瓶颈**；
- `queue-ms≈500–510`：几乎全部时间花在入队等待（含原代码固定的 `Task.Delay(500)`）；
- `work-ms=5.4` vs `total-ms=507.5`：**分段和与端到端的差距被明确记录**，没有拿 5.4 冒充 507.5；
- `stale=true`：第二条重建追上了第一条，第一条被正确标记过期；
- `failed=false`：渲染层没有抛异常。

### 5.6 心跳（证明监视器活着，且给出真实通知节奏）

```
17:08:02.942  STALL | heartbeat session=s1-1-… publishes=2648 publish-gap-ms=635.3 playing=false pos=44.067
              stalls=0 dumps=0 skipped-gc=0 skipped-settling=0 skipped-rate=0 attempts=0 remaining=5
17:09:59.396  STALL | heartbeat session=s1-3-… publishes=1437 publish-gap-ms=30.6  playing=true  pos=53.48  …
17:10:59.400  STALL | heartbeat session=s1-3-… publishes=2937 publish-gap-ms=0.3   playing=true  pos=113.52 …
```

- 60 秒内 1437～2937 次位置发布 → **约 24～49 次/秒（间隔 20～42ms）**。这是评估 `600ms` 位置停滞阈值的关键实测依据（≈ 正常间隔的 25 倍以上）。
- `publish-gap-ms=635.3` 且 `playing=false`：暂停时位置不再前进，间隔自然拉长 —— 因为 `playing=false`，`progress-gap` 判据根本不参与，**没有误判**。这条样本同时验证了「暂停不误采」在真实链路上成立。

### 5.7 线程泄漏

用 `Get-Process | Select Threads.Count` 在会话切换前后采样：

| 时点 | 线程数 |
|---|---|
| 一个播放会话进行中 | 214 |
| 关闭播放器后 | 209 |
| 再开关一次后 | 208 |

线程数**没有增长**（反而回收了几个）。样本只有 3 个点、且没有 20 次切视频的压力，所以只能说「未见增长迹象」，不能宣称「已验证无泄漏」。

### 5.8 性能对比（改动前 vs 改动后）

同一台机器、同一个 Dev 包，改动前的日志（`log-2026-11-22.txt`）与改动后（`log-2026-11-23.txt`）：

| 指标 | 改动前（11-22） | 改动后（11-23） |
|---|---|---|
| `PERF UiLag` p50 | 0.08 – 0.10 ms | 0.09 – 0.11 ms |
| `PERF UiLag` p95 | 0.52 – 1.50 ms | 1.48 – 2.53 ms |
| `PERF UiLag` max | 1.26 – 48.75 ms | 2.00 – 48.0 ms |
| `PERF Load.MpvInit` | 16202 ms | 7798 – 8238 ms |
| `PERF Load.Total` | 17294 ms | 9516 – 9570 ms |
| `PERF Load.Danmaku` | 206.70 ms | 108.57 – 805.20 ms |

**怎么读这张表（重要）**：

- 这不是 A/B。改动前后是不同时间段、不同视频、不同网络状况的单样本对比，`Load.MpvInit` 的「变快」**不能归因于我这次改动**（我没有碰 mpv 初始化路径）。
- `UiLag` p95 从 ~1ms 抬到 ~1.5–2.5ms，量级未变、仍远低于 16.7ms 帧预算。**我没有证据说明这是诊断代码引起的还是当天机器状态/视频码率差异**（当天播放的是 4K `qn=120`）。这一条记为「未定论」，需要严格 A/B 才能定论。
- 唯一可以确定的是：诊断代码没有引入可见的卡顿（p95 仍在毫秒级，`STALL | skipped` 一条都没出现）。

### 5.9 一次真实的 `progress-gap` 候选（被 GC 闸门正确拦下）

这一条是收尾阶段才出现的，但它比前面所有「0 条」的记录更有信息量，所以单独记下来。

应用启动后自动续播了一个视频（session `s1-1-20261123T093230Z`），启动期负载很重（`gc0=3815` 次 Gen0），日志出现：

```
17:33:49.981  STALL | skipped session=s1-1-20261123T093230Z type=progress-gap reason=gc-or-wake-gap
              observed-ms=604.0 playing=true pos=39.5 gc0=3815 attempts=0 remaining=5 tick=317238828098
```

怎么读这条：

- **判据确实在真实链路上工作了**：`type=progress-gap` 说明「播放中位置 604ms 没前进」这个条件真的被判出来了，不是只有单元测试里成立。
- **抑制闸门也工作了**：`reason=gc-or-wake-gap` 说明它被拦下——启动期 GC 剧烈（`gc0=3815`）或监视线程自己被饿过，这段耗时不能算「mpv 卡住」。
- **没有消耗名额**：`attempts=0 remaining=5` —— 被抑制的候选不占用 dump 配额，这是有意为之（只有真正要写盘才 `TryReserve`）。
- 位置 `pos=39.5`、`playing=true`＋`publishes` 仍在增长（前一分钟 1560 次），说明当时画面在推进，604ms 的空档是启动期抖动而不是内核卡死 —— **闸门的判断是对的**。

如果这一条被写成 `STALL | dump`，就是一次典型的假阳性；实测没有。

## 6. Dump 有效性

本机没有 WinDbg/cdb，用 `dotnet-dump`（`%USERPROFILE%\.dotnet\tools\dotnet-dump.exe`，已装）。

因为没有在应用里制造出真实停滞（0 dump），dump 有效性用**同一份生产代码**验证：在 `E:\build-temp-dsh\dumpcheck` 建了一个临时 console 工程，用 `<Compile Include>` 直接编译本仓的 `PlaybackStallDump.cs` 并调用 `TryWrite`（与桌面端完全同一条代码路径、同一组 flag）。

```
ok=True file=stall-20261123T091127.5339715Z.dmp bytes=183573 write-ms=388.0 error=
```

落盘结果（含侧车与自动生成的 `.gitignore`）：

```
.gitignore                              2 bytes   （内容 "*"）
stall-20261123T091127.5339715Z.dmp 183573 bytes
stall-20261123T091127.5339715Z.txt    145 bytes
```

`dotnet-dump analyze` 输出（`clrstack -all`，节选）：

```
Loading core dump: E:\build-temp-dsh\dumps\stall-20261123T091127.5339715Z.dmp ...
OS Thread Id: 0x1c28
        Child SP               IP Call Site
000000E0317DFBE8 00007ff90306a944 [DebuggerU2MCatchHandlerFrame: 000000e0317dfbe8]

OS Thread Id: 0xa7e8
000000E02F97E350 00007ff90306c7f4 [InlinedCallFrame: ...] BiliCopilot.UI.Services.Diagnostics.PlaybackStallDump.MiniDumpWriteDump(IntPtr, UInt32, Microsoft.Win32.SafeHandles.SafeFileHandle, UInt32, IntPtr, IntPtr, IntPtr)
000000E02F97E300 00007FFF035957A2 ILStubClass.IL_STUB_PInvoke(IntPtr, UInt32, Microsoft.Win32.SafeHandles.SafeFileHandle, UInt32, IntPtr, IntPtr, IntPtr)
000000E02F97E450 00007FFF03594CB7 BiliCopilot.UI.Services.Diagnostics.PlaybackStallDump.TryWrite(...) [E:\...\Services\Diagnostics\PlaybackStallDump.cs @ 96]
000000E02F97E660 00007FFF03594865 Program.<Main>$(System.String[]) [E:\build-temp-dsh\dumpcheck\Program.cs @ 4]

OS Thread Id: 0xac90
000000E00077FB98 00007ff90306a944 [DebuggerU2MCatchHandlerFrame: 000000e00077fb98]
```

**结论**：dump 能打开，`clrstack -all` 能枚举全部 OS 线程并解析到**托管方法与源码行**。这同时证明 `MiniDumpNormal | MiniDumpWithThreadInfo` 这组 flag **足以出线程栈**（对应分析文档第 2 节的判断），也证明 P/Invoke 声明（`hProcess` 伪句柄 + 真实 pid）是对的——否则 `MiniDumpWriteDump` 会直接返回失败。

写作时注意：转储线程自己出现在栈上是正常的（`MiniDumpWriteDump` 就在它上面跑）；排查卡顿时看的是**其它**线程。

## 7. 故障注入

**没有在应用内做**。原因与替代方案：

- 要求明确禁止在真实 UI 线程或 mpv 关键线程里 `Thread.Sleep`；而本仓没有现成的「可注入阻塞」的缝，硬加一个缝属于改播放代码。
- 替代：判定链路的注入在**单元测试**里完成（假时钟 + 可控 GC 计数 + 直接调 `PlaybackStallProbe.BeginRead/EndRead/Observe` 与 `PlaybackStallPolicy.Observe`），覆盖了「人为延长一次读取 → 超过 60ms → 触发判定」这条主线；写盘链路用第 6 节的 harness 真调 `MiniDumpWriteDump`。
- **没做到的**：没有一条「应用真实跑起来、真实触发一次 dump、再用 dotnet-dump 打开那个 dump」的端到端证据。这是本次验证最大的空缺。

## 8. 未验证 / 未做清单（不做掩饰）

| 项 | 状态 |
|---|---|
| 30 分钟连续播放 | 未做（最长 4 分 05 秒） |
| 20 次切视频 | 只做到 1 次 |
| 30 次弹幕开关 | 0 次 |
| 20 次样式切换 | 0 次 |
| 高密度弹幕场景 | 未构造 |
| 关播放器时存在未完成重建 | 未构造 |
| 应用内故障注入 / 端到端 dump 触发 | 未做（见第 7 节） |
| 弹幕显示是否正常（视觉检查） | 未做。只做了间接证据：`events=33` 且 `failed=false`，说明重建链路完成；**没有截图确认画面上弹幕真的在动** |
| 严格 A/B 性能对比 | 未做（第 5.8 节是单样本对比，已标注不可归因） |
| 线程泄漏压力验证 | 只有 3 个采样点，只能说「未见增长」 |
| MSIX 打包 | 未尝试（本机无 MSVC 链接器，见第 1 节） |
| `progress-gap` 真实内核卡死样本 | 没有抓到真实卡死，该判据只有单元测试覆盖 |

## 9. 已知问题与后续建议

1. **60ms / 600ms 两个阈值都没有真实卡顿样本校准过。** 现在的依据是「实测通知间隔 20–42ms」+「用户可感知的卡顿量级」。若现场出现误报或漏报，改 `PlaybackStallPolicy` 里的两个常量即可，不需要动结构。
2. **应用内端到端触发链未被证实。** 建议下一次在真机上刻意制造一次卡顿（例如拖动窗口/让 GPU 忙）或在 Debug 构建里临时加一个「人为把一次读取拖到 200ms」的开关（只在 Debug、只在非 mpv 线程），跑通「dmp 落盘 → dotnet-dump 打开」。
3. **`LocalState\Diagnostics` 目录在没有任何 dump 时不会创建**（`Directory.CreateDirectory` 只在写盘时调用）。若希望「目录常驻、方便用户找」，可以在会话启动时顺手建一下——本次没有做，避免无意义副作用。
4. **`watch-started` / `watch-stopped` 的配对在「进程被强杀」时会缺一半。** 这是设计使然（强杀时没有任何清理机会），排查时以 `started` 出现、后面没有对应 `stopped` 且有新 `started` 来识别。
5. **心跳每 60 秒一行**，一场 30 分钟播放会写 30 行；若觉得吵，调 `PlaybackStallWatch.HeartbeatIntervalMilliseconds`。
6. 上游那两个提交后续没有任何修补提交（已用 `git log -S` / `git log -- <path>` 确认），所以本仓不存在「跟着上游修 bug」这一层落差；本文档第 2 节记的两个上游缺口（无总大小上限、`.threads.txt` 侧车从没写过）是本仓自己补的。
