# 哔哩助理 · 自用重写版

个人自用的 B 站桌面客户端。mpv 播放内核，WinUI 3 界面，部分界面用 Reactor 重写过。

上游是 [Bili.Copilot](https://github.com/azpanel/Bili.Copilot)（作者**云之幻**）。界面骨架、播放内核、登录与数据接口有很大一部分是原作者的工作；这个仓库是我按自己使用习惯改出来的分支，原作者没参与，这里的问题也不该去找他。

当前基于 **.NET 11**（`net11.0-windows10.0.22621.0`）与 Windows App SDK 2.6.5。分支 `master` 就是自用主线，没有稳定/开发之分。

---

## 目录

- [边界](#边界)
- [现在能用的](#现在能用的)
- [三处关键设计](#三处关键设计)
  - [直播布局的三态](#直播布局的三态)
  - [播放器生命周期](#播放器生命周期)
  - [图片与列表](#图片与列表)
- [性能与诊断](#性能与诊断)
- [构建](#构建)
- [打包与发布](#打包与发布)
- [已知问题](#已知问题)
- [代码结构](#代码结构)
- [文档索引](#文档索引)
- [致谢与许可](#致谢与许可)

---

## 边界

- **内容版权**：视频、音频、封面、弹幕、评论归哔哩哔哩与对应 UP 主、版权方所有。本项目只处理界面与播放，不提供内容下载分发，不做付费内容破解。
- **个人自用**：仅用于个人学习与自用。使用本仓库代码产生的后果与原项目作者、哔哩哔哩官方无关。
- **不分发安装包给陌生人**：GitHub 上的 Release 是我自己机器上用的构建，带自签名证书，装不装由你判断。

---

## 现在能用的

| 模块 | 状态 | 说明 |
| --- | --- | --- |
| 播放 | 可用 | mpv 主内核，WebView2 + Shaka 回落 |
| 直播 | 可用 | 互动面板、自研弹幕通道、响应式布局 |
| 动态 | 可用 | 瀑布流 + 直播分区 |
| 收藏 | 可用 | 含「订阅合集」子分区 |
| 通知 | 可用 | 订阅合集更新提醒，可关 |
| AI 分析 | 可用 | 空降识别 / 观看兴趣 / 兴趣条 |

### 播放

- **内核**：mpv 为主，WebView2 + Shaka 作回落。mpv 初始化失败降级，不弹窗。
- **CDN 优选**：节点池同步外部 `cdn.json`，顶栏与右键菜单都能手选或自动优选；直播时不显示节点指示器（直播走另一条拉流链路）。
- **着色器与超分**：Anime4K / ArtCNN / NNEDI3 / RAVU 与 NVIDIA VSR，设置里记住选择；每次媒体加载完强制再应用一次。
- **空降助手**：SponsorBlock 兼容服务 + 跳转点数据 + AI 识别三层叠加，默认自动跳过；静音类片段压低音量而不跳。
- **结束页**：播完不停在「底栏还在、点播放没反应」。进片尾 0.3 秒内切页，位置不推进时 700ms 防抖兜底；重播走 `seek(0) + resume` 复用已加载媒体。
- **换集**：分 P、合集、播放列表的下一个都走「复用已有 mpv 实例换文件」。同窗口销毁重建必挂 20 秒超时，更早的版本直接崩在原生层。

### 直播

- **互动面板**：弹幕、榜单、大航海三页，自研 WebSocket 通道。
- **弹幕过滤**：底栏漏斗可按「等级 < 3」「没有粉丝牌」过滤普通弹幕；礼物、醒目留言、上舰、进场不受影响。
- **线路**：顶栏「原画 · 线路 N」与面板顶行是同一份数据，切换即时生效。

### 动态

- **综合**：响应式瀑布流，可用宽度 ≥900 DIP 两列、≥1250 DIP 三列；顶部关注 UP 头像条可按人筛选。
- **直播分区**：先出关注主播正在播的房间，几秒后补直播回放。开播状态走 `room/v1/Room/get_status_info_by_uids` 批量查。
- **卡片**：`ItemsRepeater` + `UniformGridLayout`，`MinItemWidth` 按可用宽度算；悬停上浮用 `RenderTransform`，不用 `TranslationTransition`（同一个 `CardControl` 二次设置会抛 `UnauthorizedAccessException`，在 `PointerEntered` 里抛就是直接崩）。

### 收藏 · 订阅合集

侧栏「订阅合集」采用空间式布局，支持播放全部与订阅切换。卡片与收藏视频同款；封面不带 UP 头像（早期画的是合集创建者，实测常错人，去掉顺便省掉每张卡一次用户资料请求）。更新进通知中心，可关。

### AI 分析

同一套 OpenAI 兼容接口用在三处：空降广告识别、动态「已看过」的观看兴趣、稍后再看 / 历史顶部的兴趣条。

输出格式是硬约束，不靠模型自觉：提示词写死 `<<<兴趣>>> … <<<END>>>`，解析端只取标记之间；不按格式就退回 `【结论】` 之后，再退回按中文句子拼接。字数上限（20~200，默认 60）同时写进提示词和裁剪逻辑。

---

## 三处关键设计

### 直播布局的三态

侧栏互动面板占 360 DIP。窗口够宽时贴右，变窄时压到底部，收起只留一根 26 宽的展开条。

| 状态 | 触发 | 画面 | 顶栏 | 底栏 |
| --- | --- | --- | --- | --- |
| 展开 · 宽 | 宽 ≥ 1024 DIP 且面板展开 | 右侧让出面板宽度 | 按钮组停在面板左边缘 | 铺到面板左边缘 |
| 折叠 · 宽 | 宽 ≥ 1024 DIP 且面板收起 | 铺满 | 按钮组靠右，避开窗口按钮 | 铺满 |
| 窄版 | 宽 < 1024 DIP | 上方 16:9，面板占满剩余高度 | 右侧按钮整排隐藏 | 铺满 |

实现上分成两件事：

- **覆盖层让位**：`ContentHost.Margin` 按面板实际宽高设置，一次管住进度条、控制栏、顶栏。
- **mpv 画面让位**：`MpvSurfaceService.ApplyInset` 直接摆 mpv 子窗口，宽度比覆盖层再少 20 像素——两个独立算出来的宽度之间必然有缝，与其对准不如重叠，伸出去的部分藏在面板底下。

窄版下控制栏触发区也抬到面板顶边（原本绑窗口底部 160px，面板压上去就很难触发）。

**已知限制**：mpv 是覆盖层下的独立子窗口，鼠标落在画面区时 `PointerMoved` 进不了 XAML，控制栏要等指针划到边缘区才弹出。根治得加透明接收层或给 mpv 子窗口挂 `WS_EX_TRANSPARENT`，目前没做。

### 播放器生命周期

三件事必须分开看：Reactor 重渲染、XAML 控件重造、mpv 实例重建。混在一起就会出现「改了 UI 结果播放器没了」。

- **换视频不重建实例**：`PlayerViewModel` 在收尾换视频时走「复用现有 mpv + 换 resolver」，只有冷启动和换窗口才建新实例。重建实例是白屏卡死的根源。
- **重建前先放干净**：停 → dispose 旧 surface → 等 800ms 再建。dispose 之后**不要**再调 `Client.StopAsync()`，会一直挂住不返回。
- **初始化有 20 秒上限**：`InitializeAsync().WaitAsync(20s)`。超时分支刻意**不** dispose 那个卡住的 surface（dispose 会撞原生层闪退），只退订事件并把 `Player` 置空。
- **超时会毒化本进程**：一个进程里只要有一次 20 秒超时，后续初始化直接快速失败（`_mpvInitPoisoned`），错误文案是「请重开播放窗口或重启应用」。这是刻意的：那个被放弃的 surface 仍占着共用的 mpv client，重建也起不来。
- **超时的两种真因**：① GPU 设备被 reset（日志里 `Device lost` / `DXGI_ERROR_DEVICE_RESET 0x887a0007` / `Could not create the texture 0x887a0005`）；② 媒体地址打不开（`InitializeAsync(alsoPlay: true)` 等的是「真的开播」，地址 403 时 `PlayAsync` 会一直挂着）。**先看日志再判断**，两者的外部表现完全一样。

### 图片与列表

- 封面解码宽度按实际渲染尺寸自适应，**只抬不降**——往下调会把服务端缩略图钉死在过小尺寸，表现为整片封面变马赛克。
- 解码宽度必须在 `SetSourceAsync` 之前设，设晚了不生效。
- markdown 图与热路径图片共用同一个 `HttpClient`，超时差异用 `CancellationTokenSource` 控制，不改共享实例的 `Timeout`。
- 动态瀑布流卡片按业务身份复用（`ReferenceEquals(card.Tag, items[i])`），插入、重排不串图。

---

## 性能与诊断

以下数字来自本机（Ryzen 7 5800H / 16 逻辑核 / 31.4 GB / Windows 11），口径与对照等级见 [`NET11_PERFORMANCE_VALIDATION.md`](NET11_PERFORMANCE_VALIDATION.md)。

| 指标 | 值 | 对照等级 |
| --- | --- | --- |
| async 状态机（`BiliCopilot.UI.dll`） | 592 → 86（−85.5%） | 受控 A/B（runtime-async 单变量） |
| 程序集体积 | 5,057,536 → 4,489,728 B（−11.2%） | 受控 A/B |
| 播放期 UiLag | p50 0.08–0.12 ms / p95 0.17–1.53 ms | 非受控对照 |
| 冷启动到出窗口 | 中位 ≈ 2.9 s | 无法对照 |
| 播放 75 秒 | Responding 15/15，Gen2 GC 0 次 | 绝对事实 |
| 构建 | Debug/Release 均 0 警告 0 错误 | — |

**runtime-async**：`Features=runtime-async=on` 是 opt-in（GA 但非默认），只对**返回 Task 的方法**生效——`async void` 事件处理器一律回落状态机，全仓现有 79 个 `async void` 就是残留的那 79 个。改这个开关必须 `-t:Rebuild` 或换输出目录，MSBuild 不把它当重编译输入。

### 日志与探针

| 位置 | 内容 |
| --- | --- |
| `LocalState\Logger\log-YYYY-MM-DD.txt` | 主日志。`PERF UiLag:` 每 10 秒一行（n / p50 / p95 / max） |
| `LocalState\Logger\player-lifecycle-YYYYMMDD.log` | 播放器创建/释放，含实例号、线程 ID、耗时、`create-player:start/end/timeout` |
| `LocalState\Logger\crash-YYYY-MM-DD.txt` | 应用级未处理异常（硬崩在原生层的不一定落这里） |
| `bin\startup-trace\startup-YYYYMMDD.log` | 启动检查点，故意不放包数据目录——设置坏掉时正是最需要日志的时候 |
| 事件查看器 → Windows 日志 → 应用程序 | 硬崩看这里：`Application Error 1000` 带 faulting module / 异常码 / 偏移 |

### 采集性能数据

```powershell
# 进程级计数器（GC / 工作集 / 分配速率）
dotnet-counters collect -p <pid> --counters System.Runtime[gc-heap-size,alloc-rate,gen-0-gc-count,gen-1-gc-count,gen-2-gc-count,time-in-gc,working-set] -o counters.csv --duration 00:00:30
```

两个坑：

- **`gen-*-gc-count` 是 1 分钟 Rate 计数器，不是次数**。三列常出现同样的数，别当分代次数读；要判断 GC 压力用 `gc-heap-size` / `alloc-rate` / `time-in-gc` / `working-set`。
- **被裁剪过的产物采不到任何数据**。如果 `runtimeconfig.json` 里有 `System.Diagnostics.Tracing.EventSource.IsSupported=false`，`dotnet-counters` 只会写出表头、0 样本——这不是工具坏了。判断方法：读部署目录 `BiliCopilot.UI.runtimeconfig.json` 的 `configProperties` 条数，正常的 Debug 构建是 4 条。

---

## 构建

需要 Windows 11、.NET 11 SDK（本机 `11.0.100`）、Windows App Runtime 2.6。

```powershell
git clone https://github.com/TravelLeobai/Bili.Copilot.git
cd Bili.Copilot/src
dotnet build Desktop\BiliCopilot.UI\BiliCopilot.UI.csproj -c Debug -p:Platform=x64 `
  "-p:SolutionDir=<仓库绝对路径>\src\"
```

`-p:SolutionDir` 不能省：工程用 `$(SolutionDir)` 算 `_build\<Platform>\<Configuration>\`，单项目构建时这个属性是空的，产物会散落到奇怪的地方。

```powershell
pwsh -File tools\run-app.ps1 [-NoBuild] [-CleanData]      # 构建 + 递增版本 + 注册 + 启动
pwsh -File tools\smoke-test.ps1 [-NoBuild] [-Reinstall]    # 构建 + 注册 + 启动两次 + UIA
pwsh -File tools\stress-player.ps1 -Rounds 10              # 播放器生命周期压测
```

### 构建坑

- **`-t:Rebuild` 与并发**：同一工程同一个 `obj` 不能并行构建，否则 XAML 生成的 `.g.cs` 会被对方清掉，报出一大串 `CS2001 未能找到源文件 obj\...\*.g.cs`（见过 540 条）。要并行就给每条线一个独立输出目录：`-p:BuildOutDir=E:\build-temp-dsh\out-<你的名字>\`。注意 `Directory.Build.props` 只迁了 `OutDir`/`IntDir`，`IntermediateOutputPath` 仍在仓库内——所以**同一工程同一配置还是得串行**。
- **`MSB3021` / `MSB3027`（文件被占用）**：应用或 `MSBuild` / `VBCSCompiler` 还活着。先结束 `BiliCopilot.UI`、`BiliCopilot.Visor`、`MSBuild`、`VBCSCompiler`，等十来秒再编。
- **`PRI210 0x800704c8 File move failed`**：`bin\resources.pri` 被占用。关应用；还不行就把 `resources.pri` 改名成 `.bak` 再编。
- **`CS2001` 一大片**：`obj` 不完整（被并发构建或手工删过）。`-t:Rebuild` 全量重建。
- **`0x80073D06` 注册失败**：清单版本必须**大于**已安装版本。手工 `Add-AppxPackage -Register` 时尤其注意，版本低了会静默失败，跑的仍是旧包，表现为「改了没生效」。
- **改 `Features`（runtime-async）不生效**：普通 build 是空操作，必须 `-t:Rebuild` 或换输出目录。
- **临时目录别留在 C 盘**：C 盘长期紧张，构建前 `$env:TMP="E:\build-temp-dsh"; $env:TEMP=$env:TMP`。
- **测试工程**：`dotnet test` 偶尔在子进程结束后挂住（testhost 继承 stdout），加 `--no-build` 或改用日志判读，别当成构建失败。

---

## 打包与发布

`BiliCopilot.UI` 是单项目 MSIX（`EnableMsixTooling=true`，无 `.wapproj`）。

```powershell
cd src
dotnet publish Desktop\BiliCopilot.UI\BiliCopilot.UI.csproj `
  -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 `
  -p:SelfContained=true `
  -p:GenerateAppxPackageOnBuild=true -p:AppxPackageSigningEnabled=true `
  -p:PackageCertificateThumbprint=<自签名证书指纹> `
  -p:AppxBundle=Never
```

产物落在 `dist\release\BiliCopilot.UI_<版本>_x64_Test\`，约 200 MB。

**关于 AOT**：UI 工程**不再**常开 `PublishAot`。它以前是工程里的常开属性，导致普通 build 也背 23 条裁剪导向开关（`EventSource.IsSupported=false` 之类），而这条工程集从来没真正 publish 过。真要给 UI 上 AOT，把 `<PublishAot>true</PublishAot>` 写进**本工程的 csproj** 再 publish——**不要**在命令行传 `-p:PublishAot=true`，那是全局属性，会传染给 netstandard2.0 的子工程（`BiliCopilot.UI.Models`、`ResourceGenerator`）并直接 `NETSDK1207` 打断整解。Visor 是独立进程，Native AOT 保留在它自己的工程里。

清单里两个版本号都要改（`src\Desktop\BiliCopilot.UI\Package.appxmanifest`）：

```xml
<Identity Version="1.0.4.0" />
<TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.17763.0" ... />
```

`MinVersion` 写的是 **OS 版本**，上游模板里它跟应用版本同值（`1.0.0.7`），装包时会被判非法。

发布用 `gh release create <tag> <msix> <cer>` 附自签名证书；安装顺序是「装证书到受信任根 → 开开发者模式 → 装 msix」。

---

## 已知问题

- **mpv 初始化可能超时，且一次超时会毒化整个进程**。表现是白屏 + 「播放器初始化超时」，之后每次开视频都立刻失败，只能重开窗口或重启应用。先查日志区分 GPU 设备 reset 与媒体地址 403（见「播放器生命周期」）。
- **切集仍不算完全稳**。复用路径换视频的长期稳定性没有充分验证，遇到白屏重开播放窗口即可。
- **窄版弹幕绘制位置**没调完。上次一起改导致闪退，那条已回退，目前弹幕仍按宽版高度排，可能压到下方面板。
- **控制栏要靠指针划到边缘才弹**，原因见「直播布局的三态 → 已知限制」。
- **别用 `Copy-Item` 搬包数据目录**。`LocalState` / `Settings` 带包身份的 EFS 状态，外部拷贝会破坏，之后抛 `0x80070199` 或启动即崩。要重置就整个删掉让系统重建。
- **设置状态容器会坏**：`AppModel-State` 的 `SettingsInitialize` 失败，表现是启动退回登录页或十秒后进程消失。应用自带兜底（连续两轮没到「窗口起来」就把 `Settings` 挪成 `Settings-broken-<时间>`）——反过来说，**重注册包之前先备份 `Settings\settings.dat`**，否则登录态会跟着走丢（恢复办法：把最近一个 `Settings-broken-*` 里的 `settings.dat` 拷回去）。
- **分析器目前是空转的**：`Directory.Build.props` 写了 `EnableNETAnalyzers=true` / `AnalysisMode=AllEnabledByDefault`，实测求值 `false`——`Directory.Packages.props` 引的 `Microsoft.CodeAnalysis.NetAnalyzers` 包会把自己的 props 压上去。真打开会立刻在 `TreatWarningsAsErrors` 下红一片，得先设计最小规则集。
- **`DefaultStyleOptimizations` 默认关闭**：WinUI 自带控件样式优化，本机实测打开后启动不稳。
- **收藏页兴趣条、字幕采样没做**：外壳层拿不到当前视频收藏分区的集合。

---

## 技术栈

- .NET 11 / C# preview / WinUI 3（Windows App SDK 2.6.5），`net11.0-windows10.0.22621.0`，`runtime-async=on`
- Microsoft.UI.Reactor 0.1.0-preview.17（注意：该包只发布 `net10.0-windows10.0.22621` 一个 TFM，靠 TFM 回落跑在 net11 上）
- CommunityToolkit.Mvvm 8.2.2（`[ObservableProperty]` / `[RelayCommand]`）
- Richasy.MpvKernel 0.0.2-preview2（mpv 封装，NuGet，无源码）
- Microsoft.Graphics.Win2D 1.4.0（封面解码与滤镜）
- BiliKernel 0.0.2-preview2（上游数据与接口层，NuGet，无源码）

规模口径（`git ls-files`，排除 `obj/` / `bin/` / `_build/`）：

| 分类 | 数量 |
| --- | ---: |
| 仓库内手写 `.cs` | 769 个 / 85,166 行 |
| 生成代码（`*.g.cs` 等） | 494 个（只看不改） |
| XAML | 246 个 |
| 语义扫描命中 | 735 条（落在 189 个手写文件上） |

Reactor 只覆盖部分界面：应用外壳、播放页侧栏、视频卡片布局走 Reactor，设置卡片与播放器控制层刻意保持 XAML。

---

## 代码结构

```
src/
  Desktop/
    BiliCopilot.UI/             主程序（WinUI 3，单项目 MSIX）
      Controls/                 界面控件（XAML 与 Reactor 混合）
        Core/                   播放器覆盖层、控制栏、结束页
        Player/                 播放器附属控件（直播侧栏、弹幕、指示器）
        Comment/                评论区（含投票卡片）
        Favorites/              收藏与订阅合集
        Settings/               设置卡片
      Reactor/                  Reactor 外壳（动态、评论、直播互动等）
      ViewModels/               MVVM 视图模型
      Services/                 CDN 优选、播放器生命周期、启动检查点、诊断、界面状态存储
      Resolvers/                媒体源解析（点播 / 番剧 / 直播）
      Forms/                    窗口（播放器窗口在此）
      Models/                   数据模型与播放表面
    BiliCopilot.Visor/          独立进程（无边框播放，Native AOT）
    BiliCopilot.Visor.Models/   Visor 的 DTO
    BiliCopilot.UI.Models/      共享模型（netstandard2.0）
  tests/                        回归测试（net11.0）
tools/                          构建、冒烟、压测、验证脚本
scripts/                        环境准备（libmpv、Visor 发布）
docs/                           设计与诊断文档
```

---

## 文档索引

仓库根目录的四份报告是最近一次 .NET 11 改造的交付物：

| 文件 | 内容 |
| --- | --- |
| [`NET11_OPTIMIZATION_AUDIT.md`](NET11_OPTIMIZATION_AUDIT.md) | 构建配置现状、依赖漂移、P0–P3 问题清单、「不建议做」的项 |
| [`NET11_MODERNIZATION_REPORT.md`](NET11_MODERNIZATION_REPORT.md) | 每个提交的动机 / 文件 / 验证档位（已验证 / 仅构建 / 未验证） |
| [`NET11_PERFORMANCE_VALIDATION.md`](NET11_PERFORMANCE_VALIDATION.md) | 性能数字与对照等级（受控 A/B / 非受控 / 无法对照） |
| [`NET11_SOURCE_SCAN_MANIFEST.md`](NET11_SOURCE_SCAN_MANIFEST.md) | 扫描口径与统计，逐文件清单在 `docs/` 同名文件 |

`docs/` 下另有八份证据文档：特性研究、构建审计、逐文件清单、热点分级、P0 修复记录、播放位置回调、网络与图片热路径、验证原始记录。播放相关的历史分析见 [`PLAYBACK_STALL_UPSTREAM_ANALYSIS.md`](PLAYBACK_STALL_UPSTREAM_ANALYSIS.md) 与 [`DANMAKU_REBUILD_DIAGNOSTICS.md`](DANMAKU_REBUILD_DIAGNOSTICS.md)。

---

## 致谢与许可

沿用上游 LICENSE。代码可读可学，但要拿去发布、改名上架或商用，请先满足上游许可条件并取得哔哩哔哩相关授权——这不是本仓库能替你解决的事。

给云之幻：谢谢你写的 Bili.Copilot，这个分支是从它长出来的。
