# 阶段 2：插桩与首批采样

> 状态：**插桩已落地并跑通，但样本量不足**，下面的数字只能证明"探针能用"，不能当基线。

## 1. 插桩内容（可开关，关闭时零开销）

| 位置 | 探针 | 作用 |
|---|---|---|
| `Services/PerfProbe.cs` | `Measure` / `Count` / `FlushIfDue` | 汇总 P50/P95/Max，每 10 秒打一行 `PERF` 日志 |
| `PlayerViewModel.OnIsControlsVisibleChanged(bool)` | `TopBar.SetCaptionButtonVisibility` | 量"改 AppWindow 标题栏颜色"那一步 |
| `PlayerViewModel.OnIsControlsVisibleChanged(old,new)` | `TopBar.IsTopMost.Query` / `TopBar.BottomProgressBar` | 量切换时另外两步 |
| 同上 | `TopBar.IsControlsVisible.Set` | 统计可见性写入次数（验证 hover 是否在狂写） |
| `PlayerOverlay` 指针处理器 | `TopBar.Hover.ShowRequests` / `HideRequests` | 统计 hover 触发的显示/隐藏请求数 |

开关：`LocalState/perf-probe.txt` 存在即启用，删除即关闭。关闭时不分配、不写日志。

## 2. 首批采样（1.0.12.2，BV13DBHBGEbs，1080P）

```
PERF TopBar.SetCaptionButtonVisibility: n=1 p50=2.99ms p95=2.99ms max=2.99ms
PERF TopBar.IsTopMost.Query:            n=1 p50=0.58ms p95=0.58ms max=0.58ms
PERF TopBar.BottomProgressBar:          n=1 p50=0.10ms p95=0.10ms max=0.10ms
PERF COUNT TopBar.Hover.HideRequests=1
PERF COUNT TopBar.Hover.ShowRequests=1
PERF COUNT TopBar.IsControlsVisible.Set=1
```

**怎么读这组数：**

- 单次顶栏显示，同步部分合计约 **3.7ms**（2.99 + 0.58 + 0.10），占 16.7ms 帧预算的 22%。
- 其中 `SetCaptionButtonVisibility`（改 AppWindow 标题栏前景色）占 80%，是目前顶栏路径上最贵的一步——**印证了阶段 1 的判断**（跨到非客户区的调用）。
- 但 **n=1**，没有统计意义。要判断"概率性卡顿"，至少需要 100 次以上样本。

## 3. 为什么样本没跑够

自动化 hover 只触发到 1 次显示/隐藏。原因在阶段 1 也记录过：mpv 是独立子窗口，指针大部分时间落在 mpv 的 HWND 上，XAML 侧的 `PointerMoved` 收不到事件——只有指针落到标题栏/控制栏这类 XAML 可见区域才会触发。

也就是说：**用自动化大量 hover 顶栏这条路本身就不通**，需要换成"在 XAML 顶栏区域内反复进出"或直接用 UIA 点标题栏区域。这一条要单独解决，否则 100 次采样跑不出来。

## 4. 下一步（需要有数据后才会动手）

1. 改自动化：把指针限定在标题栏那条 56px 带内做小范围移动，反复穿过 `HeaderBarHeight` 分界线。
2. 每轮跑满 100 次，拿到 `SetCaptionButtonVisibility` 的 P50/P95/P99。
3. 同时补 UI 线程延迟探针（当前只有同步耗时，没有排队延迟）。
4. 详情面板：补 `DetailOpenRequested/FirstVisible/Ready` 三点，量进入详情这条链。
5. 有数据后再决定：是否把 `SetCaptionButtonVisibility` 收敛成"只在值变化时执行"、是否把顶栏可见性从 `Visibility` 改为 `Opacity`。

## 5. 明确未测

- UI 线程 Dispatcher 排队延迟分布：未测。
- 详情面板进入耗时的任何数字：未测。
- GPU 侧（VSR 实际生效、PresentMon 呈现时间）：未测，本机没有跑 PresentMon/GPUView。
- 卡顿复现率：未测（当前探针只量同步耗时，不能判断用户感知卡顿）。
