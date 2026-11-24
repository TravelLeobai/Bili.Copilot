# P1·网络与图片热路径治理（承接 NET11_HOTSPOTS.md §P1-2 / §P1-3 / §P1-4）

> 分支 `exp/net11-optimization`（**未 push**）· 2026-11-24 · 两批提交：`1372af1`（图片）、`b41a978`（直播消息）
> 只做「复用已存在的共享客户端 + 去掉中间分配」，不引入 `IHttpClientFactory`、不做架构改造。

## A. `new HttpClient()` 治理

### 改动前后（实测当前树，`grep -rn "new HttpClient" src --include=*.cs`）

| # | 站点 | 频率语义 | 处置 |
|---|---|---|---|
| 1 | `Controls/Markdown/TextElements/MyImage.cs:100` | 每张 markdown 图 | **本批已改**：复用 `InternalHttpExtensions.ImageClient`（静态单例，带 UA / GZip+Brotli / HTTP2） |
| 2 | `Controls/Comment/CommentTextBox.xaml.cs:269`（10s） | 每次插入表情 | 待改（不在本任务文件范围） |
| 3 | `Services/Live/LiveFollowService.cs:51`（15s）/`:112`（12s）/`:238`（12s） | 关注列表刷新 | 待改（同上） |
| 4 | `ViewModels/Core/NotificationViewModel/NotificationViewModel.Live.cs:176`（15s） | 开播轮询 | 待改（同上） |
| 5 | `ViewModels/Core/PlayerViewModel/PlayerViewModel.cs:1245`（15s）/`:1286`（12s） | 字幕拉取 | **明令不动**（engineer 正在改该目录） |
| 6 | `ViewModels/Core/PlayerViewModel/PlayerViewModel.Live.cs:881`（8s） | 粉丝数刷新 | **明令不动**（同上） |
| 7 | `ViewModels/View/GalleryPageViewModel/GalleryPageViewModel.cs:115`/`:134` | 保存图片 / 设壁纸 | 待改（不在本任务文件范围） |
| 8 | `Services/BackgroundLibrary.cs:247`（全限定名，无超时） | 导入主题 | 待改（同上） |

合计 **12 处**，与 `NET11_HOTSPOTS.md` §P1-4 的索引一致；本批改掉 1 处，剩 11 处（其中 3 处属任务明令不碰的 `PlayerViewModel/**`）。

### 本来正确的静态工厂：22 处，一处未动

`grep -rn 'static readonly HttpClient'` 命中 22 个字段：12 处经 `CreateClient()` / `CreateHttpClient()` / `GetImageClient()` 工厂初始化，10 处直接 `new(new SocketsHttpHandler { … })`（`AiEndpointTester` / `AiInterestAnalyzer` / `BiliEmoteService` / `BiliJumpService` / `CdnPoolService` / `DynamicShareService` / `BiliWbiSigner` / `QualityUnlockService` / `SeasonSubscriptionService` / `SubtitleSampler`）。`CommentVoteService` 的 client 建在构造函数里、由 `Shared` 单例持有，也不动。

> 与 t3 的「16 处静态工厂」口径差在计数方式（t3 是语义扫描的 `NEW_HTTPCLIENT` 节点数）；按字段口径实测是 22。

### 待改站点的统一改法（给下一批用）

这些站点的差异只在 `Timeout`，改法固定三步，**不在共享 client 上改 `Timeout`**：

```csharp
// 改前
using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
var resp = await client.GetAsync(url);

// 改后（共享 client + 每次调用自己的取消源当超时）
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
using var resp = await SharedClient.GetAsync(url, cts.Token);
```

选哪个共享 client 取决于协议需求：图片走 `InternalHttpExtensions.ImageClient`（带浏览器 UA，`UseCookies=false`）；只要 JSON/接口的走 `IpV4Connector` 那一族的静态 client（强制 IPv4）。**没有一刀切的单例**，因为 UA / 证书回退 / IPv4 这三条会改变请求行为。

## B. 图片解码

`Converters/UriToBitmapConverter.cs`（App.xaml:64 注册为 `BitmapConverter`，3 个调用点：`AppSearchBox` 16px 图标、`PlaybackEndPanel` 64px 头像、`GalleryPage` 可缩放大图）

- **改前**：`value is string url ? new BitmapImage(new Uri(url)) : …` —— 每次绑定求值都新建一张、不设 `DecodePixelWidth`、不复用，等于把原图按原始尺寸解码一遍。
- **改后**：按 `URL + 解码宽度` 走 24 条 LRU 复用同一张 `BitmapImage`（命中直接返回实例，不再 decode）；`DecodePixelWidth` 在 `UriSource` **之前**赋值（地址一设解码就排队，之后再给就晚了）；支持 `ConverterParameter` 传目标像素宽（16–4096）；订阅 `MemoryManager.AppMemoryUsageIncreased` 清表，与 `SmallImageCache` 同一套处理。
- 3 个调用点没传 `ConverterParameter`（XAML 不在本任务文件范围），所以默认仍是原尺寸解码——**下次给这三处各加一个参数就是完整收益**：`Converter={StaticResource BitmapConverter}, ConverterParameter=32`（图标）、`128`（头像）；`GalleryPage` 保持不传。

`Controls/Markdown/TextElements/MyImage.cs`（正文内联图，真正的热路径）

- **改前**：`:100` 每张图一个 `HttpClient`（从不 Dispose）；`:123` 建 `BitmapImage` 后直接 `SetSourceAsync`，`:135` 才读 `PixelWidth` —— 解码已经按原图尺寸做完，`DecodePixelWidth` 来不及生效。
- **改后**：复用 `ImageClient`；`ResponseMessage` 补 `using`；`bitmap.DecodePixelWidth = ResolveDecodePixelWidth()` 写在 **`SetSourceAsync` 之前**。宽度取值：markdown/HTML 给了显示宽度就按 2 倍 DPI 换算（夹在 16–2048），没给就套 1024 上限。`DecodePixelWidth` 只缩小不放大，所以对小图无副作用。
- 依据：`DecodePixelWidth` 只在解码那一刻生效，`SetSourceAsync`/`UriSource` 之后设置无效——[BitmapImage.DecodePixelWidth odd behavior](https://stackoverflow.com/questions/14680078/bitmapimage-decodepixelwidth-odd-behavior)、[How to quickly load and display images with WPF/BitmapImage?](https://stackoverflow.com/questions/39495099/how-to-quickly-load-and-display-images-with-wpf-bitmapimage)、[Using Image Source with big images in WPF](https://stackoverflow.com/questions/2504359/using-image-source-with-big-images-in-wpf)。仓库内 `Extensions/SmallImageCache.cs:64-67` 就是「先地址、立刻给解码宽、之后才解码」的现成正确写法。

## C. 直播消息接收路径（`Services/Live/LiveDanmakuClient.cs`）

| 位置（改前） | 现状 | 改后 |
|---|---|---|
| `:256` | `stream.ToArray()`，每条 WS 消息整块拷贝 | 消息缓冲 `byte[]` 跨包复用，切片 `ReadOnlyMemory` 交给解析；解析 `await` 返回后才复用，不会踩数据 |
| `:286-287` | 每包 `new byte[bodyLength]` + `BlockCopy` | `data.Slice(offset + headerLength, bodyLength)`，零分配 |
| `:311` | 解压输入 `new MemoryStream(byte[])` | `ReadOnlyMemoryStream` 只读视图（`CanSeek=false`），少一次包体拷贝 |
| `:359` | 解压输出 `output.ToArray()` | `GetBuffer()` + 有效长度，少一次拷贝 |
| `:327` | `Encoding.UTF8.GetString(body)` → string | 删除 |
| `:328` | `JsonDocument.Parse(json)`（吃 string） | `JsonDocument.Parse(ReadOnlyMemory<byte>)`，直接吃 UTF-8 字节 |
| `:88-89` | `Task.Run(() => ReceiveLoopAsync(...))` | 直接 `_receiveTask = ReceiveLoopAsync(...)`（两个循环上来就 `await`，不需要线程池跳转） |

每条弹幕的分配从「body 数组 + string + JsonDocument + Clone」降到「JsonDocument + Clone」；每个压缩包从「body 数组 + 解压输入拷贝 + 输出 ToArray 拷贝」降到「解压缓冲一次分配」。

**行为不变**：事件契约仍是 `(string Command, JsonElement Data)`，`root.Clone()` 保留（消费者在 `PlayerViewModel.Live.cs:432` 里调 `GetRawText()` / `TryGetProperty`，且 `LiveMessageParser.Parse` 靠它分发弹幕 / 礼物 / SC），字段名与容错路径一处未改；`protover 2/3` 的递归解压、`op=8` 认证、心跳包格式均照旧。唯一语义差异：非法 UTF-8 现在是 `JsonDocument.Parse` 抛异常走 `catch` 记 Debug（原来会用 U+FFFD 替换后尝试解析），B 站弹幕流是合法 UTF-8，不影响。

## 验证

### 构建

| 命令 | 结果 |
|---|---|
| `dotnet build …BiliCopilot.UI.csproj -c Debug -p:Platform=x64`（含 `SolutionDir`） | **0 警告 0 错误**，EXIT=0（20:19，提交 `1372af1`+`b41a978` 之后） |
| 同上 + `-p:OutDir=…/_build/_eng2check/`（隔离输出，避开另一 agent 占用 `_build`） | **0 警告 0 错误**（20:16） |

> 期间两次失败与代码无关：一次是 `MSB3021/MSB3027`（主输出目录的 `BiliCopilot.UI.exe` 被另一个 agent 启动的实例锁住），一次是另一位 agent 正在改 `PlayerViewModel/**`，`PlayerMethods.cs` / `PlayerWindow.cs` 处于半成品（`CS0103 RefreshTickPathCache` 等 5 条）。都不是本任务文件。

### 直播间（含弹幕 / 礼物 / SC 路径）

实例：`Richasy.BiliCopilot.Dev_g7ew4fxhv1f46`，`tools/verify-live.ps1` 的流程改成鼠标点击（该构建里导航项的 `SelectionItemPattern.Select()` 返回 `RPC_E_SERVERFAULT`，UIA 走不通）。进入直播间后日志 `LocalState\Logger\log-2026-11-24.txt`：

```
20:22:03 [INF] 弹幕服务器响应长度 956
20:22:03 [INF] 弹幕包：op=8 proto=1 len=26          ← 认证回包
20:22:04 [INF] 弹幕包：op=5 proto=3 len=673         ← brotli 压缩包（走新 ReadOnlyMemoryStream 解压）
20:22:04 [INF] 弹幕包：op=5 proto=0 len=1598        ← 未压缩包（走切片解析）
20:22:04 [INF] 弹幕消息：ENTRY_EFFECT
20:22:04 [INF] 弹幕消息：ONLINE_RANK_V3
20:22:04 [INF] 弹幕消息：INTERACT_WORD_V2
20:22:04 [INF] 弹幕消息：ONLINE_RANK_COUNT
20:22:05 [INF] 弹幕原始样本 DANMU_MSG：{"cmd":"DANMU_MSG","dm_v2":"","info":[[0,1,25,65532,… 
20:32:55 [INF] 表情弹幕样本：{"cmd":"DANMU_MSG",…    ← 消费者用 GetRawText() 找 emoticon_unique，说明 Clone() 后的 JsonElement 可用
20:22:20→20:22:48 [INF] 计时[baseline] 直播弹幕列表构建：0 页 41→113 条
```

`直播弹幕列表构建 … 113 条` 是 `LiveInteractions` 的条数，只有 `LiveMessageParser.Parse(cmd, data)` 成功返回才会累加 —— 即「包体字节 → JsonDocument → Clone → 分发 → 解析成互动项 → 上屏」整条链走通。整场没有一条 `直播弹幕解压失败` / `直播弹幕 JSON 解析失败`（Debug 级，若发生会落在同一文件）。

侧栏实拍（互动面板：用户名 + 粉丝牌 + 进场提示 + 弹幕正文）见会话内 `_eng2-comment.png` / `_eng2-live.png`；礼物与 SC 在这 25 秒里没出现（该房间当时无礼物/SC 事件），**这两类未拿到真实样本**——但它们的入口同为 `op=5` 包 → `Parse` → `LiveMessageParser`，本批没有改动 `LiveMessageParser`，字段名一处未动。

### 视频评论区（登录恢复后补测，2026-11-24 21:33–21:55）

登录态已恢复（engineer 用 `Settings-broken-203705\settings.dat` 快照覆盖回去，**不是**重注册包）。日志：

```
21:33:31 [INF] 访问令牌：已就绪 access_token=c59d**** uid=177800289 剩余有效期≈4290.6h
21:33:39 [INF] 访问令牌：已就绪 …
21:33:46 [INF] 访问令牌：已就绪 …
21:37:30 [INF] 访问令牌：已就绪 …          ← 本轮冒烟启动时的登录态
```

启动方式：**当前已注册包**（`shell:AppsFolder\Richasy.BiliCopilot.Dev_g7ew4fxhv1f46!App`，版本 1.0.4.0），未重注册、未换版本、未碰 `src\_build`。

#### 实测（拿到实证）

| 项 | 结果 | 证据 |
|---|---|---|
| 登录态 + 进主窗口 | ✅ | 21:33–21:37 四行 `访问令牌：已就绪 uid=177800289` |
| 我的收藏页加载 | ✅ | 21:41 打开，卡片网格正常（会话截图 `_t12-fav.png`），22 张卡片标题可读 |
| 打开视频并播放 | ✅ | 21:42 点击收藏卡片 → 播放器窗口标题 `以防你不知道GTA6路人是如何暴富的??`，视频在播、弹幕浮层正常（`_t12-player-windowed.png`） |
| 视频详情面板 | ✅ | 21:44 点播放器右下角按钮唤出详情页：UP 头像 + 昵称 + `关注`、重播/点赞/投币/收藏/分享计数、`接着看` 推荐列表（`_t12-probe-br.png`） |
| **图片路径（带图内容）** | ✅ | 上条详情页里 UP 头像与 5 张推荐封面全部正常渲染，**没有占位块/裂图**；日志中无 `UriToBitmap` / `BitmapImage` / 解码相关告警 |
| 评论列表渲染 | ❌ **未拿到** | 见下 |

#### 未验证（说清卡在哪）

评论列表**没有取到证**。`视频评论区` 挂在 `Controls/Player/VideoPlayerPanel.xaml`（360 宽的 `VideoPlayerSideBody` → `PlayerCommentSection`），由 `PlayerOverlay.xaml:263` 的 `OverlayPanel IsShow="{x:Bind ViewModel.IsExtraPanelVisible}"` 控制显隐。问题在自动化可达性：

- 播放器窗口的 UIA 树只有 **9 个节点**（标题栏 3 个按钮 + mpv 的 `Pane` + 非客户区），Reactor 渲染的控件一个都不暴露，所以拿不到「评论」Tab 的 `AutomationId`/名字，`InvokePattern`/`SelectionItemPattern` 都用不上；
- 用坐标点击右下角按钮只把播放器切到了「视频详情」页（上表第 4 行那张图），**不是**那个 360 宽侧栏；再点一次会切回去；
- 在详情页里滚轮下滚 8 格画面无变化（该页不吃滚轮），到不了评论列表。

要补这一条，最省事的是**人工在应用里点一下**：播放器 → 右下角「详情」按钮 → 侧栏顶部的 `评论` Tab，然后我照一张图 + 收日志即可（约 1 分钟）。之所以没这么做：21:45 之后 `verifier` 要独占 UI 跑 t14 的播放项（冷启动 ×3 / 播 75 秒 / 播放器开关 30 轮），我已把窗口让出。

代码侧：`Services/CommentVote/**`、`ViewModels/Components/Comment*/**`、`Reactor/CommentPanel.cs` 本批一处未动；评论区头像走 `SmallImageCache`，不走本批改的 `UriToBitmapConverter`；`MyImage` 只用于 markdown 正文（AI 回复 / 文章阅读），也不在评论区路径上。所以缺的是「整页冒烟的最后一步」，不是「本批改动的回归」——详情页的图片渲染已经覆盖了本批改动的那条解码链。

#### 这次会话里唯一一条与图片沾边的日志（不是本批引入）

```
21:34:01 [INF] 兴趣分析含图片失败，改用纯文本重试
21:34:01 [WRN] 兴趣分析返回内容为空：{"role":"assistant","content":"",…}
```

走的是「观看兴趣分析」把封面图一起发给模型的那条路（`AiInterestAnalyzer`），失败后自己降级成纯文本重试，属于模型侧返回空内容，与本批的 `UriToBitmapConverter` / `MyImage` 无关（二者都不在这条链上）。会话 21:33–21:55 共 1929 行日志里，`UriToBitmap` / `BitmapImage` / `DecodePixel` 相关告警 **0 条**。

另外顺带看到 `[WRN] 处理播放器属性变更时出错：Position`（21:33:27，成串出现）——属播放器属性回调那条线（P0-1 / `69785b5`），不在本任务范围，仅记一笔给 t6/t9 汇总时核对。



## 明确没做的事

- 没换 `IHttpClientFactory`（要动 DI 容器与 22 个静态工厂，收益是 DNS 轮换与统一遥测，与本批「最小改动」相悖）。
- 没动 `MyImage` 的 `async void LoadImage` 与 `_loaded` 只在 else 分支置位的问题（属 P1-1 async void 线）。
- 没动 `PlayerViewModel/**`、`Services/CommentVote/**`、`Reactor/**`。
