# 哔哩助理 · 自用重写版

> 个人自用的 B 站桌面客户端源码存档。不分发安装包，也不面向普通用户发布。

## 先说清楚

- **原项目**：[Bili.Copilot](https://github.com/azpanel/Bili.Copilot)，作者**云之幻**。这个仓库是在原项目基础上给自己用的改造分支，界面骨架、播放内核、登录与数据接口很大一部分仍是原作者的工作。原作者没参与这个分支，这里的问题也不用去找原作者。
- **B 站版权**：视频、音频、封面、弹幕、评论等内容版权归哔哩哔哩及对应 UP 主、版权方所有。这个客户端只处理观看时的界面与播放，不提供内容下载分发，也不做付费内容破解。
- 仅供个人学习与自用，使用本仓库代码产生的后果与原项目作者、哔哩哔哩官方均无关。
- README 已整个换成这份说明，因为代码改动已经很大，挂着原文容易被当成官方发布。

## 这个分支改了什么

### 播放结束页

播完不再停在「底栏还在、按播放键画面不动」的状态，整屏换成结束页：

- **触发**：位置进片尾 0.3 秒内直接切；mpv 停住的位置比时长差零点几秒时，靠一个 700ms 防抖兜底——进末两秒后位置不再推进也切。
- **底栏收起**，只留一根不可拖动的进度条；顶栏右侧那排（画质 / CDN / 空降 / AI 跳过 / 置顶 / 设置）一起隐藏。
- **一行排布**：左侧 UP 头像、昵称、关注按钮，右侧重播、好评、投币、收藏、分享。窗口窄到放不下时按钮组落到第二行，阈值取宽态下量出来的实际宽度，不是写死的数字。
- **互动按钮不另写一套**，复用视频信息面板的 `LikeButton` / `CoinButton` / `FavoriteButton`，计数、三连、收藏夹列表都跟着同一个连接器走。
- **关注两态**：已关注显示对勾 +「已关注」，点一下真实取消并提示；未关注点了真实关注。命令内部吞异常，所以按「状态有没有变」判断成败，不会失败也报成功。
- **分享**：把 `https://www.bilibili.com/video/BV…` 写进剪贴板，底部提示「已经复制视频链接，请自行粘贴」。
- **接着看**：先用播放器自身的推荐分区，没有就回退 `IVideoDiscoveryService.GetRecommendVideoListAsync`，最多三列，随窗口宽度在一到三列间变化。
- **重播**：`seek(0)` + `resume`，复用已加载的媒体。早先走整条重载链路（重解析画质 / CDN / 字幕），实测会卡在「连接中」。

### 搜索结果页

顶栏和分区栏是 Reactor，视频卡片、排序、刷新仍是原 XAML 控件。搜索框居中、宽度与搜索首页一致，进来预填当前关键词；分区栏是 `SelectorBar`，选中项带下划线。窗口宽 ≥1000 DIP 时排序与刷新留在顶栏，窄了并进分区栏那一行。

### 动态页

- **综合动态**：响应式瀑布流，可用宽度 ≥900 DIP 两列、≥1250 DIP 三列；顶部关注 UP 头像条可按人筛选。
- **直播分区**（`SelectorBar` 第三段）：先出关注主播**正在直播**的房间，几秒后往下补**直播回放 / 录播**。
  - 在播列表自己拼：拉关注分组里的用户 → 批量问 `room/v1/Room/get_status_info_by_uids` → 取 `live_status=1` 的房间；缺主播名或头像的补一次 `getInfoByRoom` 拿 `anchor_info.base_info`。
  - 回放走「关注 UP 主的投稿动态」再按标题筛「直播回放 / 录播」。不用全站搜索——搜出来全是大主播，关注的人不在结果里（实测 0 命中）。
  - 卡片区是 `ItemsRepeater` + `UniformGridLayout`：`ItemsStretch=None` 让卡片保持内容高度（行高由该行最高卡片决定，互不拉伸），`MinItemWidth` 在代码里按可用宽度算，所以列宽均分、右侧不留空白。
  - 加载分两段：第一段只拉关注分组 + 开播状态，直播卡先出、界面立刻可用；第二段扫投稿补回放。补卡片走**增量追加**（只 `Items.Add` 新卡、只往头像条尾部加新作者），不整页重建。
  - 角标：`直播中` 实心 accent，`回放` outline。点卡片进播放，右下角 Globe 用浏览器打开直播间 / 视频页。

### AI 分析

同一套「OpenAI 兼容接口」用在三处：空降助手的广告识别、动态「已看过」的观看兴趣、稍后再看 / 历史顶部的兴趣条。

**输出格式是硬的**，不靠模型自觉：

- 提示词里写死 `<<<兴趣>>> … <<<END>>>` 标记，要求只输出这一段、不超过设定字数，禁止解释、标题、分点、罗列视频名、Markdown、引号。
- 解析端只取标记之间的内容；模型不按格式来时退回 `【结论】` / `结论：` 之后的文字，再退回按中文句子拼接。
- 拿到正文后先清洗（去行首列表符、多余空行、包裹引号），再按设置的字数上限硬裁，优先在句号处收口。
- 字数上限在设置页可调（20~200，默认 60），同时写进提示词和裁剪逻辑——提示词管方向，解析管兜底。

**顶部兴趣条**（稍后再看 / 历史）：顶栏下面一条圆角矩形，左边图标、右边一段话，取该页前 20 个视频的标题、UP 主、封面送分析；没配接口或没数据时整条隐藏。同一页面进程内只请求一次，结果缓存到退出。

**没做的**：收藏页的兴趣条还没接（主体在 `FavoritesPageMainBody` 里按子分区渲染，外壳层拿不到当前视频收藏分区的集合）；「视频字幕前 200 字」也还没抓，现在只送标题 / UP 主 / 封面，字幕要逐视频请求，得配并发限制和降级。

### 其它界面改造

- **评论面板**：整块 Reactor 化，主评论与「N 条回复」共用一套卡片，排序、点赞、回复、加载更多都在。
- **顶栏与侧边栏**：搜索框居中，收藏 / 稍后再看 / 历史移到左侧，右侧只留窗口按钮；头像放进 PaneFooter，折叠态也能点搜索图标展开并聚焦搜索框。
- **搜索页背景**：右下角「编辑背景」，可导入 B 站主题装扮的 JSON（自动下装扮包、解出静态图与 mp4），也能直接传本地图片或视频；导入过的留在列表里带缩略图，叉号删除时连资源目录一起清掉，背景可设成透明。

### 播放与内容能力

- **播放内核**：mpv 为主，WebView2 + Shaka 回落；mpv 初始化失败自动降级。
- **画质解锁**：`Services/QualityUnlockService`，按 `avid` / `cid` / `fn=0` 走 wbi 签名调 `ogv/player/playview`（`api/bzview3`），失败退回常规地址，不阻塞播放。
- **CDN 优选**：节点池同步自外部 `cdn.json`，右上角和右键菜单都能手动指定或自动优选。
- **直播**：右侧互动面板响应式（展开时播放器让位）；弹幕走自研 WebSocket 通道；底栏漏斗可按「等级 < 3」「没有粉丝牌」过滤普通弹幕，礼物、醒目留言、上舰、进场不受影响。
- **空降助手**：SponsorBlock 兼容服务 + 跳转点数据 + AI 识别三层叠加，默认自动跳过，可切手动（左下角药丸按钮），静音类片段压低音量而不是跳过。

### 稳定性工程

这一块是后来补的，起因是反复出现「编译通过、启动就崩或卡住」：

- **启动检查点**（`Services/StartupTrace.cs`）：写到 `bin\startup-trace\startup-YYYYMMDD.log`，故意不放包数据目录——设置坏掉时正是最需要日志的时候。记录时间、PID、阶段、版本、Git SHA、异常类型 / 消息 / HRESULT / InnerException 链，覆盖 `OnLaunched` 各阶段 → `MainShell` → `MomentShell` → 卡片控件 → `STARTUP-OK`。日志不含 Cookie / Token / 用户内容。
- **启动成功标记**：`startup-ok.txt` 在首屏内容挂载后写入，Smoke Test 只认这个，不看进程是否存活。不能等 `Window.Activated`——从自动化方式拉起时窗口可能一直不激活。
- **Smoke Test**（`tools/smoke-test.ps1`）：结束旧进程 → 构建 → 注册并核对「安装位置 == 当前产物」→ 启动等 `startup-ok` → UIA 检查导航 → 正常关闭 → **再启动一次** → 收集日志。退出码区分构建失败(2) / 部署失败(3) / 首次启动失败(4) / 第二次启动失败(5) / UI 无响应(6) / 环境问题(7)，失败保留现场，不删用户数据。
- **界面状态改存本地文件**（`Services/AppStateStore.cs`）：上次分区、通知基线这些原来写在 `LocalSettings`，切分区、轮询都写一次，把包设置状态容器写脆了。现在存 `LocalState\app-state.json`，写坏删掉即可。
- **自动更新注册**：`tools/run-app.ps1` 每次构建后递增清单版本号再 `Add-AppxPackage -Register`，不卸载重装（卸载会连应用数据一起清掉）。

### 顺手修掉的

- **动态页顶部 UP 头像条**点了没反应：`OnControlLoaded` 在 ViewModel 为 null 时直接解引用，空引用把后面挂 `SelectionChanged` 的代码整段挡掉；订阅拆成 Attach / Detach。
- **收藏 / 稍后再看 / 历史记录的返回键**要按好几次：`OverlayFrame.NavigateTo` 是往栈里加页，两个列表长得像，看着像没动；这三个入口页点返回直接清栈退出。
- **评论回复按钮**点了没反应：命中区放大、命令创建时绑定，点完把 `@用户名 ` 写进输入框并抢焦点。
- **搜索结果页切分区不生效**：给 `SelectorBar` 传固定 `selectedIndex` 等于受控模式，点击被立刻改回；内容宿主又没 key，旧分区还留在树上。改成 `Optional<int>.Unset` + 内容宿主按分区类型加 key。
- **搜索框建议浮层**是 Popup，跳结果页后仍浮在最上层吃掉点击；结果页那个实例直接摘掉 `ItemsSource` 并每次渲染压一遍。
- **视频标签点击**：原来 `SearchCommand.Execute(string)` 触发参数类型异常（命令签名是 `IAsyncRelayCommand<SearchSuggestItemViewModel>`），改成走 `NavigationViewModel.Search(tag.Name)`。
- **直播卡片首行被切**：`ScrollViewer` 初始偏移不为 0，`Loaded` 后 `ChangeView(null, 0, null, true)` 归零。

## 代码规模与 Reactor 进度

口径是 `git ls-files` 那批文件的实际行数，排除 `obj` / `bin` / `_build`，按钮控件（`LikeButton` / `CoinButton` / `FavoriteButton` / `DownloadButton` / `CommentLikeButton`）算进 XAML。

| 分类 | 文件 | 行数 |
| --- | ---: | ---: |
| XAML | 241 | 18,824 |
| XAML 后台代码（`.xaml.cs`） | 230 | 12,124 |
| Reactor 外壳（`Reactor/*.cs`） | 16 | 4,186 |
| 全部 C# | 704 | 58,083 |

| 对比 | 计算 | 结果 |
| --- | --- | ---: |
| Reactor / XAML | 4,186 ÷ 18,824 | **22.2%** |
| Reactor / 界面层（XAML + 后台） | 4,186 ÷ 30,948 | **13.5%** |
| Reactor / 全部 C# | 4,186 ÷ 58,083 | **7.2%** |

按「界面落点」数（外壳、每个页面、播放页每个侧栏面板、评论 / 弹幕 / 直播互动面板、卡片布局、设置卡片、播放器控制层各算一个）：

| 区域 | 已迁移 | 总数 | 进度 |
| --- | :---: | :---: | --- |
| 应用外壳 | 1 | 1 | `██████████` 100% |
| 页面 | 12 | 21 | `██████░░░░` 57% |
| 播放页侧栏 | 4 | 4 | `██████████` 100% |
| 评论 / 弹幕 / 直播互动面板 | 3 | 4 | `███████░░░` 75% |
| 视频卡片布局 | 2 | 2 | `██████████` 100% |
| 设置卡片 | 0 | 20 | `░░░░░░░░░░` 0% |
| 播放器控制层（含结束页） | 0 | 1 整块 | `░░░░░░░░░░` 0% |

合计 **22 / 53 ≈ 42%**。

**已挂 Reactor 外壳的页面**：动态、消息、直播分区、历史、收藏、稍后再看、设置、流行、视频分区、动漫、影视、搜索结果。**仍是纯 XAML**：启动页、图库、搜索首页、文章分区、文章阅读、个人空间、关注、粉丝。

**刻意没动的**：20 个设置卡片（排版交给 `SettingsCard` / `SettingsExpander`，复刻容易走样）；播放器控制层整块（控制栏、右键菜单、字幕与着色器面板、直播弹幕浮层，加上结束页 `PlaybackEndPanel.xaml` + `PlayerViewModel.EndScreen.cs`），这块和播放器生命周期强绑定。

## 技术栈

- .NET 10 / C# / WinUI 3（Windows App SDK 2.6.5），目标框架 `net10.0-windows10.0.22621.0`
- Microsoft.UI.Reactor 0.1.0-preview.17
- CommunityToolkit.Mvvm 8.2.2（`[ObservableProperty]` / `[RelayCommand]`）
- BiliKernel（原项目的数据与接口层）
- mpv（Richasy.MpvKernel）

## 构建与运行

```powershell
git clone https://github.com/TravelLeobai/Bili.Copilot.git
cd Bili.Copilot/src
dotnet build Desktop\BiliCopilot.UI\BiliCopilot.UI.csproj -c Debug -p:Platform=x64
```

需要 Windows 11、.NET 10 SDK，以及 **Windows App Runtime 2.6**（[Windows App SDK 下载页](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads)；也可以把项目改成自包含部署）。版本号在 `src/Directory.Packages.props` 的 `Microsoft.WindowsAppSDK`。

仓库里带两个脚本，代替手工「编译 → 注册 → 启动 → 点两下」：

```powershell
# 构建 + 递增版本 + 注册 + 启动（不卸载、不清数据）
pwsh -File tools\run-app.ps1 [-NoBuild] [-CleanData]

# 构建 + 注册 + 启动两次 + UIA 检查，失败返回非零
pwsh -File tools\smoke-test.ps1 [-NoBuild] [-Reinstall]
```

踩过的构建坑：

- `MSB3027`（文件被占用）：先结束 `BiliCopilot.UI` / `MSBuild` / `VBCSCompiler`，等十来秒再编。
- XAML 编译偶发 PRI 报错：删掉 `_build\x64\Debug\BiliCopilot.UI\bin\resources.pri` 重来。
- 注册报 `0x80073D06`（已安装更高版本）：清单版本号要大于已安装版本；`smoke-test.ps1` 会自动把产物清单提到「已安装 + 1」。
- 发 Release 又不需要 AOT：加 `-p:PublishAot=false`，否则撞 `Platform linker not found`。

## 配置与密钥

仓库里不含任何接口密钥、Cookie 或账号凭据。AI 接口地址、模型、密钥、字数上限、SponsorBlock 服务器地址都在应用设置页里填，只写到本机设置与 `LocalState\app-state.json`，不会进版本库。要用空降助手的 AI 识别和观看兴趣分析，得自己填一个兼容 OpenAI 协议的接口。

## 已知问题

- **别用 `Copy-Item` / 备份还原去搬包数据目录**：`LocalState` / `Settings` 里的文件带包身份的 EFS 状态，外部拷贝会破坏它，之后读写抛 `0x80070199`（未按预期加密）或启动即崩。要重置就整个删掉让系统重建，别往回拷。
- **设置状态容器会坏**：事件日志里是 `AppModel-State` 的 `SettingsInitialize` 失败（`80070003` → `80073DB8`），表现是启动后退到登录页、或启动约十秒后进程消失（`combase.dll` + `0x80004003`）。应用自带兜底：启动记 `startup-guard.txt`，连续两轮没走到「窗口起来」就把 `Settings` 挪成 `Settings-broken-<时间>` 用全新设置继续。彻底重建则卸载包、删掉包数据目录、重新注册，代价是重新扫码。
- **写设置太频繁会把 token 写丢**：扫码成功后又被踢回登录页，原因是启动时初始化某个设置属性也触发了一次写，和 token 落盘撞在同一份文件上。现在所有「值没变就不写」，界面状态也搬到 `app-state.json`。再遇到同类问题，优先查「谁在启动路径上写设置」。
- **`DefaultStyleOptimizations` 实验开关默认关闭**：WinUI 自带的控件样式优化开关，走 `AppContext.SetSwitch`，本机实测打开后启动不稳，代码里留了注释。
- **画质解锁有额度**：走第三方登记服务，额度用完返回 `code=9`，此时自动退回常规地址，不影响播放。
- **收藏页兴趣条、字幕采样还没做**：见上文「AI 分析 → 没做的」。

## 许可

沿用原项目的 LICENSE。代码可读可学，但要拿去发布、改名上架或商用，请先满足原项目的许可条件并取得哔哩哔哩相关授权——这不是本仓库能替你解决的事。

---

给云之幻：谢谢你写的 Bili.Copilot，没有它这个分支无从谈起。
