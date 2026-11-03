# 哔哩助理 · Reactor 亚克力版改造记录

改造对象：[azpanel/Bili.Copilot](https://github.com/azpanel/Bili.Copilot)（哔哩助理，WinUI 3 客户端）。
功能参考：[ywmoyue/biliuwp-lite](https://github.com/ywmoyue/biliuwp-lite)（BiliLite UWP 版）。

## 一、技术栈升级

原项目基于 .NET 9 + Windows App SDK 1.8。Microsoft.UI.Reactor 要求 .NET 10 + Windows App SDK 2.x，因此做了整体迁移：

| 项 | 原值 | 新值 |
| --- | --- | --- |
| TargetFramework | net9.0-windows10.0.22621.0 | **net10.0-windows10.0.22621.0** |
| Microsoft.WindowsAppSDK | 1.8.251106002 | **2.2.0** |
| Microsoft.Web.WebView2 | 1.0.3595.46 | **1.0.3719.77** |
| 新增 | — | **Microsoft.UI.Reactor 0.1.0-preview.16** |

升级后原有 Richasy.* 系列包（BiliKernel、MpvKernel、WinUIKernel）在 2.2.0 下可正常工作，无需替换。

构建命令（需显式传 SolutionDir，否则源生成器拿不到 resw 输入，StringNames 不会生成）：

```powershell
dotnet build src\Desktop\BiliCopilot.UI\BiliCopilot.UI.csproj -c Release -p:Platform=x64 -p:SolutionDir=<仓库>\src\
```

## 二、Reactor 外壳

新增 `src/Desktop/BiliCopilot.UI/Reactor/MainShell.cs`，一个 Reactor `Component`：

- 左侧导航：`NavigationView(...)` 由 `NavigationViewModel.MenuItems` 经 `UseObservableTree` 驱动，页面类型到 WinUI Symbol 做了映射（FluentIcons 的枚举名不在 WinUI Symbol 里，直接传会变豆腐块）；
- 内容宿主：`XamlHostElement` 包住原有 `MainFrame` + `OverlayFrame`，XAML 页面原样复用；
- 标题栏：`MainShell` 内构造一个 48px 高的 `Border` 作为拖拽区，`MainWindow` 在 `Loaded` 后 `SetTitleBar(...)`。

`MainWindow.xaml` 改为只放一个 `RootGrid`，`ReactorHostControl` 在代码里创建后 `Mount(_shell)` —— 该控件不能在 XAML 中构造（XAML 编译器报 WMC0100）。

## 三、亚克力背景

1. `WindowBase` 基类默认 `SystemBackdrop = MicaBackdrop`，`MainWindow` 构造时覆盖为 `DesktopAcrylicBackdrop`（受 `SettingNames.EnableAcrylicBackdrop` 控制，默认开启）；
2. WinUI 的 `Page` 默认带不透明背景，必须在 `MainFrame` / `OverlayFrame` 创建页面实例时把 `Page.Background` 置空，否则亚克力被完全遮住；
3. `NavigationView` 背景置为 `"Transparent"`。

设置入口：设置 → 应用 → 通用 → 亚克力背景（开关即时生效，通过 `AppViewModel.ApplyBackdrop` 遍历所有 `MainWindow`）。

## 四、从 biliuwp-lite 移植的功能

### SponsorBlock 跳过赞助片段

- 新增 `Services/SponsorBlockService.cs`：调用 `https://sponsor.ajay.app/api/skipSegments`，支持 sponsor / selfpromo / interaction / intro / outro / preview / music_offtopic / filler 八类；
- `PlayerViewModel` 在 `InitializeAsync` 时按视频 ID 拉取片段，在播放进度回调里判断是否进入片段区间，进入则 `ChangePositionAsync(segment.End)` 直接跳到结尾；
- 设置开关：设置 → 应用 → 通用 → 跳过赞助片段（默认开启）。

与原实现一样，网络失败或无片段时静默跳过，不打断播放。

## 五、构建产物

```
dist/BiliCopilot-Reactor-x64/       # 可直接侧加载运行
dist/BiliCopilot-Reactor-x64.zip    # 分发用压缩包（约 166 MB，含 libmpv/ffmpeg）
```

验证方式：`Add-AppxPackage -Register AppxManifest.xml` 注册 loose 包后，从 `shell:AppsFolder\Richasy.BiliCopilot.Dev_g7ew4fxhv1f46!App` 启动，主窗口正常渲染，动态/直播/设置页均可加载。


## 七、第二轮迭代（标题栏 / 页面重写 / 直播）

### 标题栏统一

原先导航栏自带汉堡按钮，标题栏左侧留了一段空白，窄窗口时按钮位置还会漂移。现在：

- NavigationView 关闭自带 PaneToggleButton（IsPaneToggleButtonVisible = false），PaneDisplayMode 固定为 Left；
- 汉堡按钮移到标题栏左侧，点击通过 Reactor 的 UseState 切换 IsPaneOpen；
- 标题栏变成 [汉堡][图标][标题][副标题] 布局，拖拽区由 SetTitleBar 绑到整条标题栏。

### 用 Reactor 重写的页面

| 页面 | 组件 | 做法 |
| --- | --- | --- |
| 设置 | Reactor/SettingsShell.cs | 标题、应用/AI 分段、滚动容器由 Reactor 渲染；设置项复用原有 SettingControl 控件，按分组排列 |
| 消息 | Reactor/MessageShell.cs | 左右分栏布局由 Reactor 渲染，侧栏与聊天区嵌入原有控件 |
| 动态 | Reactor/MomentShell.cs | 顶部分段与内容切换由 Reactor 驱动，视频/综合两种详情控件按当前 section 切换 |
| 直播播放 | Reactor/LivePlayerShell.cs | 标题栏、状态、播放器宿主全部 Reactor 渲染，MediaPlayerElement 嵌入 |

页面类本身保留（继承原 LayoutPageBase<T>），内部放 ReactorHostControl 挂载对应 Reactor 组件，所以路由、参数、生命周期不变。

### 直播原生观看

原来点击直播卡片是 Launcher.LaunchUriAsync 打开 live.bilibili.com 网页。现在：

- LiveItemViewModel.PlayAsync 改为导航到新页面 Pages/Overlay/LivePlayerPage；
- 页面用 IPlayerService.GetLivePlayDetailAsync 取直播流，优先选 .m3u8（HLS）地址，用 MediaPlayerElement 在应用内播放；
- 顶部有返回按钮，播放器自带传输控件。

实测已能播放（原神直播流），不再跳浏览器或独立播放器窗口。

### 仍未 Reactor 化的部分

播放器窗口（PlayerWindow + PlayerOverlay）没有重写：它是独立 AppWindow + DesktopWindowXamlSource 托管的复杂叠加层，控制条、弹幕、播放列表、AI 面板都在里面，替换渲染方式会直接动播放核心，风险大于收益。直播之外的点播界面维持原样。
## 六、遗留项

- 其余页面仍是 XAML，未逐页重写为 Reactor；Reactor 的 `TitleBar` 元素依赖 `ReactorApp` 的窗口上下文，在 `ReactorHostControl` 场景下不可用，因此标题栏用 XAML `Border` 作为拖拽宿主；
- `PublishAot` 仍保留在 Publish 配置中，构建 Release 用的普通编译；若要 AOT 发布需要 MSVC 工具链。
