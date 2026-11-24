# .NET 11 播放位置回调链（TickPath）改造与实测

> 分支 `exp/net11-optimization`（未 push）· 提交 `69785b5`（位置回调）+ `08c0c0a`（窗口切换）· 2026-11-24
> 只改这两个文件 + 本文档，未动 `CheckSponsorBlock`、未动 79 个 `async void`、未动 Reactor 组件

## 1. 范围 A：位置回调降成 O(1) 纯内存

`ViewModels/Core/PlayerViewModel/PlayerViewModel.Methods.cs`，位置分支现在是 777–898 行。

| 改前（每个 tick） | 改后 | 代码位置 |
|---|---|---|
| `SettingsToolkit.ReadLocalSetting(PlayNextWithoutTip)` | 读字段 `_isPlayNextWithoutTip` | 807 行（字段在 1058 行读入） |
| `ReadLocalSetting(AutoPlayNext)` | `_isAutoPlayNext` | 829 / 1059 |
| `ReadLocalSetting(IsPlayerSpeedShare)` | `_isPlayerSpeedShare` | 947 / 1060 |
| `ReadLocalSetting(SubtitleFontSize)` | `_subtitleFontSize` | 960 / 1061 |
| `TimeSpan.FromSeconds(Position).ToString(@"hh\:mm\:ss")` 每 tick 一次 | 整秒值变了才格式化 | 784–791 |
| `foreach (var chapter in Chapters)` 全量遍历 + 每章写两个属性 | `UpdateChapterPlaybackState(position)`：从上次命中索引出发挪动、只写变化项 | 802 / 1075 起 |

为什么这一步能去掉 WinRT 往返：`SettingsToolkit.ReadLocalSetting` 是
`GlobalDependencies.Kernel.GetRequiredService<ISettingsToolkit>()` + `ApplicationData.LocalSettings` 容器读
（`Toolkits/SettingsToolkit.cs:22`，中间没有任何缓存层），而这四项设置的语义是「每个视频一个值」。
`InitializeInternalAsync` 每个视频都会走一次（`InitializeCoreAsync` 调它），所以在那里一次性读进
`RefreshTickPathCache()`（42 行调用，1053 行定义），位置回调只剩字段读与内存比较。
`SubtitleFontSize` 的即时生效路径不变——`ChangeSubtitleFontSizeCommand` 自己回写设置键。

章节高亮判定与原实现逐字一致：`IsPlayed = Position ≤ 播放位置`、`IsPlaying = |Position − 播放位置| < 2`
（`UpdateChapterPlaybackState` 注释里写明），只是不再每 tick 全量重算重发通知。

## 2. 代码依据（可复核）

```powershell
# 位置分支里 WinRT 设置读取 = 0 处（整文件 21 处：13 处在初始化路径、4 处在 RefreshTickPathCache、1 处在注释、3 处在别的事件分支）
$lines = Get-Content src/Desktop/BiliCopilot.UI/ViewModels/Core/PlayerViewModel/PlayerViewModel.Methods.cs
($lines[776..897] | Select-String 'ReadLocalSetting').Count          # → 0
($lines[776..897] | Select-String 'foreach \(var chapter in Chapters\)').Count   # → 0
($lines[776..897] | Select-String 'TimeSpan\.FromSeconds').Count      # → 2（1 处代码 + 1 处注释）
```

## 3. 范围 B：窗口切换（`Forms/PlayerWindow.cs`）

**更正 t3 的定性**：原文说这是「UI 线程上的 `Task.Run(...).Wait()`」，实测不是。四处
（Enter/ExitFullScreen、Enter/ExitCompactOverlay）写的是
`_ = Task.Run(() => { Task.Delay(500).Wait(); _isPresenterChanging = false; })` —— `Wait()` 在池线程上跑，
不阻塞 UI 线程，也不会死锁。

真问题两个：① 每次切窗口模式白占一个池线程 500 ms；② `_isPresenterChanging`（`:31`，`IsPresenterChanging`
在 `:83` 暴露给 UI）由池线程写、UI 线程读，非 volatile 字段的可见性没保证，而这条线还挂着绑定。

改法：四段换成 `_ = ResetPresenterChangingFlagAsync()`（221/233/252/264 行），方法体是
`await Task.Delay(500)` → `this.Get<DispatcherQueue>()` 上判断 `HasThreadAccess`，直接写或 `TryEnqueue` 回 UI 线程写。
**时序不变**（仍 500 ms、仍 fire-and-forget，调用方不等），没有改成 `volatile`——可见性不是唯一问题，
UI 绑定也要在 UI 线程上失效。风险评估：低。四个方法的调用点（`PlayerViewModel.Commands.cs:122/126`、
`PlayerViewModel.cs:812/816`、`PlayerViewModel.Engine.cs:343-367`、`Methods.cs:589-610`）全是 `void` 同步调用，
没有调用方依赖 `_isPresenterChanging` 的写入线程；标志位清除时机仍是 500 ms 后。

## 4. 实测：UiLag 与 Responding

探针口径：`Services/PerfProbe.cs` 的 UiLag 探针（每 50 ms 往 `DispatcherQueue` 投一次任务、量排队延迟，
10 秒一个聚合窗，`PlayerViewModel.cs:158` 在进播放器时启动）。数据来自应用日志
`…\Packages\Richasy.BiliCopilot.Dev_g7ew4fxhv1f46\LocalState\Logger\log-2026-11-24.txt`。

**改后**（本次构建 = `69785b5` + `08c0c0a`，20:09:07 启动，20:10:16 `VideoReconfig` 开始播放）：

| 时间窗 | p50 | p95 |
|---|---|---|
| 20:11:06 / 20:11:16 / 20:11:26 | 0.17 / 0.18 / 0.14 ms | 1.84 / 1.81 / 1.54 ms |
| 20:11:36 / 20:11:46 / 20:11:56 | 0.12 / 0.12 / 0.14 ms | 1.10 / 1.10 / 1.51 ms |
| 20:12:06 | 0.14 ms | 2.16 ms |

播放期间 p95 **1.10–2.16 ms**（7 个窗口，中位 1.54 ms，其中 1 个窗口 2.16 ms 略超 2 ms），
`Responding` 每 10 秒采一次、10 次全部 `True`（20:10:16→20:12:06，进程 CPU 71.9→138.2 s）。
（20:10:06 那个窗口 p95=785 ms 是**进播放页那一下**，在 `VideoReconfig` 之前，不属于播放期。）

**改前**（同一份日志里 t6 之前那次会话，18:51:53 `VideoReconfig` 开始播放）：

| 时间窗 | p50 | p95 |
|---|---|---|
| 18:51:52 / 18:52:02 / 18:52:12 | 0.27 / 1.33 / 1.06 ms | 8.88 / **519.79** / 9.07 ms |
| 18:52:22 / 18:52:32 / 18:52:42 | 2.07 / 1.67 / 0.65 ms | 6.96 / 6.93 / 4.44 ms |
| 18:52:52 / 18:53:12 / 18:53:22 | 0.68 / 0.64 / 0.56 ms | 5.28 / 3.02 / 2.67 ms |
| 18:53:32 / 18:53:42 / 18:53:52 | 0.35 / 0.35 / 0.28 ms | 2.05 / 2.18 / 2.53 ms |

**这份对照不是受控 A/B**：两台时间点相隔 1 小时 20 分、视频不同（改前是首页进的另一个视频）、
机器负载不完全一样。它只能说明「改后这次播放期的 p95 落在 1.1–2.2 ms、p50 在 0.12–0.19 ms，
比改前那次会话（p50 0.28–2.07 ms、p95 2.05–9.07 ms，另有两个 >400 ms 的起步尖峰）低一个档」。

受控 A/B 这一轮没做成，失败原因留证：把两个文件回退到 `b41a978` 后两次构建都被环境打断——
第一次 `XamlCompiler error WMC0601`（并发构建把 `obj/…/win-x64/App.xaml` 中间产物打乱），
第二次 `MSB3021/MSB3027`（另一个进程正在运行 `BiliCopilot.UI`，锁住了 `_build/x64/Debug/.../bin`）。
工作区已恢复到 HEAD，没有留下回退版本。

## 5. 运行时复测（2026-11-24 21:1x–21:3x，t13）

这一节是把 t11 标「未验证」的几项补齐后的原始记录。两条前置事实先说清楚，否则数字没法读：

**(a) 部署目录里的 bin 曾是一份 AOT 产物**：`src/_build/x64/Debug/…/bin` 里 20:41 那次构建的
`runtimeconfig.json` 带着 **27 条**裁剪开关，其中 `System.Diagnostics.Tracing.EventSource.IsSupported=false`
——**EventSource 被关掉，EventCounter / EventPipe 就采不到任何东西**。这正是 t11 §7「dotnet-counters
对应用进程采 30 s / 10 s / 6 s 都是 0 样本、只有 CSV 表头」的根因，不是工具坏了（对 MSBuild 能采到）。
用当前 HEAD 重建（`dotnet build … -c Debug -p:Platform=x64`，默认输出到上面那个 bin，MSIX 松散注册直接指向它）
后 runtimeconfig 只剩 **4 条**、`EventSource.IsSupported` 不再出现，counters 随即正常（见 5.3）。

> 重建时会遇到 `PRI210: 0x800704c8 - File move failed … to …\bin`：这是 bin 里旧的 `resources.pri`
> 卡住了（新 PRI 移不进去）。把旧文件改名成 `resources.pri.bak` 再构建即可，实测改名后 **EXIT=0**，
> 新的 `resources.pri` 正常写出。这条路是构建环境问题，与源码无关。

**(b) 登录态**：20:36 那次重注册把 `Settings` 轮换成 `Settings-broken-203705` 等快照，应用丢了 token
（日志里「没有本地缓存的令牌信息，请重新登录」），这是 t11 播放项跑不了的直接原因。本轮用应用自己的快照恢复
（`Settings-broken-203705\settings.dat` → `Settings\settings.dat`，重启后动态页与播放恢复）；只改了本机应用数据，
没有进仓库。

### 5.1 冷启动（到主窗口出现）

| 第几次 | 耗时 | Responding | 窗口标题 |
|---|---|---|---|
| 1 | 3200 ms | True | 哔哩助理 |
| 2 | 3155 ms | True | 哔哩助理 |
| 3 | 3422 ms | True | 哔哩助理 |
| **中位数** | **3200 ms** | | |

（另有一轮 30 轮开关循环里的 30 次启动样本，中位 3137.5 ms，范围 2361–4223 ms。）

### 5.2 播放 ≥60 秒

`VideoReconfig` 21:31:43 起播，跑到 21:33:30（107 秒）。每 10 秒采一次 `Responding`：**6/6 全 True**
（进程 CPU 53.2→64.3 s，工作集 565–580 MB）。播放期 UiLag 窗口：

```
21:31:50 p50=0.09ms p95=0.20ms  21:32:20 p50=0.09ms p95=0.26ms  21:32:50 p50=0.08ms p95=0.18ms
21:32:00 p50=0.09ms p95=0.20ms  21:32:30 p50=0.08ms p95=0.22ms  21:33:00 p50=0.08ms p95=0.19ms
21:32:10 p50=0.09ms p95=0.23ms  21:32:40 p50=0.08ms p95=0.21ms  21:33:10 p50=0.09ms p95=0.19ms
```

播放期 **p50 0.08–0.09 ms、p95 0.18–0.26 ms**，全部远低于 2 ms 门槛。
（21:31:20 那个 p95=211 ms 的窗口是**进播放页那一下**，在 `VideoReconfig` 之前。）

### 5.3 dotnet-counters（播放中 30 秒）

```powershell
dotnet-counters collect -p <BiliCopilot.UI pid> `
  --counters "System.Runtime[gc-heap-size,alloc-rate,gen-0-gc-count,gen-1-gc-count,gen-2-gc-count,time-in-gc,working-set]" `
  -o E:\build-temp-dsh\verifier\results\counters-play-fresh.csv --duration 00:00:30
```

结果：**210 行数据（30 个采样点 × 7 个计数器）**，原始片段：

```
11/24/2026 21:31:38,System.Runtime,Working Set (MB),Metric,421.257216
11/24/2026 21:31:38,System.Runtime,GC Heap Size (MB),Metric,16.587184
11/24/2026 21:31:38,System.Runtime,Gen 0 GC Count (Count / 1 sec),Rate,0
11/24/2026 21:31:46,System.Runtime,Gen 1 GC Count (Count / 1 sec),Rate,1
```

| 计数器 | 范围 | 末值 |
|---|---|---|
| Working Set | 421 – 586 MB | 578 MB |
| GC Heap Size | 16.6 – 23.7 MB | 19.1 MB |
| Allocation Rate | 40 KB/s – 2.48 MB/s | 2.04 MB/s |
| Gen 0 / Gen 1 GC | 0–1 / 0–1 次每秒 | 0 |
| Gen 2 GC | **全程 0** | 0 |
| % Time in GC | 0 % | 0 % |

### 5.4 播放器开关循环

**仓库外的验证脚本 `toggle-player.ps1` 只能跑一轮**：它只在开头导航一次到搜索页，关闭播放器后应用停在
播放页（没有导航栏与搜索框），第 2 轮起必然 `search-unavailable`。实测 `-Rounds 2` 是 1/2 打开、exit 0。
所以 30 轮改用「每轮重启 → 动态页 → 点开视频 → 关进程」的循环（脚本在
`E:\build-temp-dsh\login-recovery\toggle30b.ps1`，结果 JSON 在
`E:\build-temp-dsh\verifier\results\toggle30-restart.json`）：

| 指标 | 结果 |
|---|---|
| 轮数 | 30 |
| 成功打开播放 | **30 / 30**，每次 `Responding=True`，打开中位 7.77 s |
| 成功关闭 | **30 / 30**，失败 **0** |
| 进程泄漏 | 跑前 UI=1 / Visor=1；跑后 UI=0 / Visor=0（无残留） |

## 6. 刻意没做的

- 79 个 `async void` 一个没动（天然不能转 runtime-async，见 t2 §4.1）。
- 章节 UI 的绑定与 `PlayerViewModel.Commands.cs` 里读 `IsPlayed/IsPlaying` 的「上一章/下一章」逻辑没动：
  增量更新维护的仍是同一组布尔值。
- 设置项的「即时生效」没有引入设置变更事件（仓库里没有这类事件源）：这四项改为**下一个视频生效**，
  写进了提交说明；`SubtitleFontSize` 的即时路径本来就由命令负责。
