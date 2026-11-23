# 上游诊断能力分析（BiliFluent 8222b78 / 9abb6cd）

本文只回答两件事：上游那两个提交具体做了什么、它们的边界与坑在哪，以及这些内容移植到本仓时哪些前提**不成立**。所有结论来自实际读取的源码与 `git show` 输出，不是推测；涉及没验证到的地方会直接标注。

- 上游仓库：`https://github.com/Better-Native-APPs/BiliFluent`（克隆到 `E:\build-temp-dsh\BiliFluent`，`--depth 120`）
- 分析对象：
  - **A** `8222b78` `feat(player): capture debug playback stall dumps`（3 文件，+420 / -3）
  - **B** `9abb6cd` `chore(danmaku): log track rebuild timings`（1 文件，+49 / -12）

## 0. 后继修复检查（先做，避免照抄已修的 bug）

```
git log -S"PlaybackStall" --oneline --all     → 只有 8222b78
git log --oneline -- '*PlaybackStall*'        → 只有 8222b78
git log --oneline -- '*MpvAssDanmakuRenderer*'→ 9abb6cd 与更早 3 个提交，没有 9abb6cd 之后的修复
git log -S"stall-dumps" --oneline --all       → 只有 8222b78
```

**结论：两个提交之后都没有针对它们的修补提交。** 上游这两处代码是「一次性落地、之后未再改动」的状态，所以本文分析的就是上游最终形态，没有「后来的修法可以参考」这一层。

## 1. 提交 A 的结构

上游把五个类型压在**两个文件**里：

| 文件 | 内容 |
|---|---|
| `ViewModels/Core/PlayerViewModel/PlaybackStallDump.cs`（85 行） | `PlaybackStallDumpResult`、`PlaybackStallDump`（P/Invoke + 落盘 + 清理） |
| `ViewModels/Core/PlayerViewModel/PlaybackStallWatch.cs`（256 行） | `PlaybackStallGcCounts`、`PlaybackStallRead`、`PlaybackStallProbe`、`PlaybackStallDecision`、`PlaybackStallDumpBudget`、`PlaybackStallPolicy`、`PlaybackStallWatch` |
| `ViewModels/Core/PlayerViewModel/PlayerViewModel.Diagnostics.cs`（+82/-3） | 探针接线、settling 事件钩子、诊断会话生命周期 |

整份代码在 `#if DEBUG` 里，`Release` 连类型都不存在。

## 2. P/Invoke：声明与参数是否规范

上游的声明（原样）：

```csharp
[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
[DllImport("kernel32.dll", ExactSpelling = true)]
private static extern nint GetCurrentProcess();

[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
[DllImport("dbghelp.dll", ExactSpelling = true, SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
private static extern bool MiniDumpWriteDump(nint process, uint processId, SafeFileHandle file, uint dumpType,
    nint exception, nint userStreams, nint callback);
```

逐条判断：

- **`DefaultDllImportSearchPaths(System32)`** —— 规范做法。`dbghelp.dll` 是系统 DLL，限定只在 System32 找，避免应用目录里被放一个同名 DLL 劫持。保留。
- **`ExactSpelling = true` + `SetLastError = true`** —— 正确。`dbghelp.dll` 导出的就是 `MiniDumpWriteDump`（无 `A`/`W` 后缀），`ExactSpelling` 防止运行时去拼 `MiniDumpWriteDumpW`；`SetLastError` 让失败时 `Marshal.GetLastPInvokeError()` 拿得到 NTSTATUS/HRESULT。
- **`[return: MarshalAs(UnmanagedType.Bool)]`** —— 正确。Win32 `BOOL` 是 4 字节 `int`，C# `bool` 默认 marshalling 只有 4 字节，但显式标注更稳（防止某些场景被当成 1 字节）。
- **`SafeFileHandle` 作为文件句柄** —— 正确且比 `IntPtr` 好：`using (var file = new FileStream(...))` 保证 `MiniDumpWriteDump` 返回后句柄立即关闭，异常路径也不会泄漏句柄。
- **参数顺序与类型** —— 与 `minidumpapiset.h` 一致：`(HANDLE hProcess, DWORD ProcessId, HANDLE hFile, MINIDUMP_TYPE DumpType, PMINIDUMP_EXCEPTION_INFORMATION, PMINIDUMP_USER_STREAM_INFORMATION, PMINIDUMP_CALLBACK_INFORMATION)`。三个可选指针都传 0，对「非崩溃现场、主动采样」是正确的。
- **`hProcess` / `hProcessId` 的一致性（SEQPID 那件事）** —— `MiniDumpWriteDump` 会用 `ProcessId` 给 dump 盖上被转储进程的 ID，并在两者不一致时返回失败（`ERROR_INVALID_PARAMETER`）。上游用 `GetCurrentProcess()`（伪句柄 `-1`，隐式拥有全部权限）+ `(uint)Environment.ProcessId`，两者必然指向同一进程，**是规范写法**。反过来（伪句柄 + 随便一个 PID）会直接失败。
- **flag：`DumpFlags = 0x00001000`** —— 注释写的是 `MiniDumpNormal | MiniDumpWithThreadInfo`，看起来像漏了 `MiniDumpNormal`，其实不是：`MiniDumpNormal` 的值就是 `0`，所以 OR 的结果就是 `0x1000`。注释与代码一致。

### `MiniDumpWithThreadInfo` 是否足够出线程栈

结论：**对托管栈够用，对原生栈只到「能看帧、看不到局部变量」的程度。** 依据分两层：

- 语义层：`MiniDumpNormal`（0）本身就包含每个线程的**栈内存范围**、模块列表、TLS；`MiniDumpWithThreadInfo`（0x1000）在此之上补 TEB 与线程时间。两者合起来足以让 CLR 的 DAC 走完所有线程的托管栈——这正是抓「卡在哪个托管方法上」需要的。
- 实测层（本任务里对**同样的 flag** 做过验证）：用本仓移植后的 `PlaybackStallDump` 写出的 183,573 字节 dump，`dotnet-dump analyze <dmp> -c "clrstack -all"` 能把每个 OS 线程列出来并解析到**源码行**，见 `PLAYBACK_STALL_VALIDATION.md` 第 6 节。

刻意**没有**加的 flag 与代价：

| 未加 | 少拿到什么 |
|---|---|
| `MiniDumpWithFullMemory`（0x2） | 整个堆。加了会从 ~180KB 涨到几百 MB，而且 Cookie / Token / 完整 URL 会原样落盘 —— 对「诊断卡顿」这个目的没有增量价值。 |
| `MiniDumpWithProcessThreadData`（0x100） | 更完整的线程环境块。`MiniDumpNormal` 已经含栈，托管栈验证通过，故不加。 |
| `MiniDumpWithIndirectlyReferencedMemory`（0x40） | 栈上指针指向的内存片段。原生帧（libmpv / Win2D）的局部变量会难读一些。**这是已知局限**：本仓没有 libmpv 的 PDB，原生帧本来就只能看符号名。 |

## 3. 落盘：后台线程、DbgHelp 串行化、失败清理、保留策略

- **写在哪里**：上游在**监视线程自己**上调用 `TryWrite`，没有再开一个线程。所谓「后台」是指监视线程本身是后台线程（`IsBackground = true`，名字 `PlaybackStallWatch`），不是 UI 线程、也不是线程池。
- **DbgHelp 串行化**：`private static readonly object DbgHelpSync`，`lock (DbgHelpSync)` 包住**整个** TryWrite（建目录、写文件、prune）。这是必须的：`dbghelp.dll` 全部函数单线程实现，并发调用会踩内存。锁是 `static`，所以跨会话也串行。
- **失败清理**：`created` 标志 + `catch` 里 `File.Delete(path)`（并吞掉 `IOException` / `UnauthorizedAccessException`）。`FileMode.CreateNew` 保证不会覆盖已有 dump；名字冲突时在建文件阶段就抛，`created` 仍为 false，不会误删别人的文件。
- **错误文本**：`Win32Exception` → `"0x" + NativeErrorCode.ToString("X8")`，其它异常只记类型名（不把消息里的路径/URL 带进日志）。
- **保留策略**：文件名 `stall-<UTC yyyyMMddTHHmmss.fffffffZ>.dmp`；`PruneDirectory` 按 `LastWriteTimeUtc` 升序，删到只剩 **10 个**。

上游这里有两个**明确的缺口**（本仓移植时补上了，见实现文档）：

1. **只有数量上限，没有总大小上限。** 10 个 dump 各自多大取决于进程规模；本仓实测单个 ~180KB，但带大量模块时会更大。本仓加了 200MB 总大小上限。
2. **`PruneDirectory` 里删 `.threads.txt` 侧车，但整个提交从来没写过这个侧车。** 属于残留的意图，不是 bug，但容易让人以为有线程摘要文件。本仓改成真的写一个 `.meta` 侧车（含 sessionId / 停滞类型 / 位置 / 计数，且不含任何凭据）。

## 4. Probe：怎么记录「一次状态读取」的起止

上游的读取点是 `ProbeFrames` 里的两次同步属性读：

```csharp
_core-idle → ReadProbeIdle → { _stallWatch.BeginRead(); try { _client.GetProperty("core-idle", out idle); } finally { _stallWatch.EndRead(); } }
time-pos  → ReadProbePosition → 同上
```

`PlaybackStallProbe` 的机制：

- `BeginRead()`：先取 GC 计数，再取 `_startedAt`，最后 `Volatile.Write(ref _pendingId, ++_nextId)` —— **把 id 放在最后发布**，读方不会看到「id 有了、起始时间还没写」的组合。
- `EndRead()`：`Volatile.Write(ref _pendingId, 0)`。
- `Read()`：先读 `_pendingId`，为 0 直接返回空；否则读其余字段，最后 `Interlocked.Read(ref _pendingId) == id` 复核，不一致就当作读空（这一轮不作数）。
- `Observe(playing, position, timestamp)`：把「上一次的播放状态 / 上一次读的时刻 / 上一次位置」记下来，供判定用。注意上游判定用的是 `read.WasPlaying`（**上一次**的播放状态），因为「当前这次读」正卡着，它自报不了状态。

**语义要点**：上游所谓「一次状态读取」是**单次 `GetProperty` 调用**的耗时，不是「读两项」的耗时——两次读各有自己的 Begin/End，所以一次停滞只会挂在一个 id 上。

## 5. 监控线程怎么判超时

`PlaybackStallWatch.Watch()`：

```
previousWakeAt = now
while (!_stopped):
    Thread.Sleep(10)
    if (_stopped) break
    ts = now
    read = _probe.Read()
    decision = _policy.Observe(ts, previousWakeAt, read, GC.Read())
    previousWakeAt = ts
    if (decision != Dump || _stopped || !_probe.IsPending(read.Id)) continue
    TryWrite(...)
```

判定条件（`PlaybackStallPolicy.Observe`）按顺序：

1. `read.Id == 0` → 没有在飞的读，跳过；
2. `ts - read.StartedAt < 60ms` → 还没超阈值，跳过；
3. `read.Id == _seenId` → **同一次读只判一次**（去重），跳过；
4. 否则 `_seenId = read.Id; Stalls++`，进入抑制闸门。

所以「超时」= 同一个 `_pendingId` 在 10ms 轮询里**持续存在超过 60ms**。这个设计的好处是：不需要在读的那一侧做任何计时判断，卡住的读自然会被外部看见。

## 6. Policy 怎么避免误判

四道闸门，缺一不可：

| 闸门 | 判据 | 挡掉什么 |
|---|---|---|
| GC / 线程被饿 | `ts - previousWakeAt > 30ms` **或** `read.Collections != 当前GC` | 这一段里发生过 GC，或者监视线程自己都没被及时调度 —— 耗时不能算在「读卡住」头上 |
| 不在播放 | `!read.WasPlaying` | 暂停时的读本来就可能慢（mpv 空闲），不算卡顿 |
| 上一次读太久远 | `ts - read.PreviousReadAt > 100ms` | 探针早已停摆（比如会话刚起、或通知断了），这个区间不描述稳态播放 |
| 稳定期 | `ts - _settlingAt < 5s` | 加载、拖动、切视频、暂停恢复之后的 5 秒内一律不采 |

`_settlingAt` 由 `NotifySettling(ts)` 用 CAS 单调推进（只增大不减小，UI 线程与其它线程并发调用安全）。上游挂的 settling 来源是 **`ReachFileLoading` / `ReachFileLoaded` / `SeekStarted` / `SeamlessSwitchProgress` 四个事件 + 心跳里检测到「从暂停恢复」**。

去重（`_seenId`）与限流分工不同：前者保证「一次停滞只判定一次」，后者保证「整台机器别写出太多 dump」。

## 7. 限流是不是进程级

是。`private static readonly PlaybackStallDumpBudget ProcessBudget = new(Stopwatch.Frequency);`，`PlaybackStallWatch` 的每个实例都引用它。所以换视频、重载播放器、重建 mpv 都**不会**重置名额。

参数：`_minimumInterval = frequency * 180`（180 秒）、`_attempts >= 5` 停止；`_attempts != 0 && ts - _lastAttemptAt < _minimumInterval` 拒绝。时间口径是 `Stopwatch` tick，不是墙钟——改系统时间不会放开限流。

一个设计选择值得记下：**失败的尝试也占用名额**。上游注释写得很直白——诊断不会重试同一次停滞，否则「写不出 dump」会变成每 10ms 一次的写盘风暴。

## 8. 监控线程怎么停止、怎么释放

- `Stop()`：`_stopped = true`（`volatile`）。不 Join、不唤醒、不等。
- 线程在 `while` 顶部与写成前各检查一次 `_stopped`，最坏 10ms 后退出；`finally` 里写一行 `stall-watch stalls=… dumps=… skipped-…` 汇总，然后 `_finished.TrySetResult()`。
- 释放：`PlaybackDiagnosticsSession.DisposeAsync()` 里 `await Task.WhenAll(_worker, _frameProbe, snapshots, _stallWatch.Completion)` —— 是 `await`，不是 `Join`，UI 线程不会被卡住；但**会话级的 Dispose 确实会等监视线程退出**，这保证了「会话 dispose 完成之后不会再有 dump」。
- 线程是 `IsBackground = true`：进程直接退出时由运行时回收，不会挂住关闭流程。

## 9. `PlayerViewModel.Diagnostics.cs`：四个关键问题

### 9.1 状态读发生在哪个线程

`ProbeFrames` 是被显式丢到**专用长任务线程**上的：

```csharp
_frameProbe = Task.Factory.StartNew(ProbeFrames, CancellationToken.None,
    TaskCreationOptions.LongRunning, TaskScheduler.Default);
```

注释写明理由：「a busy thread pool must not look like a frame gap」。这是上游这份实现里最关键的一个正确决定——如果读跑在线程池上，池子一忙就会把「读变慢」误判成 mpv 卡住。因此读**不在 UI 线程**，也不在线程池。

### 9.2 会话生命周期

`RefreshPlaybackDiagnostics` 是 UI 线程心跳里的入口：

- 关掉开关或正在关闭 → `SuspendPlaybackDiagnosticsAsync()`；
- `Client` 换了 → `DetachPlaybackDiagnostics()` 并重新挂 `ReachFileLoading/ReachFileEnd/Shutdown`；
- `_playbackDiagnostics is null` 且播放状态属于 `Playing/Paused/Buffering/Seeking` → `StartPlaybackDiagnostics(client)`。

`OnDiagnosticsFileLoading` 起会话、`OnDiagnosticsFileEnd`（文件结束 / Shutdown）停会话，两者都用 `_mediaVersion` 做「还是不是当前这一次播放」的校验，并 `queue.TryEnqueue` 回 UI 线程执行。

### 9.3 会不会重复启动

不会重复「并存」，但会重复「创建」：`StartPlaybackDiagnostics` 第一行就是 `_ = StopPlaybackDiagnosticsAsync();`，而 `StopPlaybackDiagnosticsAsync` 把旧会话的 `DisposeAsync` 链接到 `_playbackDiagnosticsDrain` 上**串行等待**（`Task.WhenAll(_playbackDiagnosticsDrain, session.DisposeAsync())`）。所以同一时刻只有一个活跃会话，且所有退役会话的 worker 都会被保留到真正停下来为止。

### 9.4 旧监视器会不会摸到已释放的 mpv / 关闭后会不会还 Dump

- 监视线程本身**不持有** `MpvClient`/`MpvPlayer` 的任何引用，只读 `PlaybackStallProbe` 的字段。所以它没有「摸到已释放 mpv」的路径。
- 会摸 mpv 的是 `ProbeFrames`（它读 `_client`），而 `ProbeFrames` 属于会话，会话 `DisposeAsync` 会 `await` 它退出。上游 `_stopped` 标志贯穿整个读循环。
- 「关闭后还 Dump」：`Stop()` 之后线程在写成前再检查一次 `_stopped`；而会话 `DisposeAsync` 会等线程退出。**唯一残留窗口**是一次已经在 `MiniDumpWriteDump` 里的写（不可中断），它会在 dispose 完成前写完，因此不会出现「进程都拆完了还在写盘」的情况。

### 9.5 一个遗留的良性竞态（记录，不修）

`PlaybackStallProbe.Read()` 用「先读 id、读字段、再复核 id」的方式取快照。写侧 `BeginRead` 是「先写字段、最后发布 id」，`EndRead` 直接写 0。理论上存在这种交错：读方拿到 id=N、开始读字段；写方 `EndRead`（id=0）然后 `BeginRead`（id=N+1）并改写字段；读方拿到 id=N 与 N+1 的字段混合，最后复核时 `_pendingId == N+1 ≠ N` → **读方丢弃这个快照**。所以最终结果仍然安全，只是可能多丢一帧样本。移植时本仓改成了「发布不可变快照对象 + `Volatile.Write`」，从结构上消除这类交错。

## 10. 上游参考参数的评估

上游给的是 60ms / 10ms / 5s / 180s / 最多 5 次。这些数字**不能直接搬**，因为它们的含义绑定在「一次同步 mpv 属性读」上。本仓的实测通知节奏是每 60 秒 1437～2937 次位置发布，即 **~20–24ms 一次**（见验证文档的心跳数据）。据此：

| 参数 | 上游值 | 本仓取值 | 理由 |
|---|---|---|---|
| 读取停滞阈值 | 60ms | **60ms（保留）** | 一次「状态读取 + 消费」超过 60ms ≈ 正常通知间隔的 2.5–3 倍，用户已能感知 UI 卡顿；这个数字在两种架构下语义都可解释，没必要改。 |
| 轮询间隔 | 10ms | **10ms（保留）** | 分辨率足够（阈值 60ms 的 1/6），自身开销可忽略。 |
| 稳定期 | 5s | **5s（保留）** | 本仓 mpv 初始化实测 8.2 秒（`PERF Load.MpvInit`），5 秒稳定期不能覆盖初始化，但初始化期间 `Playing=false` 已经挡掉了；5 秒足够覆盖 seek/切清晰度后的状态转换。 |
| 限流间隔 | 180s | **180s（保留）** | 抓的是偶发卡顿，180 秒里最多 5 次已经能覆盖一次排查。 |
| 最多次数 | 5 | **5（保留）** | 同上。 |
| 位置停滞阈值 | 无 | **600ms（新增）** | 本仓没有同步读路径可复用，只能靠「位置多久没前进」。600ms ≈ 正常间隔的 25–30 倍，正常播放不可能触发；而 600ms 的画面冻住用户已经能看见。 |
| 上一读最大年龄 | 100ms | **不采用** | 本仓没有「上一次同步读」，改为以「最后一次位置发布」为基准，并用心跳里的 `publish-gap-ms` 观测它。 |

## 11. 提交 B（9abb6cd）分析

改动全在 `Controls/Danmaku/MpvAssDanmakuRenderer.cs`（上游**自有**的 mpv ASS 弹幕实现）：

- `ScheduleBuild(int delay)` → `ScheduleBuild(int delay, string reason)`，7 个调用点补上原因：`items` / `ready` / `style` / `realtime` / `ready` / `ready`。
- 在 `_native.Gate` 之外量 `build-ms`（`MpvAssDanmakuDocument.Build`，纯 CPU）；
- `sub-add` 量成 `add-ms`，`secondary-sub-visibility` + `secondary-sid` 量成 `select-ms`，`RemoveTrack(previous)` 量成 `remove-ms`；
- `CommitEmptyAsync` 改成返回 `double?`（删旧轨的耗时），并把「清空」这条路径也写进日志；
- 日志一行：`Danmaku | track built events={Events} bytes={Bytes} build-ms add-ms select-ms remove-ms reason`。

上游漏了两项（本仓移植时补的）：

1. **`write-ms` 根本没量** —— ASS 文件落盘（`File.WriteAllTextAsync`）是这段里唯一真正碰 IO 的地方，恰恰最容易慢，上游没记。
2. **没有 `total-ms`** —— 四个分段相加不等于端到端（中间还有锁等待、`Task.Delay`、调度延迟、`FindTrack` 的属性读）。分段和冒充端到端会低估真实耗时。

另外 `bytes` 用 `new FileInfo(file).Length` 取，紧跟在写完文件之后，是对的。

## 12. 移植前提：本仓与上游的四处硬差异

| 维度 | 上游 | 本仓 | 影响 |
|---|---|---|---|
| mpv 内核 | 仓内自研 layer，有同步 `GetProperty`、`SeekStarted`、`SeamlessSwitchProgress` | `Richasy.MpvKernel 0.0.2-preview2`（NuGet，无源码）：**没有** `GetProperty`，位置/状态由 `MpvPlayer.PropertyChanged` 推 | 探针无法包住「同步读」，只能包住「通知的消费」；`SeekStarted`/`SeamlessSwitchProgress` 不存在，settling 换用 `PlaybackState` 变化 + `CacheStateChanged` + 文件事件 |
| 播放器诊断 partial | 有 `PlayerViewModel.Diagnostics.cs` | **没有**，但有 `PerfProbe` / `PlayerLifecycle` / `PerfWidgetMountTrace` / `MpvStatsProbe` / `MpvLogWatcher` | 新建 partial，复用现有探针而不重造 |
| 弹幕 | mpv ASS 字幕轨（`sub-add` / `secondary-sid` / 临时 `.ass`） | Richasy.Danmaku 的 `DanmakuFrostMaster`(Win2D) / `DanmakuRenderer`(DirectX)，进程内绘制。**全仓 grep `sub-add` / `secondary-sid` / `MpvAss` 均为 0 命中** | 上游 B 的 `add-ms` / `remove-ms` 在本仓**没有对应动作**，只能按真实流程（build/write/select/style/resume）打点 |
| 崩溃采样 | 无 | 无自采（`%LOCALAPPDATA%\CrashDumps` 是 WER 的） | 从零建 |

结论：**不能 cherry-pick，也不能照抄文件名**。上游两份代码里，可以原样保留的是「判定/限流/落盘的骨架」；必须重写的是「探针的挂载点」和「弹幕的分段定义」。
