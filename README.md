# 哔哩助理 · 自用重写版

个人自用的 B 站桌面客户端。mpv 播放内核，WinUI 3 界面，React 思路的 XAML 外壳。

上游是 [Bili.Copilot](https://github.com/azpanel/Bili.Copilot)（作者**云之幻**）。界面骨架、播放内核、登录与数据接口有很大一部分是原作者的工作；这个仓库是我按自己使用习惯改出来的分支，原作者没参与，这里的问题也不该去找他。

---

## 目录

- [边界](#边界)
- [现在能用的](#现在能用的)
- [直播布局的三态](#直播布局的三态)
- [稳定性工程](#稳定性工程)
- [技术栈与规模](#技术栈与规模)
- [构建](#构建)
- [打包与发布](#打包与发布)
- [已知问题](#已知问题)
- [代码结构](#代码结构)
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
| 播放 | 可用 | mpv 主内核，WebView2 + Shaka 回落；mpv 初始化失败自动降级 |
| 直播 | 可用 | 互动面板、弹幕通道、响应式布局 |
| 动态 | 可用 | 瀑布流 + 直播分区 |
| 收藏 | 可用 | 含「订阅合集」子分区 |
| 通知 | 可用 | 订阅合集更新提醒，可关 |
| AI 分析 | 可用 | 空降识别 / 观看兴趣 / 兴趣条 |

### 播放

- **内核**：mpv 为主，WebView2 + Shaka 作为回落。mpv 初始化失败自动降级，不弹窗。
- **CDN 优选**：节点池同步自外部 `cdn.json`，顶栏与右键菜单都能手选或自动优选；直播时顶栏不显示节点指示器（直播走另一条拉流链路，那个指示器没意义）。
- **着色器与超分**：Anime4K / ArtCNN / NNEDI3 / RAVU 与 NVIDIA VSR，设置里可记住选择；每次媒体加载完强制再应用一次。
- **空降助手**：SponsorBlock 兼容服务 + 跳转点数据 + AI 识别三层叠加，默认自动跳过，静音类片段压低音量而不跳过。
- **结束页**：播完不再停在「底栏还在、点播放没反应」。进片尾 0.3 秒内切页，位置不推进时 700ms 防抖兜底；布局复用视频信息面板的 `LikeButton` / `CoinButton` / `FavoriteButton`，重播走 `seek(0) + resume` 复用已加载媒体。
- **换集**：分 P、合集、播放列表的下一个都走「复用已有 mpv 实例换文件」，不再同窗口销毁重建（重建必挂 20 秒超时，更早的版本直接崩在原生层）。

### 直播

- **互动面板**：弹幕、榜单、大航海三页；自研 WebSocket 通道。
- **弹幕过滤**：底栏漏斗可按「等级 < 3」「没有粉丝牌」过滤普通弹幕，礼物、醒目留言、上舰、进场不受影响。
- **布局**：宽窗贴右、窄窗压底部，详见下一节。
- **线路**：顶栏的「原画 · 线路 N」下拉与面板顶行的是同一份数据，切换即时生效。

### 动态

- **综合**：响应式瀑布流，可用宽度 ≥900 DIP 两列、≥1250 DIP 三列；顶部关注 UP 头像条可按人筛选。
- **直播分区**：先出关注主播正在播的房间，几秒后补直播回放。开播状态走 `room/v1/Room/get_status_info_by_uids` 批量查，回放从投稿动态里按标题筛——全站搜索搜出来的都是大主播，关注的人一个不在（实测 0 命中）。
- **卡片**：`ItemsRepeater` + `UniformGridLayout`，`MinItemWidth` 按可用宽度动态算；悬停上浮用 `RenderTransform`，不碰 `TranslationTransition`（同一个 `CardControl` 上二次设置会抛 `UnauthorizedAccessException`，在 `PointerEntered` 里抛就是直接崩）。

### 收藏 · 订阅合集

- 侧栏新增「订阅合集」，空间式布局，支持播放全部与订阅切换。
- 卡片版式与收藏视频卡片同款；封面不带 UP 头像（早期版本画的是合集创建者，实测常错人，干脆去掉，顺便省掉每张卡一次用户资料请求）。
- 合集更新会进通知中心，设置里可关。

### 图片与列表性能

- 封面解码宽度按实际渲染尺寸自适应，**只抬不降**——往下调会把服务端缩略图钉死在过小的尺寸上，表现为整片封面变马赛克。
- 动态瀑布流卡片改为按业务身份复用（`ReferenceEquals(card.Tag, items[i])`），插入、重排不串图。

### AI 分析

同一套 OpenAI 兼容接口用在三处：空降广告识别、动态「已看过」的观看兴趣、稍后再看 / 历史顶部的兴趣条。

输出格式是硬约束，不靠模型自觉：提示词写死 `<<<兴趣>>> … <<<END>>>` 标记，解析端只取标记之间；模型不按格式来就退回 `【结论】` 之后，再退回按中文句子拼接。字数上限（20~200，默认 60）同时写进提示词和裁剪逻辑。

---

## 直播布局的三态

侧栏互动面板占 360 DIP。窗口够宽时贴右，变窄时压到底部，收起来只留一根 26 宽的展开条。

| 状态 | 触发 | 画面 | 顶栏 | 底栏 |
| --- | --- | --- | --- | --- |
| 展开 · 宽 | 宽 ≥ 1024 DIP 且面板展开 | 右侧让出面板宽度 | 按钮组停在面板左边缘 | 铺到面板左边缘 |
| 折叠 · 宽 | 宽 ≥ 1024 DIP 且面板收起 | 铺满 | 按钮组靠右，避开窗口按钮 | 铺满 |
| 窄版 | 宽 < 1024 DIP | 上方 16:9，面板占满剩余高度 | 右侧按钮整排隐藏 | 铺满 |

实现上分成两件事：

- **覆盖层让位**：`ContentHost.Margin` 按面板实际宽高设置，一次管住进度条、控制栏、顶栏。
- **mpv 画面让位**：`MpvSurfaceService.ApplyInset` 直接摆 mpv 子窗口，宽度比覆盖层再少 20 像素——两个独立算出来的宽度之间必然有缝，与其对准不如重叠，伸出去的部分藏在面板底下。

窄版下控制栏的触发区也跟着抬到面板顶边（原本绑定窗口底部 160px，面板压上去就很难触发）。

**已知限制**：mpv 是覆盖层下的独立子窗口，鼠标落在画面区域时 `PointerMoved` 进不了 XAML，所以控制栏要等指针划到边缘区域才弹出来。要根治得加一层透明接收层或给 mpv 子窗口挂 `WS_EX_TRANSPARENT`，目前没做。

---

## 稳定性工程

起因是反复出现「编译通过、启动就崩或卡住」，以及多次修复之间互相踩。

### 启动检查点

`Services/StartupTrace.cs` 写 `bin\startup-trace\startup-YYYYMMDD.log`，故意不放包数据目录——设置坏掉时正是最需要日志的时候。记录阶段、版本、Git SHA、异常类型 / 消息 / HRESULT / InnerException 链，覆盖 `OnLaunched` → `MainShell` → `MomentShell` → 卡片控件 → `STARTUP-OK`。不含 Cookie / Token。

`startup-ok.txt` 在首屏内容挂载后写入，Smoke Test 只认这个，不看进程存活——自动化拉起时窗口可能一直不激活。

### 播放器生命周期

`Services/PlayerLifecycle.cs` 写 `LocalState\Logger\player-lifecycle-YYYYMMDD.log`，每条含时间、事件、实例号、线程 ID，创建 / 释放带耗时。目的是把**三件不同的事**分开：Reactor 重渲染、XAML 控件重造、mpv 实例重建。

### 脚本

| 脚本 | 作用 |
| --- | --- |
| `tools/run-app.ps1` | 递增清单版本 → 构建 → 就地注册（不卸载）→ 启动，失败回退 |
| `tools/smoke-test.ps1` | 构建 → 注册并核对「安装位置 == 当前产物」→ 启动等 `startup-ok` → UIA 检查导航 → 关闭 → 再启动一次；退出码区分构建 / 部署 / 启动 / UI / 环境问题 |
| `tools/stress-player.ps1` | 循环「冷启动 → 搜索 → 打开视频 → 等播放 → 关窗」N 轮，统计 create / dispose 配对与耗时，泄漏返回非零 |

### 界面状态不进 LocalSettings

上次分区、通知基线这些原来写在 `LocalSettings`，切分区、轮询都写一次，把包设置状态容器写脆了。现在存 `LocalState\app-state.json`，写坏删掉即可。

---

## 技术栈与规模

- .NET 10 / C# / WinUI 3（Windows App SDK 2.6.5），`net10.0-windows10.0.22621.0`
- Microsoft.UI.Reactor 0.1.0-preview.17
- CommunityToolkit.Mvvm 8.2.2（`[ObservableProperty]` / `[RelayCommand]`）
- Richasy.MpvKernel 0.0.2-preview2（mpv 封装）
- Microsoft.Graphics.Win2D 1.4.0（封面解码与滤镜）
- BiliKernel（上游数据与接口层）

口径是 `git ls-files` 实际行数，排除 `obj` / `bin` / `_build`：

| 分类 | 文件 | 行数 |
| --- | ---: | ---: |
| XAML | 241 | 18,855 |
| XAML 后台代码 | 230 | 12,206 |
| Reactor 外壳 | 16 | 4,186 |
| 全部 C# | 705 | 58,539 |

Reactor 占界面层（XAML + 后台）约 **13.5%**，占全部 C# 约 **7.2%**。

按界面落点数：应用外壳 100%、播放页侧栏 100%、视频卡片布局 100%、评论/弹幕/直播互动 75%、页面 57%，设置卡片与播放器控制层**刻意没动**（设置排版交给 `SettingsCard`，复刻容易走样；控制层与播放器生命周期强绑定）。合计 22 / 53 ≈ 42%。

---

## 构建

```powershell
git clone https://github.com/TravelLeobai/Bili.Copilot.git
cd Bili.Copilot/src
dotnet build Desktop\BiliCopilot.UI\BiliCopilot.UI.csproj -c Debug -p:Platform=x64
```

需要 Windows 11、.NET 10 SDK、Windows App Runtime 2.6（[下载页](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads)），也可改成自包含部署。版本号在 `src/Directory.Packages.props`。

```powershell
pwsh -File tools\run-app.ps1 [-NoBuild] [-CleanData]      # 构建 + 递增版本 + 注册 + 启动
pwsh -File tools\smoke-test.ps1 [-NoBuild] [-Reinstall]    # 构建 + 注册 + 启动两次 + UIA
pwsh -File tools\stress-player.ps1 -Rounds 10              # 播放器生命周期压测
```

### 构建坑

- **`MSB3027`（文件被占用）**：先结束 `BiliCopilot.UI` / `MSBuild` / `VBCSCompiler`，等十来秒再编。用 `Select-String "error CS"` 过滤会把这行藏掉，结果拿到的是旧 DLL。
- **PRI 报错**：删 `_build\x64\Debug\BiliCopilot.UI\bin\resources.pri` 重来。应用在跑时删会失败（`PRI210 0x800704c8 File move failed`），先关应用。
- **注册报 `0x80073D06`**：清单版本必须**大于**已安装版本。手工用 `Add-AppxPackage -Register` 时尤其注意——版本低于已注册的会静默失败，跑的仍是旧包，表现为「改了没生效」。
- **发 Release 不需要 AOT**：加 `-p:PublishAot=false`，否则撞 `Platform linker not found`（项目默认开了 `PublishAot`）。

---

## 打包与发布

`BiliCopilot.UI` 是单项目 MSIX（`EnableMsixTooling=true`，无 `.wapproj`）。

```powershell
cd src
dotnet publish Desktop\BiliCopilot.UI\BiliCopilot.UI.csproj `
  -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 `
  -p:SelfContained=true -p:PublishAot=false `
  -p:GenerateAppxPackageOnBuild=true -p:AppxPackageSigningEnabled=true `
  -p:PackageCertificateThumbprint=<自签名证书指纹> `
  -p:AppxBundle=Never
```

产物落在 `dist\release\BiliCopilot.UI_<版本>_x64_Test\`，约 201 MB。

清单里两个版本号都要改（`src\Desktop\BiliCopilot.UI\Package.appxmanifest`）：

```xml
<Identity Version="1.0.4.0" />
<TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.17763.0" ... />
```

`MinVersion` 写的是 **OS 版本**，上游模板里它跟应用版本同值（`1.0.0.7`），装包时会被判非法，必须换成 `10.0.x`。

发布用 `gh release create <tag> <msix> <cer>`，附一张自签名证书；安装顺序是「装证书到受信任根 → 开开发者模式 → 装 msix」。

---

## 已知问题

- **切集仍不算完全稳**。同窗口销毁重建必挂 20 秒超时，已改成复用 mpv 实例换文件；复用路径换视频的长期稳定性**没有充分验证**，遇到白屏重开播放窗口即可。
- **窄版弹幕绘制位置**没调完。上次一起改导致闪退，那条已回退，目前弹幕仍按宽版高度排，可能压到下方面板。
- **控制栏要靠指针划到边缘才弹**，原因见「直播布局的三态 → 已知限制」。
- **别用 `Copy-Item` 搬包数据目录**。`LocalState` / `Settings` 带包身份的 EFS 状态，外部拷贝会破坏，之后抛 `0x80070199` 或启动即崩。要重置就整个删掉让系统重建。
- **设置状态容器会坏**：`AppModel-State` 的 `SettingsInitialize` 失败，表现是启动退回登录页或十秒后进程消失。应用自带兜底（连续两轮没到「窗口起来」就把 `Settings` 挪成 `Settings-broken-<时间>`）。
- **`DefaultStyleOptimizations` 默认关闭**：WinUI 自带控件样式优化，本机实测打开后启动不稳。
- **收藏页兴趣条、字幕采样没做**：外壳层拿不到当前视频收藏分区的集合。

---

## 代码结构

```
src/
  Desktop/
    BiliCopilot.UI/             主程序（WinUI 3，单项目 MSIX）
      Controls/                 界面控件（XAML 与 Reactor 混合）
        Core/                   播放器覆盖层、控制栏、结束页
        Player/                 播放器附属控件（直播侧栏、弹幕、指示器）
        Favorites/              收藏与订阅合集
        Settings/               设置卡片
      Reactor/                  Reactor 外壳（动态、评论、直播互动等）
      ViewModels/               MVVM 视图模型
      Services/                 CDN 优选、生命周期追踪、启动检查点、界面状态存储
      Resolvers/                媒体源解析（点播 / 番剧 / 直播）
      Models/                   数据模型与播放表面
    BiliCopilot.Visor/          独立窗口（无边框播放）
tools/                          构建、冒烟、压测、验证脚本
docs/                           文档站源（VitePress）
tests/                          回归测试
```

---

## 致谢与许可

沿用上游 LICENSE。代码可读可学，但要拿去发布、改名上架或商用，请先满足上游许可条件并取得哔哩哔哩相关授权——这不是本仓库能替你解决的事。

给云之幻：谢谢你写的 Bili.Copilot，这个分支是从它长出来的。
