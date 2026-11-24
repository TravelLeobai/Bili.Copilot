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

## 5. 刻意没做的

- 79 个 `async void` 一个没动（天然不能转 runtime-async，见 t2 §4.1）。
- 章节 UI 的绑定与 `PlayerViewModel.Commands.cs` 里读 `IsPlayed/IsPlaying` 的「上一章/下一章」逻辑没动：
  增量更新维护的仍是同一组布尔值。
- 设置项的「即时生效」没有引入设置变更事件（仓库里没有这类事件源）：这四项改为**下一个视频生效**，
  写进了提交说明；`SubtitleFontSize` 的即时路径本来就由命令负责。
