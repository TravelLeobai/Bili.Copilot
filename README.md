# 哔哩助理 · 自用重写版

> 给自己用的 B 站桌面客户端源码。不分发安装包，也不面向普通用户发布。

## 这是个什么项目

上游是 [Bili.Copilot](https://github.com/azpanel/Bili.Copilot)（作者**云之幻**）。这个仓库是我在它基础上按自己的使用习惯改出来的分支：界面骨架、播放内核、登录与数据接口有很大一部分仍是原作者的工作。原作者没参与这个分支，这里的问题也不该去找他。

改动集中在四块：播放体验（结束页、切集、画质增强）、动态页（瀑布流 + 直播分区）、AI 分析（观看兴趣）、以及一套围绕「编译过了但跑不起来」搭起来的诊断与压测工具。

## 目录

- [免责与边界](#免责与边界)
- [功能改动](#功能改动)
- [稳定性工程](#稳定性工程)
- [代码规模与 Reactor 进度](#代码规模与-reactor-进度)
- [技术栈](#技术栈)
- [构建与运行](#构建与运行)
- [配置与密钥](#配置与密钥)
- [已知问题](#已知问题)
- [许可](#许可)

## 免责与边界

- **内容版权**：视频、音频、封面、弹幕、评论归哔哩哔哩及对应 UP 主、版权方所有。这个客户端只处理观看时的界面与播放，不提供内容下载分发，不做付费内容破解。
- **个人自用**：仅用于个人学习与自用。使用本仓库代码产生的后果与原项目作者、哔哩哔哩官方无关。
- **README 已整体重写**：代码改动已经离上游很远，挂着原文容易被当成官方发布。

## 功能改动

### 播放结束页

播完不再停在「底栏还在、点播放也没反应」的状态，整屏换成结束页。

- **触发**：位置进片尾 0.3 秒内直接切；mpv 停住的位置比时长差零点几秒时，靠 700ms 防抖兜底——进末两秒后位置不再推进也切。
- **布局**：底栏收起只留一根不可拖的进度条；顶栏右侧那排（画质 / CDN / 空降 / AI 跳过 / 置顶 / 设置）隐藏。左侧 UP 头像、昵称、关注按钮，右侧重播、好评、投币、收藏、分享。窗口窄到放不下时按钮组落第二行，阈值取宽态实测宽度，不是写死的。
- **互动按钮不另写**：复用视频信息面板的 `LikeButton` / `CoinButton` / `FavoriteButton`，计数、三连、收藏夹列表走同一个连接器。
- **关注两态**：已关注显示对勾并真实取消；未关注点了真实关注。命令内部吞异常，所以按「状态有没有变」判断成败，不会失败也报成功。
- **分享**：把 `https://www.bilibili.com/video/BV…` 写进剪贴板，播放窗口内提示。
- **接着看**：优先用播放器自身推荐分区，没有就回退 `IVideoDiscoveryService.GetRecommendVideoListAsync`，最多三列，随窗口宽度在一到三列间变化。
- **重播**：`seek(0)` + `resume`，复用已加载的媒体。早先走整条重载链路（重解析画质 / CDN / 字幕），实测卡在「连接中」。

### 动态页

**综合动态**：响应式瀑布流，可用宽度 ≥900 DIP 两列、≥1250 DIP 三列；顶部关注 UP 头像条可按人筛选。

**直播分区**（`SelectorBar` 第三段）：

- 先出关注主播**正在直播**的房间，几秒后往下补**直播回放 / 录播**。
- 在播列表自己拼：拉关注分组 → 批量问 `room/v1/Room/get_status_info_by_uids` → 取 `live_status=1`；缺主播名或头像的补一次 `getInfoByRoom` 拿 `anchor_info.base_info`。
- 回放走「关注 UP 主的投稿动态」再按标题筛「直播回放 / 录播」。不用全站搜索——搜出来全是大主播，关注的人不在结果里（实测 0 命中）。
- 卡片区是 `ItemsRepeater` + `UniformGridLayout`：`ItemsStretch=None` 保留内容高度，`MinItemWidth` 按可用宽度动态算，列宽均分、右侧不留空白。
- 加载分两段：第一段只拉分组 + 开播状态，直播卡先出、界面立刻可用；第二段扫投稿补回放。补卡片走增量追加，不整页重建。
- 卡片外观沿用内核 `CardControl`（描边、圆角、悬停底色都由它给），和动态里其他视频卡片同源；悬停上浮用 `RenderTransform` 平移，不动布局。角标 `直播中` 实心、`回放` outline。点卡片进播放，右下角 Globe 用浏览器打开。

### AI 分析

同一套 OpenAI 兼容接口用在三处：空降助手的广告识别、动态「已看过」的观看兴趣、稍后再看 / 历史顶部的兴趣条。

**输出格式是硬的**，不靠模型自觉：

- 提示词写死 `<<<兴趣>>> … <<<END>>>` 标记，只准输出那一段、不超过设定字数，禁止解释、标题、分点、罗列视频名、Markdown、引号。
- 解析端只取标记之间；模型不按格式来时退回 `【结论】` / `结论：` 之后的文字，再退回按中文句子拼接。
- 拿到正文先清洗（去行首列表符、多余空行、包裹引号），再按字数上限硬裁，优先在句号处收口。
- 字数上限在设置页可调（20~200，默认 60），同时写进提示词和裁剪逻辑——提示词管方向，解析管兜底。

**顶部兴趣条**（稍后再看 / 历史）：顶栏下一条圆角矩形，左图标右一段话，取该页前 20 个视频的标题、UP 主、封面送分析；没配接口或没数据时整条隐藏。同页进程内只请求一次。

**没做的**：收藏页的兴趣条（主体在 `FavoritesPageMainBody` 里按子分区渲染，外壳层拿不到当前视频收藏分区的集合）；「视频字幕前 200 字」的采样。

### 其它界面

- **搜索结果页**：顶栏与分区栏是 Reactor，视频卡片、排序、刷新仍是 XAML 控件。搜索框居中，进来预填关键词；分区栏 `SelectorBar` 选中带下划线；窗口 ≥1000 DIP 时排序与刷新留顶栏，窄了并进分区栏那一行。
- **评论面板**：整块 Reactor 化，主评论与「N 条回复」共用一套卡片。
- **顶栏与侧边栏**：搜索框居中，收藏 / 稍后再看 / 历史移左侧，右侧只留窗口按钮；头像放进 PaneFooter，折叠态也能点搜索图标展开并聚焦。
- **搜索页背景**：可导入 B 站主题装扮 JSON（自动下装扮包、解出静态图与 mp4），也能直接传本地图片或视频；导入过的带缩略图，删除时连资源目录一起清；背景可设透明。

### 播放能力

- **内核**：mpv 为主，WebView2 + Shaka 回落；mpv 初始化失败自动降级。
- **画质解锁**：`Services/QualityUnlockService`，按 `avid` / `cid` / `fn=0` 走 wbi 签名调 `ogv/player/playview`，失败退回常规地址，不阻塞播放。
- **CDN 优选**：节点池同步自外部 `cdn.json`，右上角与右键菜单都能手动指定或自动优选。
- **直播**：右侧互动面板响应式（展开时播放器让位）；弹幕走自研 WebSocket 通道；底栏漏斗可按「等级 < 3」「没有粉丝牌」过滤普通弹幕，礼物、醒目留言、上舰、进场不受影响。
- **空降助手**：SponsorBlock 兼容服务 + 跳转点数据 + AI 识别三层叠加，默认自动跳过，可切手动，静音类片段压低音量而不是跳过。
- **着色器与超分**：Anime4K / ArtCNN / NNEDI3 / RAVU 与 NVIDIA VSR，设置里可记住选择；每次媒体加载完会强制再应用一次（开关关闭则什么都不做）。

## 稳定性工程

这一块是后来补的，起因是反复出现「编译通过、启动就崩或卡住」，以及多次修复之间互相踩。

### 启动检查点

`Services/StartupTrace.cs`：写到 `bin\startup-trace\startup-YYYYMMDD.log`，故意不放包数据目录——设置坏掉时正是最需要日志的时候。记录时间、PID、阶段、版本、Git SHA、异常类型 / 消息 / HRESULT / InnerException 链，覆盖 `OnLaunched` 各阶段 → `MainShell` → `MomentShell` → 卡片控件 → `STARTUP-OK`。不含 Cookie / Token / 用户内容。

`startup-ok.txt` 在首屏内容挂载后写入，Smoke Test 只认这个，不看进程是否存活。不能等 `Window.Activated`——自动化方式拉起时窗口可能一直不激活。

### 播放器生命周期追踪

`Services/PlayerLifecycle.cs`：写 `LocalState\Logger\player-lifecycle-YYYYMMDD.log`，每条含时间、事件种类、实例号、托管线程 ID，关键事件带调用栈，创建 / 释放带耗时。目的是把**三件不同的事**分开：Reactor 重新渲染、XAML 控件重造、mpv 实例重建。

### 脚本

| 脚本 | 作用 |
|---|---|
| `tools/run-app.ps1` | 递增清单版本 → 构建 → 就地注册（不卸载）→ 启动，失败回退 |
| `tools/smoke-test.ps1` | 构建 → 注册并核对「安装位置 == 当前产物」→ 启动等 `startup-ok` → UIA 检查导航 → 关闭 → **再启动一次**，退出码区分构建/部署/启动/UI/环境问题 |
| `tools/stress-player.ps1` | 循环「冷启动 → 搜索 → 打开视频 → 等播放 → 关窗」N 轮，解析生命周期日志统计 create / dispose 配对与耗时，泄漏返回非零 |

### 界面状态不再塞进 LocalSettings

`Services/AppStateStore.cs`：上次分区、通知基线这些原来写在 `LocalSettings`，切分区、轮询都写一次，把包设置状态容器写脆了。现在存 `LocalState\app-state.json`，写坏删掉即可。

启动守卫（连续两轮没走到「窗口起来」就把 `Settings` 挪成 `Settings-broken-<时间>`）现在在 `MainWindow:shell-loaded` 立即清标记，不再等 6 秒——否则开发期反复 `taskkill` 会被误判成启动失败，偏好（包括亚克力开关）每轮被重置一次。

### 已定位并修掉的

- **动态页顶部 UP 头像条点了没反应**：`OnControlLoaded` 在 ViewModel 为 null 时直接解引用，空引用把后面挂 `SelectionChanged` 的代码整段挡掉；订阅拆成 Attach / Detach。
- **收藏 / 稍后再看 / 历史记录的返回键要按好几次**：`OverlayFrame.NavigateTo` 是往栈里加页，看着像没动；这三个入口页点返回直接清栈退出。
- **评论回复按钮**：命中区放大、命令创建时绑定，点完把 `@用户名 ` 写进输入框并抢焦点。
- **搜索结果页切分区不生效**：给 `SelectorBar` 传固定 `selectedIndex` 等于受控模式，点击被立刻改回；内容宿主又没 key，旧分区还留在树上。改成 `Optional<int>.Unset` + 内容宿主按分区类型加 key。
- **搜索框建议浮层**：Popup 跳结果页后仍浮在最上层吃点击；结果页那个实例直接摘掉 `ItemsSource` 并每次渲染压一遍。
- **视频标签点击**：`SearchCommand.Execute(string)` 触发参数类型异常，改成走 `NavigationViewModel.Search(tag.Name)`。
- **直播卡片首行被切**：`ScrollViewer` 初始偏移不为 0，`Loaded` 后归零，并在卡片放出后补一次。
- **直播卡片悬停崩溃**：`CardControl` 已用过 `ElementCompositionPreview.GetElementVisual`，同一元素再设 `TranslationTransition` 会抛 `UnauthorizedAccessException`（在 `PointerEntered` 里抛＝直接崩）。改用 `RenderTransform` 平移。
- **扫码成功后又被踢回登录页**：启动时初始化某个设置属性也触发写入，和 token 落盘撞在同一份文件上；现在值没变就不写。
- **换视频白屏卡死 / 闪退**：见「已知问题」里仍挂着的那条。

## 代码规模与 Reactor 进度

口径是 `git ls-files` 的实际行数，排除 `obj` / `bin` / `_build`，按钮控件（`LikeButton` / `CoinButton` / `FavoriteButton` / `DownloadButton` / `CommentLikeButton`）算进 XAML。

| 分类 | 文件 | 行数 |
| --- | ---: | ---: |
| XAML | 241 | 18,855 |
| XAML 后台代码（`.xaml.cs`） | 230 | 12,206 |
| Reactor 外壳（`Reactor/*.cs`） | 16 | 4,186 |
| 全部 C# | 705 | 58,539 |

| 对比 | 计算 | 结果 |
| --- | --- | ---: |
| Reactor / XAML | 4,186 ÷ 18,855 | **22.2%** |
| Reactor / 界面层（XAML + 后台） | 4,186 ÷ 31,061 | **13.5%** |
| Reactor / 全部 C# | 4,186 ÷ 58,539 | **7.2%** |

按「界面落点」数（外壳 1；每页 1；播放页每个侧栏面板、评论 / 弹幕 / 直播互动面板、视频卡片布局、设置卡片、播放器控制层各 1；样式模板图标不算）：

| 区域 | 已迁移 | 总数 | 进度 |
| --- | :---: | :---: | --- |
| 应用外壳（窗口 / 导航 / 标题栏 / 侧边栏 / 账号） | 1 | 1 | `██████████` 100% |
| 页面 | 12 | 21 | `██████░░░░` 57% |
| 播放页侧栏（视频信息 / 分 P / 合集 / 播放列表） | 4 | 4 | `██████████` 100% |
| 评论 / 弹幕 / 直播互动面板 | 3 | 4 | `███████░░░` 75% |
| 视频卡片布局（推荐流 / 动态） | 2 | 2 | `██████████` 100% |
| 设置卡片 | 0 | 20 | `░░░░░░░░░░` 0% |
| 播放器控制层（控制栏 / 右键菜单 / 进度条 / 浮层 / 结束页） | 0 | 1 整块 | `░░░░░░░░░░` 0% |

合计 **22 / 53 ≈ 42%**。

**已挂 Reactor 外壳的 12 个页面**：动态、消息、直播分区、历史、收藏、稍后再看、设置、流行、视频分区、动漫、影视、搜索结果。

**仍是纯 XAML**：启动页、图库、搜索首页、文章分区、文章阅读、个人空间、关注、粉丝。

**刻意没动的**：20 个设置卡片（排版交给 `SettingsCard` / `SettingsExpander`，复刻容易走样）；播放器控制层整块（控制栏、右键菜单、字幕与着色器面板、直播弹幕浮层，加上结束页 `PlaybackEndPanel.xaml` + `PlayerViewModel.EndScreen.cs`），这块和播放器生命周期强绑定。

## 技术栈

- .NET 10 / C# / WinUI 3（Windows App SDK 2.6.5），目标框架 `net10.0-windows10.0.22621.0`
- Microsoft.UI.Reactor 0.1.0-preview.17
- CommunityToolkit.Mvvm 8.2.2（`[ObservableProperty]` / `[RelayCommand]`）
- BiliKernel（上游的数据与接口层）
- mpv（Richasy.MpvKernel）

## 构建与运行

```powershell
git clone https://github.com/TravelLeobai/Bili.Copilot.git
cd Bili.Copilot/src
dotnet build Desktop\BiliCopilot.UI\BiliCopilot.UI.csproj -c Debug -p:Platform=x64
```

需要 Windows 11、.NET 10 SDK，以及 **Windows App Runtime 2.6**（[下载页](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads)；也可改成自包含部署）。版本号在 `src/Directory.Packages.props` 的 `Microsoft.WindowsAppSDK`。

```powershell
pwsh -File tools\run-app.ps1 [-NoBuild] [-CleanData]      # 构建 + 递增版本 + 注册 + 启动
pwsh -File tools\smoke-test.ps1 [-NoBuild] [-Reinstall]    # 构建 + 注册 + 启动两次 + UIA，失败非零
pwsh -File tools\stress-player.ps1 -Rounds 10              # 播放器生命周期压测
```

踩过的构建坑：

- `MSB3027`（文件被占用）：先结束 `BiliCopilot.UI` / `MSBuild` / `VBCSCompiler`，等十来秒再编。
- XAML 编译偶发 PRI 报错：删掉 `_build\x64\Debug\BiliCopilot.UI\bin\resources.pri` 重来。
- 注册报 `0x80073D06`：清单版本必须大于已安装版本；`smoke-test.ps1` 会自动提升，手工注册时注意 `bin\AppxManifest.xml` 里的版本号可能落后于已安装包。
- 发 Release 又不需要 AOT：加 `-p:PublishAot=false`，否则撞 `Platform linker not found`。

## 配置与密钥

仓库不含任何接口密钥、Cookie 或账号凭据。AI 接口地址、模型、密钥、字数上限、SponsorBlock 服务器地址都在应用设置页里填，只写本机设置与 `LocalState\app-state.json`，不进版本库。要用空降助手的 AI 识别和观看兴趣分析，需自备兼容 OpenAI 协议的接口。

## 已知问题

- **切换视频（合集 / 播放列表的下一集）仍不稳定**。已确认的事实：在同一个 mpv client 上 `dispose` 旧实例再 `new` 新实例，新实例 `InitializeAsync` 会挂满 20 秒超时（更早的版本直接崩在原生层）。当前版本改成「有可用 mpv 实例就复用」（换文件而非重建），超时兜底会显示「播放器初始化超时，请重开播放窗口」而不是崩溃。**复用路径换视频是否完全正常尚未验证**——如果你遇到白屏，重开播放窗口即可恢复。
- **别用 `Copy-Item` / 备份还原搬包数据目录**：`LocalState` / `Settings` 里的文件带包身份的 EFS 状态，外部拷贝会破坏它，之后读写抛 `0x80070199`（未按预期加密）或启动即崩。要重置就整个删掉让系统重建。
- **设置状态容器会坏**：事件日志里是 `AppModel-State` 的 `SettingsInitialize` 失败（`80070003` → `80073DB8`），表现是启动后退到登录页、或启动约十秒后进程消失（`combase.dll` + `0x80004003`）。应用自带兜底：连续两轮没走到「窗口起来」就把 `Settings` 挪成 `Settings-broken-<时间>`。彻底重建则卸载包、删掉包数据目录、重新注册，代价是重新扫码。
- **写设置太频繁会把 token 写丢**：扫码成功后又被踢回登录页，原因是启动时初始化某个设置属性也触发了一次写。现在「值没变就不写」，界面状态也搬到了 `app-state.json`。
- **`DefaultStyleOptimizations` 实验开关默认关闭**：WinUI 自带控件样式优化，走 `AppContext.SetSwitch`，本机实测打开后启动不稳。
- **画质解锁有额度**：走第三方登记服务，额度用完返回 `code=9`，自动退回常规地址，不影响播放。
- **收藏页兴趣条、字幕采样还没做**：见「AI 分析 → 没做的」。

## 许可

沿用上游 LICENSE。代码可读可学，但要拿去发布、改名上架或商用，请先满足上游许可条件并取得哔哩哔哩相关授权——这不是本仓库能替你解决的事。

---

给云之幻：谢谢你写的 Bili.Copilot，这个分支是从它长出来的。
