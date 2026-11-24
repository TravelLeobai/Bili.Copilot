# .NET 11 现代化改造报告（Bili.Copilot / bili-copilot-v2）

> 分支 `exp/net11-optimization`，报告基线 HEAD `77ed06a`（四份报告本身提交于 `7a81ab6`），**未 push**。本轮提交范围 `2f10c66..77ed06a` 共 25 个提交：8 个改代码或工程配置，17 个文档。
> 本文只回答一个问题：**改了什么、改到什么程度**。现状见 `NET11_OPTIMIZATION_AUDIT.md`，数据见 `NET11_PERFORMANCE_VALIDATION.md`，逐文件清单见 `docs/NET11_SOURCE_SCAN_MANIFEST.md`。
> 验证分三档，正文逐项标注：**已验证（行为）**＝除构建外还有运行期证据；**仅构建通过**＝只有编译/求值证据，没有运行期证据；**未验证**＝缺条件，不填推断。

## 0. 结论

1. 8 个代码/工程提交：修 Release 红灯（`4e3566b`）、拆常开的 `PublishAot`（`d781292`）、补 `Visor.Models` 的 runtime-async（`f7fa0e3`）、三个测试工程升 `net11.0`（`5fe32e5`）、播放位置回调 O(1) 化（`69785b5`）、窗口切换的 500 ms 复位改回 UI 线程写（`08c0c0a`）、markdown 图与绑定转换器复用/解码宽度前置（`1372af1`）、直播弹幕接收路径去分配（`b41a978`）。
2. 两条有运行期证据的改动：`69785b5`（播放期 UiLag p95 1.10–2.16 ms、Responding 10/10，NET11_P0_TICKPATH.md §4）与 `b41a978`（直播间 op=8 / op=5 proto=3 / op=5 proto=0 全部解析成功、互动列表 41→113 条，NET11_P1_NETWORK_IMAGE.md §直播间）。`1372af1` 只覆盖到"详情页图片正常渲染"，评论区未取证。
3. 三条只有编译/求值证据：`4e3566b`（Release EXIT=0）、`d781292`（开关数 28/27 → 5/4）、`f7fa0e3`（csc 命令行带 `/features:runtime-async=on`，但该工程 0 个 async，零收益）。
4. `5fe32e5` 的验证是测试执行本身：CommentPoll 84/84、Diagnostics 23/23；`-c Release` 只剩 5/23 的既有问题没解。
5. **本轮没有做**：79 个 `async void` 一个没动、`UI.Models` 没抬 TFM、CI 与 `global.json` 没动、`rd.xml`/死符号/未用钉版没清、AOT publish 与 MSIX 打包没跑。
6. 构建通过不等于功能正常：`08c0c0a`（窗口切换）只有代码路径分析 + 0 错 0 警，没有"切 30 次窗口"的专项数据；`d781292` 退出的是配置语义，AOT 那条路本来就没人走过。
7. **t14 第二轮在 HEAD 重建产物上重跑了构建与单测**（Debug/Release `-t:Rebuild` 均 0/0，167.5 s / 170.0 s；CommentPoll 84/84、Diagnostics 23/23），但**播放没有成立**：5 次尝试全部卡在 mpv 初始化 20 s 超时，同一窗口还有 360 条播放器属性回调的 `NullReferenceException`（NET11_VERIFY_RAW.md 附·A1、A3、A5.2、A7.4）。所以本轮"播放期"相关的改动能引用的运行期证据仍来自 20:0x/21:3x 两次会话，不能声称在 HEAD 上回归通过。

## 1. 提交清单

| 提交 | 类型 | 改动文件（行数） | 动机 | 验证档位 |
|---|---|---|---|---|
| `4e3566b` | 修 bug | `Services/Diagnostics/DiagnosticsReport.cs`（+1/−1） | Release 编不过会卡住后续全部验证 | 已验证（构建） |
| `d781292` | 构建配置 | `BiliCopilot.UI.csproj`（+16/−1） | 退出 AOT 意图与 `PublishTrimmed` 求值 | 已验证（配置求值）／AOT publish 未验证 |
| `f7fa0e3` | 构建配置 | `BiliCopilot.Visor.Models.csproj`（+8） | 补 runtime-async 覆盖面 | 仅编译证据 |
| `5fe32e5` | 测试配置 | 三个测试工程 `.csproj`（+7/−4） | net9 测试工程挡着 net11-only API | 已验证（测试执行） |
| `69785b5` | 性能 | `PlayerViewModel.Methods.cs`（+160/−14） | 位置回调每 tick 碰 WinRT 与分配 | 已验证（行为，非受控对照） |
| `08c0c0a` | 性能/正确性 | `Forms/PlayerWindow.cs`（+33/−20） | 池线程白占 500 ms + 跨线程写非 volatile 字段 | 仅构建通过 |
| `1372af1` | 性能 | `MyImage.cs`（+44）、`UriToBitmapConverter.cs`（+125） | 每图一个 HttpClient、全尺寸解码 | 部分已验证（详情页图片） |
| `b41a978` | 性能 | `Services/Live/LiveDanmakuClient.cs`（+116/−20） | 每包多次分配、整串拷贝 | 已验证（弹幕链） |

文档提交（17 个）：`2f10c66` `c588061` `ee5357c` `a8dbea4` `a0d12a6` `5eeddeb` `dc7a8a3` `ce02cd2` `fbb239e` `555fabf` `25dbbb4` `2bb69b8` `dafb451` `df333da` `05dd026` `dfdf0bf` `77ed06a`，内容对应 `docs/NET11_RESEARCH.md`、`NET11_BUILD_AUDIT.md`、`NET11_HOTSPOTS.md`、`NET11_SOURCE_SCAN_MANIFEST.md`、`NET11_P0_FIXES.md`、`NET11_P0_TICKPATH.md`、`NET11_P1_NETWORK_IMAGE.md`、`NET11_VERIFY_RAW.md`（`77ed06a` 只往最后一份追加了"附：第二轮实测"）。

## 2. 逐项改动

### 2.1 `4e3566b` 修 Release 红灯（P0）

- **动机**：`DiagnosticsReport.cs:14` 的 `<see cref="PlaybackStallDump"/>` 指向整体包在 `#if DEBUG` 里的类型，Release 下 CS1574 被 `Directory.Build.props:8` 的 `TreatWarningsAsErrors` 升成错误；编译一失败，XAML 第二遍缺 `-LocalAssembly`，再喷 `WMC1509` + 一串 `WMC0001` + `WMC9999`（NET11_BUILD_AUDIT.md §6）。
- **改动**：cref 改成 `<c>PlaybackStallDump.cs</c>` 并注明该类只在 Debug 编译。**没有把类型挪出 `#if DEBUG`**，那会改变 Release 行为；全仓 `cref` 扫过，指向 DEBUG-only 类型的只有这一处（NET11_P0_FIXES.md 批 1）。
- **验证**：改前 Release EXIT=1（1 error）；改后 EXIT=0、0 警告 0 错误，WMC 连锁同时消失（NET11_P0_FIXES.md 批 1）。
- **档位**：已验证（构建）。这批只有构建结果可验，没有运行时行为变化。

### 2.2 `d781292` 拆掉常开的 `PublishAot`（P0）

- **动机**：`<PublishAot>True</PublishAot>` 在**普通 build** 阶段就把 `PublishTrimmed` 求值成 `true`，把 23 条裁剪导向开关写进 `runtimeconfig.json`，而程序集并没有真被裁剪——运行时被告知"不要做动态代码/反射"（NET11_BUILD_AUDIT.md §5）。
- **改动**：删掉 csproj 原 `:1500` 的 `<PublishAot>True</PublishAot>`，原位置留注释说明取舍。`PublishReadyToRun=False` 保留；`SelfContained` 不动（它来自 WinAppSDK/MSIX 打包链路，与 AOT 无关）。
- **验证**：`dotnet msbuild … -getItem:RuntimeHostConfigurationOption` 复算，Release 28 / Debug 27 → Release 5 / Debug 4；属性求值 `PublishAot=True` → `PublishTrimmed=true`、`SelfContained=true`，拆掉后前两个为空、`SelfContained` 仍 `true`（NET11_P0_FIXES.md 批 2）。复现前置：`<PublishAot>` 必须在 restore 之前就在位，否则差值会偏小（同批注释；"6/5"那个数已由 t10 撤回）。
- **档位**：已验证（配置求值）。AOT publish 不可行性/AOT 运行时行为**未验证**。

### 2.3 `f7fa0e3` 补 `Visor.Models` 的 runtime-async（P1）

- **动机**：runtime-async 是逐项目的 `Features`，不随 `ProjectReference` 传播；`Visor.Models` TFM 是 `net11.0` 但没写 `Features`，以后有人往里写 async 会悄悄退回状态机（NET11_BUILD_AUDIT.md §3、NET11_RESEARCH.md §1.9）。
- **改动**：加 `<Features>$(Features);runtime-async=on</Features>`（csproj:16）。
- **验证**：`-t:Rebuild -v:detailed` 后 csc 命令行含 `/features:runtime-async=on`，工程求值 `Features=;runtime-async=on`（NET11_P0_FIXES.md 批 3）。
- **档位**：仅编译证据。该工程当时 0 个 async 方法，没有可观测的行为差异；`IsAotCompatible` 刻意没跟着加（防 ILLink 警告经 `TreatWarningsAsErrors` 变红灯）。

### 2.4 `5fe32e5` 三个测试工程升 `net11.0`（P1）

- **动机**：三个测试工程钉 `net9.0`，同一个诊断源文件被"二次编译"——一次进 net9 测试宿主，一次进 net11-windows 应用，机械地禁止了 net11-only API（NET11_BUILD_AUDIT.md §8、§11-A）。
- **改动**：三个 `.csproj` TFM 改 `net11.0`；`CommentPoll` 删掉 `Microsoft.Extensions.Logging.Abstractions`（net11 共享框架已内置，原引用触发 NU1510）；测试栈版本（xunit 2.7.0 / Test.Sdk 17.10.0 / runner 2.5.7）一个没动。
- **验证**：CommentPoll 84/84、Diagnostics 23/23（均 net11.0，退出码 0）；`Moment` 0 错 0 警（该工程是控制台小程序，不是测试工程）。
- **已知限制**：`Diagnostics.Tests` 的 18 个用例被 `#if DEBUG` 包住，`-c Release` 只剩 5/23——升 TFM 不解决这个（NET11_P0_FIXES.md 批 4）。

### 2.5 `69785b5` 播放位置回调 O(1) 化（P0-1）

- **动机**：位置回调每秒跑 20–60 次，原实现每个 tick 做三件事：4 处 `SettingsToolkit.ReadLocalSetting`（服务定位器 + WinRT 容器读，无缓存，`Toolkits/SettingsToolkit.cs:22`）、一次 `TimeSpan.FromSeconds(...).ToString(@"hh\:mm\:ss")`、一次 `foreach (var chapter in Chapters)` 全量遍历并逐章写 `IsPlayed`/`IsPlaying`（NET11_HOTSPOTS.md P0-1）。
- **改动**（`PlayerViewModel.Methods.cs`，位置分支现在 777–898 行）：
  - 四项设置改为进视频时一次性读进字段（`RefreshTickPathCache()`），回调里只读字段；
  - 整秒值变了才格式化 `PositionText`；
  - 章节状态改为从上次命中索引出发挪动、只写变化项，判定条件与原实现逐字一致（`IsPlayed = Position ≤ 播放位置`、`IsPlaying = |Position − 播放位置| < 2`）。
- **验证**：代码侧可复核——位置分支内 `ReadLocalSetting` 0 处、`foreach (var chapter in Chapters)` 0 处、`TimeSpan.FromSeconds` 2 处（1 处代码 + 1 处注释）（NET11_P0_TICKPATH.md §2）。运行期：Debug 构建 0 错 0 警；播放期 7 个 10 s 窗口 p50 0.12–0.19 ms、p95 1.10–2.16 ms，`Responding` 10/10 `True`（§4）。
- **档位**：已验证（行为）。改前/改后是**非受控对照**——两次会话相隔 1 小时 20 分、视频不同、机器负载不同（NET11_P0_TICKPATH.md §4 末尾；NET11_PERFORMANCE_VALIDATION.md §5）。
- **行为变化（写进提交说明）**：这四项设置改为"下一个视频生效"，因为仓库里没有设置变更事件源；`SubtitleFontSize` 的即时生效路径不变（由 `ChangeSubtitleFontSizeCommand` 自己回写）。

### 2.6 `08c0c0a` 窗口切换的 500 ms 复位（P1-7）

- **动机更正**：t3 原文说这是"UI 线程上的 `Task.Run(...).Wait()`"，实际四处写的是 `_ = Task.Run(() => { Task.Delay(500).Wait(); _isPresenterChanging = false; })`——`Wait()` 在池线程上跑，不阻塞 UI 线程、不会死锁。真问题两个：每次切窗口模式白占一个池线程 500 ms；`_isPresenterChanging`（`:31`，`IsPresenterChanging` 在 `:83` 暴露）由池线程写、UI 线程读，非 volatile 字段可见性没保证（NET11_P0_TICKPATH.md §3）。
- **改动**：四段换成 `_ = ResetPresenterChangingFlagAsync()`（221/233/252/264 行），方法体 `await Task.Delay(500)` → 判断 `DispatcherQueue.HasThreadAccess`，直接写或 `TryEnqueue` 回 UI 线程写。时序不变（仍 500 ms、仍 fire-and-forget）。
- **验证**：调用点全为 `void` 同步调用，没有调用方依赖 `_isPresenterChanging` 的写入线程（`PlayerViewModel.Commands.cs:122/126`、`PlayerViewModel.cs:812/816`、`PlayerViewModel.Engine.cs:343-367`、`Methods.cs:589-610`）；构建 0 错 0 警（NET11_P0_TICKPATH.md §3；NET11_P1_NETWORK_IMAGE.md §构建 20:19 那次覆盖到这四个提交）。
- **档位**：仅构建通过 + 代码路径分析。没有"反复切换窗口模式 N 次"的压测数据。

### 2.7 `1372af1` markdown 图与绑定转换器

- **动机**：`MyImage.cs:100` 每张图 `new HttpClient()` 且从不 Dispose，`:123` 建 `BitmapImage` 后直接 `SetSourceAsync`，`:135` 才读 `PixelWidth`——解码已按原图尺寸做完，`DecodePixelWidth` 来不及生效；`UriToBitmapConverter` 每次绑定求值都 `new BitmapImage(uri)`、不设解码宽、完全绕过 `SmallImageCache`（NET11_HOTSPOTS.md P1-3、P2-5）。
- **改动**：
  - `MyImage`：复用 `InternalHttpExtensions.ImageClient`（静态单例，带 UA / GZip+Brotli / HTTP2）；`ResponseMessage` 补 `using`；`DecodePixelWidth` 写在 `SetSourceAsync` **之前**，宽度取显示宽度的 2 倍 DPI（夹 16–2048），无显示宽度时套 1024 上限；
  - `UriToBitmapConverter`：按「地址 + 解码宽度」走 24 条 LRU 复用同一张 `BitmapImage`；支持 `ConverterParameter` 传目标像素宽（16–4096）；订阅 `MemoryManager.AppMemoryUsageIncreased` 清表。
- **验证**：Debug 构建 0 警告 0 错误（20:19；20:16 用隔离 `OutDir` 的对照构建同样 0/0）。运行期：视频详情页的 UP 头像与 5 张推荐封面全部正常渲染，无占位块/裂图；21:33–21:55 共 1929 行日志里 `UriToBitmap` / `BitmapImage` / `DecodePixel` 相关告警 **0 条**（NET11_P1_NETWORK_IMAGE.md §视频评论区）。
- **未覆盖**：`AppSearchBox` 16px 图标、`PlaybackEndPanel` 64px 头像、`GalleryPage` 三处调用点没传 `ConverterParameter`（XAML 不在该批文件范围），所以默认仍是原尺寸解码——完整收益要等这三处各加一个参数；评论区列表没取证（见 §4）。

### 2.8 `b41a978` 直播弹幕接收路径

- **动机**：每条 WS 消息 `stream.ToArray()` 整块拷贝、每个包 `new byte[bodyLength] + BlockCopy`、每包一个 `Encoding.UTF8.GetString` 再 `JsonDocument.Parse(string)`、解压输入/输出各一次拷贝、两个循环外面套 `Task.Run` 且返回的 Task 被丢弃（NET11_HOTSPOTS.md P1-2）。
- **改动**：消息缓冲跨包复用 + `ReadOnlyMemory` 切片；包体零分配切片；`JsonDocument.Parse(ReadOnlyMemory<byte>)` 直接吃 UTF-8 字节；解压输入改只读内存流视图（`CanSeek=false`），输出用 `GetBuffer()` + 有效长度；连接后直接 `await` 启动收发循环，去掉 `Task.Run`。
- **验证**：直播间实测日志 `20:22:03 弹幕服务器响应长度 956` → `op=8 proto=1 len=26`（认证回包）→ `op=5 proto=3 len=673`（brotli 解压路径）→ `op=5 proto=0 len=1598`（未压缩切片路径）→ 消息 `ENTRY_EFFECT` / `ONLINE_RANK_V3` / `INTERACT_WORD_V2` / `ONLINE_RANK_COUNT` / `DANMU_MSG`；`直播弹幕列表构建：0 页 41→113 条` 说明"包体字节 → JsonDocument → Clone → 分发 → 解析成互动项 → 上屏"整链走通；整场没有一条解压失败或 JSON 解析失败（NET11_P1_NETWORK_IMAGE.md §直播间）。
- **行为不变**：事件契约仍是 `(string Command, JsonElement Data)`，`root.Clone()` 保留，`LiveMessageParser` 一处未动；`protover 2/3` 递归解压、`op=8` 认证、心跳格式照旧。唯一语义差异：非法 UTF-8 现在由 `JsonDocument.Parse` 抛异常走 `catch` 记 Debug（原来会用 U+FFFD 替换后尝试解析），B 站弹幕流是合法 UTF-8（同文 §C）。
- **未覆盖**：该房间当时无礼物/SC 事件，这两类没有真实样本；评论区未取证。

## 3. 验证等级总表

| 改动 | 构建 | 运行期行为 | 档位 |
|---|---|---|---|
| `4e3566b` Release 红灯 | Release EXIT=0，0 警告 0 错误 | 不适用（注释） | 已验证（构建） |
| `d781292` PublishAot | Release/Debug `-t:Rebuild` 0/0；开关数 28/27 → 5/4 | 不适用（配置）；AOT 运行时未验证 | 已验证（配置求值） |
| `f7fa0e3` Visor.Models | `-t:Rebuild` 0/0；csc 带 `/features:runtime-async=on` | 无 async 可观测 | 仅编译证据 |
| `5fe32e5` 测试 TFM | 三工程编过，Moment 0 错 0 警 | 84/84、23/23 通过 | 已验证（测试执行） |
| `69785b5` 位置回调 | Debug 0/0 | UiLag p50 0.12–0.19 / p95 1.10–2.16 ms，Responding 10/10 | 已验证（非受控对照） |
| `08c0c0a` 窗口切换 | 0/0 | 无专项数据 | 仅构建通过 |
| `1372af1` 图片 | 0/0（两处输出目录） | 详情页图片正常、0 条解码告警 | 部分已验证 |
| `b41a978` 弹幕 | 0/0 | 三类包 + 五类消息 + 互动列表 41→113 | 已验证（礼物/SC 未覆盖） |

复用的整体验证（同一代码树，两轮独立复跑）：

- `555fabf`（t11）：Debug `-t:Rebuild` EXIT=0 / 0 警告 0 错误 / 152.4 s，Release 同 / 148.3 s；状态机 async 108、迭代器 7、`d__` 86（NET11_VERIFY_RAW.md §1.2、§2.2）。
- `dafb451` 工作区干净时重建（t14）：Debug EXIT=0 / 0 警告 0 错误 / 167.5 s，Release 同 / 170.0 s；状态机计数三处一致（108 / 7 / 86）；CommentPoll 84/84、Diagnostics 23/23（NET11_VERIFY_RAW.md 附·A1、A2、A3）。
- `555fabf` 之后到 `77ed06a` 只有注释与文档改动（`2bb69b8` 只改 `BiliCopilot.UI.csproj:1500` 起那段 `<!-- -->` 注释、其余全为 md），所以两轮的代码状态相同。

## 4. 已知限制与行为变化

| 项 | 内容 | 出处 |
|---|---|---|
| 四项播放设置 | `PlayNextWithoutTip` / `AutoPlayNext` / `IsPlayerSpeedShare` / `SubtitleFontSize` 改为"下一个视频生效"（仓库无设置变更事件源） | NET11_P0_TICKPATH.md §6；`69785b5` 提交说明 |
| 章节高亮 | 判定条件与原实现逐字一致，只是不再每 tick 全量重算重发通知 | NET11_P0_TICKPATH.md §1 |
| 弹幕非法 UTF-8 | 由"U+FFFD 替换后尝试解析"变为"抛异常走 catch 记 Debug" | NET11_P1_NETWORK_IMAGE.md §C |
| 绑定转换器 | 3 个调用点未传 `ConverterParameter`，默认仍按原尺寸解码 | NET11_P1_NETWORK_IMAGE.md §B |
| `MyImage` 的 `async void` | `LoadImage` 的 `async void` 与 `_loaded` 只在 else 分支置位的问题没动（属 P1-1 线） | NET11_P1_NETWORK_IMAGE.md §明确没做的事 |
| 79 个 `async void` | 一个没动（形态限制，非 .NET 11 收益项） | NET11_P0_FIXES.md §本轮没做的事 |
| `Diagnostics` Release | `-c Release` 只剩 5/23，必须固定 Debug | NET11_P0_FIXES.md 批 4 |
| 测试工程位置 | 仍在 sln 外、不继承 `src/Directory.Build.props`，`-p:BuildOutDir` 对它们不生效 | NET11_VERIFY_RAW.md §3.2 |
| `UI.Models` 抬 TFM | 评估后不抬：立报 `PinItem.cs(51,26)` CS8765，要改源码，超出该任务范围 | NET11_P0_FIXES.md 批 3 |
| 播放初始化 | HEAD 重建产物上 5 次尝试全部 mpv 初始化 20 s 超时（`Load.MpvInit p50=20012.34ms`）；21:40 那次成功（`create-player:end … elapsed=11555ms`），两次不一致的原因未查明 | NET11_VERIFY_RAW.md 附·A5.2、A8 |
| 播放器属性回调 | 同一采集窗口 360 条 `NullReferenceException`（Position 332），栈顶是 `PlayerViewModel.Methods.cs:704` 的 `Player` 未判空；属当前代码状态，由单独任务处理，不是本轮 8 个提交引入 | 附·A7.4 |

## 5. 未验证项

| 项 | 缺什么 | 为什么缺 | 补它需要的条件 |
|---|---|---|---|
| UI 工程的 AOT publish | 一次真正带 `PublishAot` 的出包结果 | 本轮 publish 未开 AOT；拆 PublishAot 只解决 build 阶段语义（`d781292`） | 把 `<PublishAot>true</PublishAot>` 写进 csproj（不能用命令行，会 `NETSDK1207` 传染 netstandard 工程），跑 publish + 运行时冒烟 |
| MSIX 打包 | `GenerateAppxPackageOnBuild` 在 net11 + WinAppSDK 2.6.5 上的出包证据 | 需要签名证书 | 临时证书 + 一次打包 |
| ARM64 / x86 | 两个架构的构建与运行 | 只在 x64 上验 | 对应架构工具集 + 一次 `-t:Rebuild` |
| 视频评论区 | 评论列表整页冒烟的截图与日志 | 播放器窗口 UIA 树只有 9 个节点，Reactor 控件不暴露；坐标点击只切到详情页；滚轮无效；21:45 后窗口让给 verifier（NET11_P1_NETWORK_IMAGE.md §未验证） | 人工在应用里点「详情」→ 侧栏 `评论` Tab 约 1 分钟即可补 |
| 礼物 / SC 弹幕 | 两类事件的真实样本 | 该房间当时无此类事件 | 进一个有礼物/SC 的直播间，同一解析入口无需改代码 |
| 窗口切换专项 | 反复切换全屏/紧凑覆盖的失败率与卡顿 | 只做了代码路径分析 | 一段 UIA 或手动 30 次切换 + UiLag 采样 |
| 改前/改后受控 A/B | 安静机器上单向变量的两轮对照 | 环境被并发构建与 bin 锁打断两次（`WMC0601`、`MSB3021/MSB3027`），工作区已恢复 HEAD（NET11_P0_TICKPATH.md §4） | 安静机器 + 固定同一个视频 + 各自 3 轮取中位 |
| `dotnet-counters` 播放场景的"改前"基线 | 改动前产物上的 counters 数据 | 首次采集时部署目录里的 bin 是 AOT 产物、EventSource 被关，采不到（NET11_P0_TICKPATH.md §5a） | 在旧 bin 未重建前采集一次；现已被重建覆盖，需回退产物重取 |
| 播放能否在 HEAD 上稳定成立 | 一次成功的 ≥60 s 播放 | t14 5 次尝试全部 mpv 初始化 20 s 超时（NET11_VERIFY_RAW.md 附·A5、A8 未验证 1） | 定位 `create-player:timeout`（日志写明 `no fallback, no dispose`）的成因后重测 |
| 成功播放场景的 counters / UiLag（HEAD 产物） | 播放成立时的 30 s 采样与 UiLag 聚合 | t14 的 counters 窗口里 mpv 已超时，"播放窗口开、播放未成立"（附·A7.1 注）；成功场景数据来自 t13 的旧会话 | 播放成立后按同一条 counters 命令与 UiLag 10 s 聚合各采 30 s |

## 6. 交付边界与工作区状态

- 全部改动在本地分支 `exp/net11-optimization`，**没有 push**，没有开 PR。
- `a0d12a6` 是 auditor 的扫描文档提交（含两份扫描文档的已提交版本），原样保留；其中的最新改动经 soft reset 拆回工作区后由 `dc7a8a3` 单独提交，内容未丢（`dc7a8a3` 提交说明、任务 t4 记录）。
- 工作区在 `77ed06a` 之后（含四份报告的 `7a81ab6`）还有 **3 处未提交改动，不属于本交付**：`docs/NET11_HOTSPOTS.md`（P0-2 叙述同步）、`PlayerViewModel.Methods.cs`、`PlayerViewModel.cs`（播放器初始化超时后摘订阅的后续工作，对应附·A7.4 那批 NRE）。这四份报告的数字全部按 `77ed06a` 的已提交状态写。
- 构建验证均在隔离输出目录（`-p:BuildOutDir=E:\build-temp-dsh\...`）或"关掉占用实例后重跑"两种方式下完成；本机多次出现后台遗留应用实例锁 `bin`，`-t:Rebuild` 会报 MSB3021/MSB3027。有据可查的一次是 `BiliCopilot.UI` PID 13604、`BiliCopilot.Visor` PID 42424，关掉后同一条命令 0 错 0 警通过（NET11_P0_FIXES.md 验证汇总备注、`5eeddeb` 提交说明）。
