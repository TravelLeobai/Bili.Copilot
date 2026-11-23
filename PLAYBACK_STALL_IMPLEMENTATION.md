# 播放停滞 Dump：移植范围与实现

对应提交：`feat(debug): add playback stall dump diagnostics`（见最终回复里的 hash）。

本文写四件事：**改了哪些文件 / 什么条件下会触发 / 线程与对象生命周期 / 安全与隐私**。判定参数为什么这样定，见 `PLAYBACK_STALL_UPSTREAM_ANALYSIS.md` 第 10 节的评估表。

## 1. 改动清单

### 新增

| 文件 | 角色 |
|---|---|
| `src/Desktop/BiliCopilot.UI/Services/Diagnostics/PlaybackStallProbe.cs` | `PlaybackStallGcCounts`、`PlaybackStallSnapshot`（不可变快照）、`PlaybackStallProbe` |
| `src/Desktop/BiliCopilot.UI/Services/Diagnostics/PlaybackStallPolicy.cs` | `PlaybackStallKind`、`PlaybackStallDecision`、`PlaybackStallPolicy`（阈值 + 抑制 + 去重） |
| `src/Desktop/BiliCopilot.UI/Services/Diagnostics/PlaybackStallDumpBudget.cs` | 进程级限流 |
| `src/Desktop/BiliCopilot.UI/Services/Diagnostics/PlaybackStallDump.cs` | `MiniDumpWriteDump` 落盘 + 保留 + 清理 |
| `src/Desktop/BiliCopilot.UI/Services/Diagnostics/PlaybackStallWatch.cs` | 监视线程 + `IPlaybackStallLog` |
| `src/Desktop/BiliCopilot.UI/ViewModels/Core/PlayerViewModel/PlayerViewModel.Diagnostics.cs` | 会话 id、监视器接线、Serilog 适配 |
| `tests/BiliCopilot.Diagnostics.Tests/*` | xunit 测试（编译期复用同一份源码） |

五个类型名沿用上游：`PlaybackStallDump` / `PlaybackStallProbe` / `PlaybackStallPolicy` / `PlaybackStallDumpBudget` / `PlaybackStallWatch`。上游把后四个塞在两个文件里；本仓按类型拆文件，只是组织差异。

### 修改

| 文件 | 改了什么 |
|---|---|
| `PlayerViewModel.cs` | `InitializeCoreAsync` 开头调 `BeginPlaybackDiagnosticsSession()`；`DisposeAsync` 里 `#if DEBUG EndPlaybackDiagnosticsSession()` |
| `PlayerViewModel.Methods.cs` | `OnPlayerPropertyChanged` 顶部 `#if DEBUG ObservePlaybackForStall(e.PropertyName)`；`Position` 分支里 `Danmaku.UpdatePosition(...)` 在 Debug 下换成带计时的 `UpdateDanmakuPositionForStall(...)`；`OnClientCacheStateChanged` 里 `#if DEBUG` 通知 settling |
| `.gitignore` | 加 `StallDumps/` 与 `stall-*.dmp` |

### 刻意没做的事

- 没有新增任何 mpv 查询（本仓内核没有同步属性读取，见分析文档第 12 节）。
- 没有重构播放器、没有升级任何包、没有改播放行为。
- 没有加 `GC.Collect`。
- 没有为了「让竞态不出现」而加的 `Sleep`；唯一的时间等待是监视线程 10ms 的**采样节拍**（等价于上游的 `Thread.Sleep(10)`），它不参与任何同步逻辑。
- 没有延长任何持锁范围。

## 2. 触发条件

一次 dump 需要**同时**满足下面全部条件。

### 2.1 先判出「停滞候选」——两种类型之一

| stallType | 条件 | 覆盖的卡顿 |
|---|---|---|
| `progress-read` | 一次状态读取 + 消费（`Danmaku.UpdatePosition` → 渲染层）的 `ReadId` 存在超过 **60ms** 未收尾 | UI 线程被弹幕渲染 / 布局拖住，进度与画面更新跟着卡 |
| `progress-gap` | `Playing == true` 且播放位置超过 **600ms** 没有前进 | mpv 内核不再推进、位置通知送不到（内核侧卡死） |

同一份停滞只判一次（`_seenReadId` / `_seenAdvanceId` 去重）。

### 2.2 再过四道抑制闸门

1. **GC / 监视线程被饿**：监视线程本轮醒来间隔 > 30ms，或这段里 GC 计数变了 → `SkippedGc`；
2. **不在播放**：`progress-read` 要求 `Playing`；`progress-gap` 本身就要求 `Playing` → `SkippedSettling`；
3. **稳定期**：距离最近一次 settling 通知不足 5 秒 → `SkippedSettling`；
4. **进程级限流**：180 秒内已有一次尝试，或已用满 5 次 → `SkippedRate`。

### 2.3 settling 通知来自哪些真实路径

| 来源 | 代码位置 |
|---|---|
| `PlaybackState` 变化（播放 / 暂停 / 缓冲 / 拖动 / 加载 / 停止） | `PlayerViewModel.Methods.cs` `OnPlayerPropertyChanged` |
| 缓冲状态变化（欠载 / 恢复） | `OnClientCacheStateChanged` |
| 新播放会话（换视频 / 重载 / 重建播放器） | `BeginPlaybackDiagnosticsSession`（新监视器自带稳定期） |

上游用的 `SeekStarted` / `SeamlessSwitchProgress` **在本仓的内核里不存在**，没有伪造同名事件；拖动由 `PlaybackState == Seeking` 覆盖。

### 2.4 不触发的情况（预期行为）

- 暂停期间：`Playing == false`，位置不动也不会判 `progress-gap`；
- 加载 / 切视频 / 拖动后 5 秒内：稳定期；
- 一次会话里已经抓过一次的同一处停滞：去重；
- 进程里已经写过 5 次 dump：限流；
- Release 构建：类型不存在，调用点被 `#if DEBUG` 编译掉。

## 3. 线程与生命周期

### 3.1 线程清单

| 线程 | 谁创建 | 存在时间 | 做什么 |
|---|---|---|---|
| `PlaybackStallWatch`（`IsBackground = true`） | `PlaybackStallWatch.Start()`，由会话开始时调用 | 一个播放会话 | 每 10ms 读一次不可变快照 → 判定 → 必要时写 dump；每分钟一行心跳 |

**没有任何一次跨线程调用进 UI Dispatcher**：监视线程只读 `PlaybackStallSnapshot`，不碰 `Player` / `Client` / `Danmaku` / XAML。

### 3.2 会话生命周期（跟随玩家）

```
InitializeCoreAsync（换视频 / 重载 / 重播 / 重建播放器）
  └─ BeginPlaybackDiagnosticsSession()
       ├─ sessionId = "s{LifecycleId}-{seq}-{UTC}"
       ├─ Danmaku.DiagnosticsSessionId = sessionId      // 与弹幕重建日志共享
       ├─ StopPlaybackStallWatch()                      // 先停旧的
       └─ StartPlaybackStallWatch()                     // 再起新的
PlayerViewModel.DisposeAsync（关窗 / 释放视图模型）
  └─ #if DEBUG EndPlaybackDiagnosticsSession()
       └─ StopPlaybackStallWatch()
```

- 每个会话一个新 `sessionId`，日志能按会话切分；旧监视器不会把记录写到新会话名下。
- 同一时刻**最多一个**监视器实例：`_stallWatch` 字段在 `Stop` 时立即置 null，再赋新实例前不会并存。
- 监视器**不持有** `MpvClient` / `MpvPlayer` 引用，所以「旧会话的监视器摸到已释放的 mpv」这条路径不存在。

### 3.3 停止是不是异步的

是。

- `Stop()` 只做 `_stopped = true`（`volatile`）就返回，**不 Join、不 `await`**。
- 监视线程在循环顶部与写成前各查一次 `_stopped`，最坏 10ms 后退出；退出前写一行 `STALL | watch-stopped …` 汇总。
- `PlayerViewModel.StopPlaybackStallWatch()` 调 `watch.Dispose()`（等价于 Stop），**不 await `Completion`**：切视频与 Dispose 都在 UI 线程上，等它就等于 Join。
- 线程是后台线程，进程直接退出也不会被它挂住。

### 3.4 关窗之后还会不会写 dump

- 停止之后**不会新开**一次 dump：写成前有 `if (_stopped) break;` 的二次确认。
- 唯一残留窗口是「已经进了 `MiniDumpWriteDump` 的那一次」——该调用不可中断，它会在几十毫秒内写完。所以准确说法是：**关闭后不会再开始新的 dump，已在写的那一个会写完**。
- 实测（见验证文档）：关窗后日志出现 `watch-stopped`，`LocalState\Diagnostics` 目录在整个运行期都没有生成 `.dmp`。

### 3.5 并发模型

- **发布侧**（属性变更回调，可能来自不同线程）：`PlaybackStallProbe` 内部一把小锁保护自己的可变字段，然后 `Volatile.Write` 发布一个**不可变快照对象**（`sealed record`）。分配量约 3 个小对象 / 次位置通知，Debug-only，实测 ~24 次/秒。
- **消费侧**（监视线程）：`Volatile.Read(ref _current)` 一次拿到完整快照，**不加锁**，也不可能看到半更新的字段。
- **限流器**：`lock`，进程级单例。失败尝试也占名额。
- **日志**：经 `IPlaybackStallLog` 抽象出去，桌面端用 Serilog 实现；抽出来同时也是为了让判定逻辑能进单元测试（不依赖日志包）。

> 与上游的差别：上游用「字段 + id 复核」的近似无锁读（见分析文档 9.5 的良性竞态）；本仓改成不可变快照，把这类交错从结构上消掉。这是本仓唯一在并发模型上主动偏离上游的地方，理由是它更简单且不会有混合快照。

## 4. 存储、保留与清理

- **目录**：`%LOCALAPPDATA%\Packages\Richasy.BiliCopilot.Dev_g7ew4fxhv1f46\LocalState\Diagnostics\StallDumps\`
  （代码里取 `Microsoft.Windows.Storage.ApplicationData.GetDefault().LocalFolder` + `Diagnostics\StallDumps`；拿不到 LocalFolder 时退到 `%TEMP%\BiliCopilot\Diagnostics\StallDumps`。）
  **不落下载目录、不落网络盘。**
- **命名**：`stall-<UTC yyyyMMddTHHmmss.fffffffZ>.dmp`，侧车同名 `.txt`。
- **保留**：最多 **10 个**，且总量不超过 **200MB**，超出按**最旧**删；删 `.dmp` 时连同名 `.txt` 一起删。
- **失败清理**：写失败时删除半成品 + 侧车，不留残渣、不占名额之外的空间。
- **清理入口**：
  - 代码：`PlaybackStallDump.Cleanup(dir)`，以及 `PlayerViewModel.CleanupPlaybackStallDumps()`。
  - 手动（PowerShell）：
    ```powershell
    $d = "$env:LOCALAPPDATA\Packages\Richasy.BiliCopilot.Dev_g7ew4fxhv1f46\LocalState\Diagnostics\StallDumps"
    Remove-Item "$d\stall-*.dmp","$d\stall-*.txt" -Force -ErrorAction SilentlyContinue
    ```
- 目录里会自动写一个内容为 `*` 的 `.gitignore`：万一有人把 dump 目录指进仓库，dump 也不会被误提交。
  > 顺带记一个踩过的坑：仓库根 `.gitignore` 一开始写的是 `Diagnostics/`，它会匹配**任何**同名目录，把源码目录 `src/Desktop/BiliCopilot.UI/Services/Diagnostics/` 也一起忽略了（`git status` 里直接看不到新文件）。已改成精确规则 `StallDumps/` + `stall-*.dmp`。

## 5. 安全与隐私

**dump 里有什么**：本进程的内存镜像与全部线程栈。明文可能包含：

- B 站 Cookie（`SESSDATA` / `bili_jct` / `DedeUserID` 等）、access_token；
- 带鉴权参数的完整播放/接口 URL；
- 账号昵称、mid 等个人标识。

因此：

1. **只写本地**。不上传、不自动附 Issue、不写公开仓库 —— 代码里没有任何网络调用。
2. **目录内有 `.gitignore`（内容 `*`）**，仓库根 `.gitignore` 也加了 `StallDumps/` 与 `stall-*.dmp`。
3. **侧车 `.txt` 与日志都不落凭据**：侧车只写 `session / stallType / observed-ms / playing / position / read-ms / advance-ms / publish-ms / gc / tick`，并附一行中文隐私告示；日志字段见下节，**从不打印 URL、Cookie、Token**（本仓代码里没有任何地方把播放地址写进停滞日志）。
4. **dump 本身刻意不含完整堆**（不加 `MiniDumpWithFullMemory`）：体积从几百 MB 降到实测 ~180KB，也少暴露一份全堆。
5. 需要把 dump 发给别人时，必须先想清楚里面有什么 —— 这一条也写在侧车文件里。

## 6. 日志字段

每行都带 `session={Session}`，按下表分三类。

### 6.1 会话起止

```
STALL | watch-started session={Session} dir={Directory}
STALL | watch-stopped session={Session} stalls={Stalls} dumps={Dumps} skipped-gc={SkippedGc}
        skipped-settling={SkippedSettling} skipped-rate={SkippedRate} attempts={Attempts} remaining={Remaining}
```

### 6.2 心跳（每 60 秒一行）

```
STALL | heartbeat session={Session} publishes={Publishes} publish-gap-ms={PublishGapMs} playing={Playing}
        pos={Position} stalls={Stalls} dumps={Dumps} skipped-gc=… skipped-settling=… skipped-rate=…
        attempts={Attempts} remaining={Remaining} tick={Tick}
```

心跳不是刷日志用的，是让**「这一场没有停滞」**和**「监视器根本没跑」**在日志里能分开——没有它，`stalls=0` 无法自证。

### 6.3 判定结果

```
STALL | skipped session={Session} type={Type} reason={Reason} observed-ms={ObservedMs} playing={Playing}
        pos={Position} gc0={Gc0} attempts={Attempts} remaining={Remaining} tick={Tick}
        // reason ∈ gc-or-wake-gap / settling-or-not-playing / rate-limited

STALL | dump session={Session} type={Type} observed-ms={ObservedMs} read-ms={ReadMs} publish-ms={PublishMs}
        playing={Playing} pos={Position} gc0={Gc0} gc1={Gc1} gc2={Gc2} attempts={Attempts} remaining={Remaining}
        file={File} bytes={Bytes} write-ms={WriteMs} tick={Tick} utc={Utc}

STALL | dump-failed session={Session} type={Type} observed-ms={ObservedMs} playing={Playing} pos={Position}
        gc0={Gc0} gc1={Gc1} gc2={Gc2} error={Error}
```

字段覆盖了要求里的全部项：sessionId（`session`）、stallType（`type`）、状态读取耗时（`read-ms` / `observed-ms`）、播放状态（`playing`）、播放位置（`pos`）、GC 计数变化（`gc0/gc1/gc2`）、跳过原因（`reason`）、dump 文件名 / 大小 / 写入耗时（`file` / `bytes` / `write-ms`）、失败原因（`error`）。

为交叉验证还额外带了：`tick`（`Stopwatch.GetTimestamp()`，进程内单调）、`utc`（ISO-8601 UTC）。弹幕重建日志带同一组 `tick` / `utc` / `session`，两条日志因此可以对时间轴。

## 7. Debug 隔离与 Release 行为

- 五个诊断类型、`PlayerViewModel.Diagnostics.cs` 里除 `BeginPlaybackDiagnosticsSession` 之外的全部内容、以及三处调用点（`EndPlaybackDiagnosticsSession`、`ObservePlaybackForStall`、`UpdateDanmakuPositionForStall`）都在 `#if DEBUG` 内。
- 会话 id 本身不在 `#if DEBUG` 里：弹幕重建计时（不做 Debug 隔离）需要它才能和停滞日志对照。它只是一个字符串，不建线程、不落盘。
- **Release 里 `Danmaku.UpdatePosition(Convert.ToInt32(Player.Position))` 保持原样**，行为与改动前逐字节一致。
- Release 产物验证：`grep`/字节检索确认 `PlaybackStall*` 类型名、`MiniDumpWriteDump`、`STALL |` 全部不存在；Release 包实跑日志 0 条 `STALL` 行。详见验证文档。

## 8. 已知局限

1. **`progress-gap` 的时间基准是内核的位置通知**。如果内核连「位置没变」都不通知、且播放状态也没变化，探针拿不到新发布，`advanceAt` 就停在那里 —— 这恰好是我们要抓的情况，判据成立；但如果通知线程整体被冻住（连 `PlaybackState` 都不再变），判定同样成立。反之，如果应用自己把位置通知全丢了，会误判成 `progress-gap`——这也是真问题，值得抓。
2. **native 栈只有符号名级**：本仓不带 libmpv/Win2D 的 PDB，dump 里原生帧只能看到模块 + 偏移。
3. **一次已在写盘中的 dump 无法中断**（DbgHelp 限制）。
4. **60ms 阈值在极低端机器上可能偏紧**：实测正常通知间隔 ~20–24ms，60ms 是 2.5–3 倍。若现场发现误报，改 `PlaybackStallPolicy.ReadStallThresholdMilliseconds` 一个常量即可。
5. **没有做「只采一次、然后同会话静默」的开关**：目前靠进程级 5 次上限 + 180 秒间隔。若一次会话里出现多种停滞，会用掉多个名额。
