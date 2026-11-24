# .NET 11 源码热点评级（P0 / P1 / P2 / P3）

> 分支 `exp/net11-optimization` · 仓库 `bili-copilot-v2` · 扫描日期 2026-11-24
> 配套：[NET11_SOURCE_SCAN_MANIFEST.md](NET11_SOURCE_SCAN_MANIFEST.md)（逐文件清单）、[NET11_BUILD_AUDIT.md](NET11_BUILD_AUDIT.md)（构建配置实测）、[NET11_RESEARCH.md](NET11_RESEARCH.md)（.NET 11 特性）
> 数据来源：Roslyn 语义扫描（8 个真实编译单元、735 条命中、6,088 条调用点）+ 两轮全开分析器构建 + 现场提交记录。**本文只读代码，不改代码。**
>
> **快照说明**：语义数据取于 2026-11-24 19:40–19:50。期间团队并行提交了 `4e3566b`（修 Release 注释红灯）、`d781292`（拆掉 UI 工程常开的 `PublishAot`）、`f7fa0e3`（Visor.Models 补 runtime-async）、`5fe32e5`（三个测试工程升 net11.0）——**都不改业务源码**，所以本文的命中与调用链在当前 HEAD 上仍然成立。只有 P0-2 的前提（`PublishAot` 常开）已被 `d781292` 移除，该条已按当前树改写为"潜在依赖"。

---

## 0. 分级口径与总览

| 级别 | 定义（本仓库语境） | 条目 |
|---|---|---|
| **P0** | 已在现场造成过 UI 挂死/崩溃，或在特定构建开关下必然功能失效 | 2 组 |
| **P1** | 高频路径上的确定性浪费或稳定性风险（每帧/每包/每事件级别），或结构性缺陷（形态本身就吃掉 .NET 11 的收益） | 7 组 |
| **P2** | 中等频次路径的分配/取消/复用问题，或影响可维护性的形态问题 | 8 组 |
| **P3** | 低频或纯风格，只在特定改动附近顺手处理 | 4 组 |

| 级别 | 组数 | 命中条数（语义扫描口径） | 构成 |
|---|---|---|---|
| P0 | 2 | 4 | `JSON_REFLECTION` 真反射 4（另 7 条为误报，见 §9） |
| P1 | 7 | 107 | `ASYNC_VOID` 79 + `NEW_HTTPCLIENT` 热路径 12 + `SYNC_OVER_ASYNC` 8 + `BLOCKING_SLEEP` 4 + `STRING_CONCAT_LOOP` 2 + `DISCARDED_TASK` 1 + `IMAGE_ALLOC`(MyImage) 1 |
| P2 | 8 | 273 | `MISSING_CT` 90 + `LINQ_IN_LOOP` 59 + `HTTP_BUFFERED` 53 + `ASYNC_LAMBDA_VOID` 29 + `TASK_RUN` 20 + `IMAGE_ALLOC` 15 + `CLOSURE_IN_LOOP` 4 + `JSONDOC_NO_USING` 1 + `ASYNC_NO_AWAIT` 2 |
| P3 | 4 | 325 | `CONFIGUREAWAIT` 199 + `LINQ_MATERIALIZE` 117 + `LOOP_DELAY` 9 |
| 不计入（§9） | — | 26 | `NEW_HTTPCLIENT` 静态工厂 16 + `JSON_REFLECTION` 误报 7 + `IMAGE_IO` 3 |
| **合计** | 21 | **735** | 与清单文档 §5 的 735 条逐项对齐 |

> 一句话结论：**这个代码库的 async 形态是健康的（585 个 Task 方法全部能进 runtime-async），代价集中在两处——"以事件回调为入口的热路径"和"随手新建/整串缓冲的网络与图片路径"。**

---

## P0

### P0-1 播放器属性回调链：位置每变一次就碰一次设置与 WinRT —— 已造成过 UI 挂死

**现场证据**（来自本仓库提交 `630bfaf` 的事故记录，`hang-48212.dmp`、`Responding=False`、mpv 独立线程照播）：

```
MpvPlayer.OnDataNotify                        ← 外部库（Richasy.MpvKernel），mpv 线程
 └ PlayerSurface.OnMpvPropertyChanged         ← 事件订阅
   └ PlayerViewModel.OnPlayerPropertyChanged  ← async void（PlayerViewModel.Methods.cs:717）
     └ HandlePlayerPropertyChangedAsync       ← 本行以下全部在 UI 线程同步执行
       ├ TrackWatchedVideo
       ├ CheckSponsorBlock
       │  └ IsWholeVideoAdTestEnabled
       │     ├ Windows.Storage.ApplicationData.Current.LocalFolder     ← WinRT
       │     ├ Microsoft.Windows.Storage.ApplicationData.GetDefault()  ← WinRT（每次新建对象）
       │     └ SettingsToolkit.ReadLocalSetting ×N                     ← DI 解析 + WinRT
       └ CheckDanmakuJump
```

**现状（这次修过之后仍存在的问题）**：

| 位置 | 每次位置回调都要做的事 | 证据 |
|---|---|---|
| `PlayerViewModel.Methods.cs:773-790` | `PositionText = TimeSpan.FromSeconds(...).ToString("hh\\:mm\\:ss")`（每 tick 一次字符串分配 + 属性通知 + XAML 绑定更新） | 语义扫描 + 代码 |
| 同上 `:786-790` | `foreach (var chapter in Chapters)` 每个 tick 全量遍历并写 `IsPlayed`/`IsPlaying`（O(章节数)） | 代码 |
| `:795` | `SettingsToolkit.ReadLocalSetting(PlayNextWithoutTip, false)` —— **只要 `Duration > 5` 就每 tick 读一次** | 调用索引：`ReadLocalSetting` 在 `HandlePlayerPropertyChangedAsync` 内 4 处（`:795 :817 :935 :948`） |
| `:817` | `ReadLocalSetting(AutoPlayNext, true)`（播放到末尾附近时） | 同上 |
| `SettingsToolkit.cs:22` | `ReadLocalSetting` = `GlobalDependencies.Kernel.GetRequiredService<ISettingsToolkit>()` + `ApplicationDataContainer` 读取，**无任何缓存** | 代码 |

**为什么算 P0**：这不是"有点慢"，是 2026-11-24 18:12 真挂死过的路径；`630bfaf` 把 WinRT 目录解析改成 `static Lazy`、加了 0.25s 节流、给 `async void` 包了 try/catch，但**每 tick 仍有 1 次字符串格式化 + 1 次 settings 读取 + 全量章节遍历**。位置回调频率是"每秒几十次"，这一层任何一次 WinRT 卡顿都会直接顶到 UI 线程。

**建议方向**（留给 t4，不在本文实施）：位置类回调只保留 O(1) 的纯内存更新；`PositionText`/章节状态/投币检查分频（例如 ≥200ms 或进度变化 ≥0.1% 才更新）；`PlayNextWithoutTip`/`AutoPlayNext` 跟 `_wholeVideoAdTest` 一样做成"进视频时快照的字段"。

### P0-2 关闭了 JSON 反射，但代码里仍有 4 处反射序列化 —— 开关只要回来就静默失效

**先说当前状态（重要，与 t2 的报告不同）**：UI 工程那条"`PublishAot=True` 常开"已经在 `d781292` 里拆掉了，所以**新的构建不再写入** `System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault=false` 这类裁剪特性开关（实测 Release 28 条 → 5 条、Debug 27 → 4，口径见 `NET11_P0_FIXES.md` 批 2）。也就是说这条 P0 现在是**潜在**而非现发——**只要有人把 `<PublishAot>true</PublishAot>` 写回 UI 工程的 csproj 并重新 restore（或将来重开 AOT），下面 4 处立刻变成运行时异常**。留着它的理由：这 4 处是代码里的真实反射依赖，与构建开关解耦不了。

**当时的实测**（`_tmp/stjprobe`，net11.0 + t2 记录的 runtimeconfig 副本）：

```
System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault = false
→ THREW InvalidOperationException: Reflection-based serialization has been disabled for this application.
         Either use the source generator APIs or explicitly configure the 'JsonSerializerOptions.TypeInfoResolver' property.
```

**仓库里真正会踩到的 4 处**（11 条命中里逐点核对后的真反射；其余 7 条已正确传 `JsonTypeInfo`，是重建编译单元没跑 STJ 源生成器造成的误报，见 §9）：

| 位置 | 调用 | 后果 |
|---|---|---|
| `Services/AppStateStore.cs:107` | `JsonSerializer.Deserialize<Dictionary<string,string>>(json)` | 应用状态读不出来 |
| `Services/AppStateStore.cs:136` | `JsonSerializer.Serialize(data)` | 应用状态写不进去 |
| `Services/LiveEmoteStore.cs:142` | `JsonSerializer.Deserialize<EmoteState>(json)` | 表情状态读不出来 |
| `Services/LiveEmoteStore.cs:165` | `JsonSerializer.Serialize(_state, new JsonSerializerOptions{...})` | 表情状态写不进去（options 没带 resolver，仍是反射） |

**症状不是崩溃，是静默失忆**：四处都被 `try/catch` 包着（`catch { _cache = []; }` 之类），异常吞掉后表现为"状态每次启动都回到初始值"。这就是它至今没被当成问题发现的原因，也是它一旦回归（重开 AOT）时最难查的原因。

**建议方向**：把 `EmoteState` / `Dictionary<string,string>` 加进已有的 `GlobalSerializeContext`（`GlobalSerializeContext.cs:12-19` 已有 7 个 `[JsonSerializable]`），调用点改成传 `GlobalSerializeContext.Default.XXX`。这是纯代码侧的解耦，与"是否开 AOT"无关——无论 t4 后续怎么决定构建开关，这 4 处都该改。

---

## P1

### P1-1 79 个 `async void`：runtime-async 永远覆盖不到，异常直接杀进程

| 口径 | 数字 | 证据 |
|---|---|---|
| `async void` 声明 | **79**（55 个文件） | Roslyn 语义扫描（`ASYNC_VOID`），与 t2 的产物状态机计数（79 个残留 async 状态机）**一一对应** |
| 分布最密 | `PlayerViewModel.Methods.cs`（7）、`PlayerViewModel` 其它分部、`SearchHomePage.xaml.cs`（3）、`ArticleReader.xaml.cs`（3）、`StartupPage.xaml.cs`（2）、`CustomPromptDialog.xaml.cs`（2）、`CommentTextBox.xaml.cs`（2）、`WebSignInWindow.xaml.cs`（2）、`AIViewModel.cs`（2）、`OverlayTransportControls.xaml.cs`（7）、`PlayerInteractivePanel.xaml.cs`（5） | 见清单 §6.1 |
| 真实后果 | 1 次进程级崩溃已被 `630bfaf` 记录（"async void 里漏出去的异常直接杀掉进程，而 mpv 是独立线程/HWND → 看起来像 UI 崩了但视频还在播"），修法是**在每个 handler 里手包 try/catch** | 提交说明 |

**与 .NET 11 的关系**：`runtime-async` 只覆盖 `Task`/`Task<T>`/`ValueTask`/`ValueTask<T>` 四种返回形态；`async void` 一律回退状态机（见 t1 表格）。所以这 79 个既是"吃不到 .NET 11 收益"，也是"异常逃逸"的结构性来源。**不要为了 .NET 11 去改它们**（改不动，事件签名就是 void），改的价值在稳定性：把业务逻辑抽成 `async Task` 私有方法，handler 只留 `try { await XxxAsync(); } catch`。`630bfaf` 已经是这个形状（`OnPlayerPropertyChanged` → `HandlePlayerPropertyChangedAsync`），可作为模板。

### P1-2 直播弹幕接收循环：每包 3 次分配 + 每消息一次整块拷贝

`Services/Live/LiveDanmakuClient.cs`（直播弹幕主循环，高频、长驻）：

| 位置 | 现状 | 代价 |
|---|---|---|
| `:238` | `var buffer = new byte[64*1024]` 循环外复用 | ✅ 正确 |
| `:256` | `var data = stream.ToArray()` —— 每条完整消息复制一整块 | 每条消息一次数组分配+拷贝 |
| `:286-287` | `var body = new byte[bodyLength]; Buffer.BlockCopy(...)` —— **每个包**新建数组 | 弹幕高峰期每秒数十次 |
| `:327` | `Encoding.UTF8.GetString(body)` —— 每包一个字符串 | 同样频次 |
| `:328` | `JsonDocument.Parse(json)` —— 解析上面那个字符串 | 本可直接 `Parse(body)`（`ReadOnlyMemory<byte>` 重载）避开字符串 |
| `:88-89` | `Task.Run(() => ReceiveLoopAsync(...))` / `Task.Run(() => HeartbeatLoopAsync(...))` —— 包一层 Task.Run 启动异步方法 | 多余线程池跳转；返回的 Task 被丢弃（异常只能靠内部 catch） |

方向：`JsonDocument.Parse` 直接吃 `body` 的 `ReadOnlyMemory<byte>`；包体用 `ArrayPool<byte>`/`IMemoryOwner`；`Task.Run` 去掉直接 `_receiveTask = ReceiveLoopAsync(...)`。

### P1-3 Markdown 图片：每张图 `new HttpClient()` + 全尺寸解码

`Controls/Markdown/TextElements/MyImage.cs`：

```
:100  var client = new HttpClient();              ← 每张图片一个 HttpClient（socket/端口耗尽的经典成因）
:103  await client.GetAsync(_uri)                  ← 从未 Dispose
:120  await response.Content.ReadAsByteArrayAsync() ← 整图进内存
:123  var bitmap = new BitmapImage();
:131  await bitmap.SetSourceAsync(stream)          ← 先解码，后（:135）才读 PixelWidth —— DecodePixelWidth 来不及生效
:152  catch (Exception) { }                        ← 失败静默
```

对比同仓库已有的正确实现：`Extensions/SmallImageCache.cs`（LRU 192、按 `URL+目标像素` 去重、`DecodePixelWidth` 在设 `UriSource` 之后立刻给值、订阅 `MemoryManager.AppMemoryUsageIncreased` 主动清理）。**MyImage 这条路径应该改走 `SmallImageCache` 或 `InternalHttpExtensions.ImageClient`**（后者已是带 `SocketsHttpHandler` 的共享客户端）。

### P1-4 热/重复路径上的 12 处 `new HttpClient()`

28 条 `NEW_HTTPCLIENT` 里，13 处是静态工厂（`CreateClient()` → `static readonly`，**正确**），剩下 12 处在"每次调用都新建"的位置：

| 位置 | 频次语义 |
|---|---|
| `Controls/Markdown/TextElements/MyImage.cs:100` | 每张 markdown 图片（见 P1-3） |
| `Controls/Comment/CommentTextBox.xaml.cs:269` | 每次插入表情 |
| `Services/Live/LiveFollowService.cs:51/112/238` | 直播关注列表刷新（三处） |
| `ViewModels/Core/NotificationViewModel/NotificationViewModel.Live.cs:176` | 开播轮询查询 |
| `ViewModels/Core/PlayerViewModel/PlayerViewModel.cs:1245/1286` | 字幕文本拉取（两处） |
| `ViewModels/Core/PlayerViewModel/PlayerViewModel.Live.cs:881` | 粉丝数刷新 |
| `ViewModels/View/GalleryPageViewModel/GalleryPageViewModel.cs:115/134` | 保存图片 / 设壁纸（两处） |
| `Services/BackgroundLibrary.cs:247` | 导入主题 |

方向：全部改走共享客户端（同仓库已有 13 个静态工厂可复用）。

### P1-5 循环内字符串 `+=`（O(n²)）

`ViewModels/Core/AIViewModel/AIViewModel.Chat.cs`：

- `:310` `GetHotCommentTextAsync` —— 循环里拼热评文本
- `:387` `SendMessageInternalAsync` —— 循环里拼消息

AI 对话/热评都是"几十到几百条"量级，`+=` 在这量级会明显抬 GC。方向：`StringBuilder`（或 .NET 11 里可直接用 `string.Create`/插值 handler 先算长度）。

### P1-6 阻塞等待：3 处 `Thread.Sleep` + 1 处丢弃 Task（Visor 管道）

| 位置 | 现状 | 判定 |
|---|---|---|
| `Services/Diagnostics/PlaybackStallWatch.cs:218` | 循环内 `Thread.Sleep`（诊断线程，Debug 才编） | 可接受，但应改 `Task.Delay`/`ManualResetEventSlim.Wait(token)` 以便取消 |
| `Services/BackgroundLibrary.cs:406` | `TryDeleteDirectory` 里 `Thread.Sleep` 重试 | 若在 UI 线程调用则阻塞；需确认调用点 |
| `BiliCopilot.Visor/VisorPipeServer.cs:159` | `HandleCommand` 里 `Thread.Sleep` | 管道进程，可接受 |
| `BiliCopilot.Visor/VisorPipeServer.cs:157` | `Task.Run(...)` 的返回值被丢弃（`DISCARDED_TASK`） | 未观察异常 |

### P1-7 sync-over-async：`.Wait()` / `.Result` 共 8 处（3 个文件）

| 位置 | 形态 | 风险 |
|---|---|---|
| `Forms/PlayerWindow.cs:223/239/262/278` | `EnterFullScreen` / `ExitFullScreen` / `EnterCompactOverlay` / `ExitCompactOverlay` 里 `Task.Run(...).Wait()` | 这 4 个是**窗口状态切换**，在 UI 线程上 `.Wait()` 一个 `Task.Run` 会占住 UI 线程直到线程池跑完；轻则掉帧，重则和 DispatcherQueue 互等（死锁）。语义扫描已确认是 `Task.Wait()`，不是 `DispatcherQueue` 调用 |
| `ViewModels/Core/DownloadViewModel/DownloadViewModel.cs:271/295` | `ResolveDownloadPath` 里 `.Wait()` + 两个 `.Result` | 下载路径解析（文件选择器）——若在 UI 线程则直接卡死选择器 |
| `BiliCopilot.Visor/VisorPipeServer.cs:38` | `Stop()` 里 `.Wait()` | 管道进程，风险低 |

方向：4 个窗口方法改成 `async Task`（调用点已是事件处理器，可以 `async void` + try/catch 或直接 `await`）；下载路径解析改 `await`。

---

## P2

| # | 主题 | 命中 | 代表位置 | 判定理由 |
|---|---|---|---|---|
| P2-1 | 缺 `CancellationToken` 的 public/internal 异步方法 | 90（50 个文件） | `Services/*`、`ViewModels/*` | 不可取消 → 页面切走后请求照跑、弹幕/直播切换时旧请求继续写回 |
| P2-2 | 循环内 LINQ | 59（37 文件） | 见清单 §6.1 中 `LINQ_IN_LOOP` | 每次迭代一次 `IEnumerable` 分配 + 枚举器；热点在列表/弹幕构建 |
| P2-3 | `Task.Run` 包裹 async 方法 | 20（10 文件） | `PlayerViewModel.Danmaku.cs:45/61`（**循环内**，分段拉弹幕）、`LiveDanmakuClient.cs:88/89`、`AppViewModel.cs:189/285/412`、`AnimeTimelineViewModel.cs:41/46` | 只是多一次线程池跳转；`PlayerViewModel.Danmaku.cs` 两处还在 `for` 里造 Task |
| P2-4 | 整串缓冲 HTTP 响应 | 53（24 文件） | `ReadAsStringAsync` 各处 | 封面/弹幕/JSON 都是"先全量字符串再解析"；弹幕/长列表应流式 |
| P2-5 | 图片解码分配 | 16（11 文件） | `EmoteTextBlock.xaml.cs:141`、`ForwardDialog.cs:245`、`LiveSidePanel.cs:586/1006/1071`、`LevelImageConverter.cs:11`、`UriToBitmapConverter.cs:16/19`、`SearchHomePage.xaml.cs:89/145`、`PlayerViewModel.Live.cs:236`、`StartupPageViewModel.cs:104` | 表情/头像/等级图标在滚动或弹幕流里反复解码。其中 `UriToBitmapConverter.Convert`（`Converters/UriToBitmapConverter.cs:14-20`）**每次绑定求值都 `new BitmapImage(uri)`**、不设 `DecodePixelWidth`、完全绕过 `SmallImageCache`——列表复用/滚动时会反复整图解码，是这一组里最该先改的 |
| P2-6 | async lambda 转 void 委托 | 29（24 文件） | 事件订阅处 | 等价 `async void`，异常无人接；改法同 P1-1 |
| P2-7 | 循环内闭包捕获 | 4 | `PlayerOverlay.xaml.cs:488`、`LiveSidePanelHost.xaml.cs:238`、`Reactor/LiveSidePanel.cs:940`、`Services/QualityUnlockService.cs:793` | 每个迭代一次闭包分配；其中 `LiveSidePanel.cs:940`（表情面板填充）在渲染路径附近 |
| P2-8 | `JsonDocument` 未 `using` | 1 | `Controls/Article/ArticleReader.xaml.cs:133` | 文章 WebView 消息解析，`IDisposable` 未释放 → 池化缓冲滞留 |

---

## P3

| # | 主题 | 命中 | 说明 |
|---|---|---|---|
| P3-1 | `ConfigureAwait` | 199（37 文件） | 抽查结论：**多数是正确用法**（`Services/`、`Visor` 这类不碰 UI 的层用 `ConfigureAwait(false)` 正是该做的；WinUI 的 UI 层不该用，而 UI 层基本没用）。不建议批量改 |
| P3-2 | LINQ 物化（`ToList/ToArray/ToDictionary`） | 117（63 文件） | 绝大多数是"取完就走"的一次性物化，符合直觉；只在列表页/渲染路径上逐条评估 |
| P3-3 | 循环内 `Task.Delay` 轮询 | 9（9 文件） | 直播心跳（30s）、通知轮询等；已带 token 的保持现状 |
| P3-4 | `SmallImageCache` 容量上限 | 1 文件 | LRU 192 条 × `DecodePixelWidth` 上限 1024 → 理论上限数百 MB。实际用途是头像/表情（显示尺寸 16-64px），可按需把 `Capacity` 或像素上限调低，属微调 |

---

## 按任务要求的重点分区（同一批问题的另一种切法）

### async 形态

- `async void` 79（P1-1）；`async` lambda → void 委托 29（P2-6）；`async` 无 `await` 2（`AppViewModel.cs:93 LaunchAsync`、`SubscribedSeasonSectionDetailViewModel.cs:60`，P2）
- sync-over-async 8（P1-7）；`Thread.Sleep` 3（P1-6）；丢弃 Task 1（P1-6）
- **健康面**：585 个 async 方法里，506 个是 `Task`/`ValueTask` 形态、已由 runtime-async 消除状态机（t2 产物实测：状态机 585→79），剩下的 79 个正是上面的 `async void`——**没有"返回 Task 却回退"的案例**，说明这个代码库的 async 写法本身是规范的，问题集中在"事件回调入口"这一层。

### 高频路径分配

- 播放器位置回调：每 tick 字符串 + settings + 章节遍历（P0-1）
- 弹幕包解析：每包 3 次分配（P1-2）
- 循环内字符串拼接 2（P1-5）、循环内 LINQ 59（P2-2）、闭包 4（P2-7）
- `TimeSpan.ToString` 在热路径上的另一处（非播放器）：`Converters/DurationConverter` 等 XAML 转换器

### 网络与 JSON

- 12 处热路径 `new HttpClient()`（P1-4）、13 处静态客户端（正确，不要动）
- 反射序列化 4 处 vs 已关闭的反射开关（P0-2）；`GlobalSerializeContext` 已有 7 个类型可扩展
- 53 处 `ReadAsStringAsync` 整串缓冲（P2-4）；`JsonDocument` 未释放 1（P2-8）

### 图片缓存

- 好实践：`SmallImageCache`（LRU + 内存压力联动 + 按解码尺寸去重）、`InternalHttpExtensions.ImageClient`（共享 `SocketsHttpHandler`）
- 坏路径：`MyImage`（每图一 client + 全尺寸解码，P1-3）、`UriToBitmapConverter`（XAML 绑定里每次 `Convert` 新建 `BitmapImage`，P2-5）

### 直播消息循环

`LiveDanmakuClient`（P1-2）为主；配套 `HeartbeatLoopAsync`（30s，带 token）、`StopAsync` 的取消顺序（先 cancel 再 close 再 dispose，正确）。`LiveSidePanel`/`LiveFollowService` 的 UI 侧热点见 P1-4、P2-5。

### 弹幕轨道计算

- 轨道/显示参数来源：`DanmakuRenderViewModel.ResetOptions`（读 11 次本地设置，**每次重设都读**）→ 若有频繁 Reset 的路径，建议与 P0-1 同样做快照
- 弹幕分段拉取：`PlayerViewModel.Danmaku.cs:42-62`（`Task.Run` 在 `for` 里 + `SemaphoreSlim(6)` 限流，限流是对的，`Task.Run` 多余）
- 位置驱动：`Danmaku.UpdatePosition`（`PlayerViewModel.Methods.cs:784`）每 tick 调一次，属于设计如此

### 播放器属性回调

见 P0-1（完整调用链 + 剩余成本）。补充事实：`HandlePlayerPropertyChangedAsync` 里 `IsConnecting` 的跨线程处理已经用 `DispatcherQueue.HasThreadAccess` + `TryEnqueue` 正确兜底（`:754-770`），这块不用改。

### 启动初始化

- `AppViewModel.LaunchAsync`（`:93`）是 `async Task` 但**没有 `await`**（P2），内部 `PreloadEmotePanel()`（`:412`）与 `StartVisorService()`（`:285`）各自 `Task.Run` 后丢弃 → 启动期有 2 条不受跟踪的后台链
- `SettingsPageViewModel.InitializeAsync` 一次读 **92** 次本地设置（调用索引实测）——设置页打开时的一次性成本，可接受但可合并
- `App.ProbeEnhancementSettings` 读 6 次设置（启动期）

---

## 与 .NET 11 的真实关系（别把不是 11 的算成 11）

| 结论 | 依据 |
|---|---|
| runtime-async 已吃到 506 个方法的状态机消除 | t2 实测：`585 → 79` 个 async 方法状态机、dll −11.2% |
| `async void` 79 个**永远**吃不到（形态限制），不要为 .NET 11 去改 | t1 表格 + t2 产物计数一一对应 |
| Dynamic PGO、分层编译、ValueTask、`SearchValues` **不是 11 新增** | t1 §5 清单；本项目 `TieredPGO`/`TieredCompilation` 都是默认值，不需要配 |
| 本项目**没有**开 R2R，所以"R2R 比较器专用化 20×"之类收益不适用 | t2：`PublishReadyToRun=False` |
| 真正能靠"顺手改代码"吃到的 11 收益，只有两处：①把 8 处 sync-over-async/阻塞改成 `await`（让 UI 线程回到事件循环）②把 4 处反射 JSON 改源生成（配合 AOT/裁剪） | 本文 P0-2、P1-7 |

---

## 不该动的地方（避免 t4 误改）

1. `Extensions/SmallImageCache.cs` —— 已经是"按解码尺寸去重 + LRU + 内存压力清理"的正确实现。
2. 13 个 `static readonly HttpClient` 工厂（`AdCloudService.CreateClient` 等）—— 复用正确，不要改成 `IHttpClientFactory` 再引入一层。
3. `GlobalSerializeContext`（7 个 `[JsonSerializable]`）—— 方向对，只做加法。
4. `PlayerViewModel.Methods.cs:754-770` 的 `DispatcherQueue` 兜底 —— 跨线程语义正确。
5. `obj/` 下 494 个生成文件（XAML code-behind、MVVM Toolkit 产物、Reactor 源映射、CsWin32/ComputeSharp 生成物）—— 只看不改。
6. `#if DEBUG` 的诊断探针（`PlaybackStallWatch` 等 6 个文件）—— 它们只在 Debug 编入，改它们不影响发布物。

---

## 待 t4 决策的三个问题（本文不给答案）

1. **P0-2 只剩代码侧**：构建侧（`PublishAot` 常开）已由 `d781292` 解决，剩下的就是把这个 4 处反射改成源生成（约 20 行：2 个类型登记 + 4 个调用点传 `JsonTypeInfo`）。**做与不做取决于是否打算给 UI 进程上 AOT**——如果不打算，它只是"让代码不再依赖一个已被关掉的开关"，属于防御性清理，可以排到 P2 批次。
2. **P1-1/P1-7 的改造顺序**：`async void` 79 处是"逐个补 try/catch"（低风险、机械）还是"抽 `async Task` + 统一基类 handler"（高收益、要动 55 个文件）？建议先做有崩溃记录的那几个（`PlayerViewModel` 相关 12 处）。
3. **P0-1 的分频阈值**：位置回调里 `PositionText`/章节/投币检查各自的分频（200ms？位置变化 ≥0.1%？），需要 t5/t6 用 `PerfProbe` 的 UiLag 数据来定，不要凭感觉。

---

## §9 不计入分级的 26 条（误报与正确用法）

| 来源 | 条数 | 为什么不计入 |
|---|---|---|
| `NEW_HTTPCLIENT` 静态工厂 | 16 | 形如 `private static readonly HttpClient Client = CreateClient();`，**复用正确**。语义扫描按"出现 `new HttpClient()`"记账，但落在工厂方法里不构成问题（真正的热路径 12 条见 P1-4） |
| `JSON_REFLECTION` 误报 | 7 | 调用点已经传了 `JsonTypeInfo`（`GlobalSerializeContext.Default.VisorCommand` 等）。误报原因是我的重建编译单元**没有运行 STJ 源生成器**，`GlobalSerializeContext.Default` 在语义模型里是错误类型，因而没被识别为 `JsonTypeInfo`。逐点核对后的真反射是 4 条（P0-2） |
| `IMAGE_IO` | 3 | `SetSourceAsync`/`GetThumbnailAsync`/`OpenReadAsync` 的调用点本身不构成问题，是否浪费取决于解码尺寸——已在 P1-3/P2-5 里逐点评估 |

> 方法论备忘：源生成器缺位会稳定造成"看起来像反射"的假阳性。凡涉及源生成类型的检查项（STJ、MVVM Toolkit、Reactor、CsWin32、ComputeSharp），最终判定必须以**逐点读代码**为准，语义扫描只用于"缩小范围"。

---

## 附录：可复现路径

```powershell
# 编译规格（从真实 csc 命令行重建）
_tmp\scan\extract-specs.ps1 -Log <build -v:detailed 日志>
# 语义扫描（8 个编译单元 → findings/callsites/filestats/diag）
dotnet _tmp\scan\bin\Release\net11.0\RoslynScan.dll _tmp\scan\specs _tmp\scan\out2
# 反射开关实测
dotnet _tmp\stjprobe\bin\Debug\net11.0\p.dll     # 带 IsReflectionEnabledByDefault=false 的 runtimeconfig
```

调用链查询示例（`callsites.jsonl` 里每条含 `Project/Callee/File/Line/Member/Kind`）：

```powershell
Get-Content _tmp\scan\out2\callsites.jsonl | ForEach-Object { $_ | ConvertFrom-Json } |
  Where-Object { $_.Callee -like '*ReadLocalSetting*' -and $_.Member -like '*HandlePlayerPropertyChanged*' }
```

