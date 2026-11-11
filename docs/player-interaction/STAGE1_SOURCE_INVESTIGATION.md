# 播放器交互卡顿调查（阶段 1：源码调查）

> 状态：**只做了源码调查，没有做运行时性能测量。**
> 本文所有"证据"都是代码位置与调用关系，不含耗时数字。凡是没测的，下面会直接写"未测量"。
> 调查对象：`bili-copilot-v2`，HEAD = `d59de33`(调查时工作区已有未提交改动，见 §6)。
> 环境：WinUI 3 + Windows App SDK 2.6.5、Microsoft.UI.Reactor 0.1.0-preview.17、mpv 子窗口渲染、NVIDIA VSR 驱动侧开启、画质解锁走第三方解析服务。

---

## 1. 场景 A：播放中进入视频详情

### 1.1 调用链（源码实测路径）

```
用户点击右侧分区标签（信息 / 弹幕 / 评论 / 推荐 …）
└─ Controls\Player\VideoPlayerSideHeader.xaml.cs:68
   sender.SelectedItem.Tag as IPlayerSectionDetailViewModel
└─ PlayerViewModel 切分区的命令（IPlayerSectionDetailViewModel.CurrentSection）
   └─ ViewModels\Components\VideoPlayerInfoSectionDetailViewModel 等首次构造
      ├─ TryFirstLoadCommand → 拉视频信息 / 分 P / 合集 / UP 信息（网络）
      ├─ Items 填充 → ObservableCollection 通知
      └─ XAML 模板首次实例化：PlayerSectionTemplateSelector（Extensions\PlayerSectionTemplateSelector.cs）
         → 详情控件 Measure/Arrange（UI 线程）
         → 封面 / 头像：Controls\BasicCoverImage.cs:49 SizeChanged → RaiseDecodeWidthIfNeeded(宽)
            （按控件宽度重新解码，首帧解码在 UI 线程排队）
```

### 1.2 与播放器的交互点

- mpv 是**独立子窗口**（`Services\MpvSurfaceService.cs` 用 `EnumChildWindows` 找到 mpv 的 HWND 再 `SetWindowPos` 做内缩），不是 XAML 里的元素。
- 因此详情面板**不会**重建播放器实例；但详情面板的创建/布局发生在同一 UI 线程，且与视频解码、弹幕渲染共享 GPU / DWM 合成时间片。
- 未测量：详情面板首次弹出的 UI 线程占用、图片解码耗时、是否能复现"概率性"卡顿。

### 1.3 可疑点（按可能性排序，均未测量验证）

| # | 可疑点 | 代码位置 | 为什么会卡 |
|---|---|---|---|
| A1 | 详情 VM 首次加载把网络回调、集合填充、图片解码都排在 UI 线程 | `VideoPlayerInfoSectionDetailViewModel` + `BasicCoverImage.cs:49` | 一次操作里集中出现多个图片解码 + 布局，和视频帧抢同一线程/GPU |
| A2 | XAML 模板首次实例化（控件树较大） | `PlayerSectionTemplateSelector.cs` | 首次打开才创建，首次卡、再开不卡——与"有概率"相符 |
| A3 | 封面按控件宽度重新解码，没有尺寸缓存 | `BasicCoverImage.cs:49` | 窗口尺寸变化/首次布局各触发一次解码 |

---

## 2. 场景 B：呼出顶部控制栏

### 2.1 调用链

```
指针移动（PlayerOverlay 的 Pointer 处理器）
└─ Controls\Core\PlayerOverlay.xaml.cs:165-189
   ├─ position.Y ≤ HeaderBarHeight           → _isInTitleBar = true
   ├─ position.Y ≥ 高度 - 传输栏高度          → _isInTransportControls = true
   ├─ 命中：_cursorTimer.Start(); ViewModel.IsControlsVisible = true
   └─ 未命中：_controlTimer.Start(); ViewModel.IsControlsVisible = false
      （注意：这里**每个 pointer move 都会写** IsControlsVisible 与两个 Timer）

ViewModel.IsControlsVisible 变更
├─ PlayerViewModel.cs:833  OnIsControlsVisibleChanged(bool)
│  └─ Window?.SetCaptionButtonVisibility(value)
│     └─ Forms\PlayerWindow.cs:148 → _rootWindow.TitleBar.ButtonForegroundColor = …
│        （AppWindow 标题栏属性写入：非客户区/合成层）
├─ PlayerViewModel.cs:836  OnIsControlsVisibleChanged(old,new)
│  ├─ IsTopMost = Window?.IsTopMost()   ← 每次切换都查一次窗口置顶状态
│  └─ CheckBottomProgressBarVisible()   ← 又一次设置栏可见性属性
├─ PlayerOverlay.xaml.cs:248-258  起停两个 DispatcherQueueTimer
└─ XAML：x:Bind IsControlsVisible → 传输栏 Visibility（PlayerOverlay.xaml.cs:514）
   以及 TestRect.Visibility 反复切换（L168 / L274）——TestRect 上挂了 ShowAnimations
```

### 2.2 关键事实（源码可验证）

1. **顶栏显示本身不改变 mpv 尺寸**：mpv 的内缩只在 `OnOverlaySizeChanged`（L77）和 `OnLivePanelSizeChanged` 里算，非直播、非尺寸变化时不会 `SetWindowPos`。
   → 所以"呼出顶栏导致视频表面 resize"这条假设，在当前代码里**没有直接路径**；除非窗口尺寸本身发生变化。
2. **每次可见性切换都会碰 AppWindow 标题栏**（`ButtonForegroundColor`）并查询一次 `IsTopMost`。
   → 这是唯一在顶栏路径上"越过 XAML 进入窗口非客户区"的操作，也是最可疑的 GPU/DWM 干扰点。
3. **pointer move 是无节流的**：只要指针在窗口里移动，就会写 `IsControlsVisible`、Switch 两个 Timer、切 `TestRect.Visibility`。
   → 用户"呼出顶栏"这个动作本身包含大量 pointer move，等于短时间内反复触发上面整条链。
4. 未测量：这条链单次耗时、UI 线程排队延迟、是否触发 DWM 重绘、VSR 开启时是否放大成可见卡顿。

### 2.3 可疑点排序（未验证）

| # | 可疑点 | 依据 |
|---|---|---|
| B1 | 重复写 `IsControlsVisible`（pointer move 每次都写）+ 每次都碰 AppWindow 标题栏 | L165-189 与 PlayerViewModel.cs:833-840 |
| B2 | `IsTopMost` 查询与 `SetCaptionButtonVisibility` 在 UI 线程同步执行 | PlayerViewModel.cs:834-838 |
| B3 | `TestRect.Visibility` 反复切换，且该元素带 ShowAnimations | PlayerOverlay.xaml.cs:168/274 |
| B4 | 传输栏用 `Visibility` 切换，首次显示要实例化/测量子控件（按钮、进度条） | PlayerOverlay.xaml.cs:514 |

---

## 3. 关于 VSR 与画质解锁（现阶段能确认与不能确认的）

**能确认（代码层面）：**

- VSR 由 NVIDIA 驱动侧开启，项目代码里**没有**任何 VSR 开关或检测逻辑（全库搜索不到 VSR 相关实现）。所以它是否对当前视频生效，本仓库无法直接观测。
- 画质解锁：`Services\QualityUnlockService.cs` → `Resolvers\VideoMediaSourceResolver.cs`，会改实际播放地址（qn 提升、镜像主机），因此会真实改变解码分辨率/编码。
- 两者叠加时，解码负载更高、GPU 视频处理单元更忙——这会让上面 A/B 两条链上的卡顿**更容易被看见**，但不等于是它们的 bug。

**不能确认（需测量）：**

- 当前播放是不是真的经过 VSR（驱动行为，需 PresentMon / GPUView 或驱动日志）。
- 解锁后的实际解码分辨率与帧率（需读 mpv 属性或日志）。
- 卡顿发生在 CPU 还是 GPU 呈现。

---

## 4. 阶段 2 需要的插桩（尚未实现）

按任务书要求的低开销、可开关：

1. `PERF` 开关（LocalState 文件或环境变量），关闭时零日志。
2. UI 线程响应探针：后台按 50ms 投递 `DispatcherQueue.TryEnqueue` 打点，记录排队延迟分布（>16.7 / 33.3 / 50 / 100 / 200ms 分桶）。
3. 事件打点：`TopBarShowRequested` / `TopBarStateChanged` / `TopBarFirstVisible`、`DetailOpenRequested` / `DetailFirstVisible` / `DetailReady`、`PlayerResizeRequested/Completed`，每次带操作 ID。
4. 在 `OnIsControlsVisibleChanged` 与 `SetCaptionButtonVisibility` 前后各打一个点，直接量出"碰 AppWindow"这一步的耗时。
5. 在 hover 处理器里统计每秒 `IsControlsVisible` 写入次数（判断 B1 是否成立）。
6. 详情面板首次打开：记录模板实例化、首次布局、数据到达、图片解码完成四个时间点。

**未做任何性能测量，因此不给基线数字。** 任务书要求的 P50/P95/P99、UI 延迟分布、卡顿率，都要等插桩跑完才有。

---

## 5. 建议的最小修复方向（待证据支持后才动手）

按当前代码最可能的顺序，**先测量再决定**：

1. **去重**：`IsControlsVisible` 只在值真正变化时写；`SetCaptionButtonVisibility` / `IsTopMost` 随之只在切换时执行。
2. **hover 路径不重复 Start 定时器**（`_cursorTimer/_controlTimer` 已启动就跳过）。
3. 若测量证实 `TestRect.Visibility` 切换昂贵，改为不参与布局的方式（或挪出 hover 路径）。
4. 详情页：非首屏数据延后、封面按实际尺寸解码并复用（`BasicCoverImage` 已有尺寸门限逻辑，可加深缓存）。
5. 以上都不碰 VSR 与画质解锁代码路径（`QualityUnlockService` / `VideoMediaSourceResolver` / `MpvSurfaceService` 的内缩计算）。

---

## 6. 工作区与版本

- 调查时 HEAD：`d59de33`，工作区有未提交改动（转发/弹幕跳伞等本会话的改动）。
- 未建立独立诊断分支，未改动任何业务代码。
- 下一步若要跑测量，需要：先提交/暂存现有改动，再开 `diag/player-interaction` 分支加插桩。
