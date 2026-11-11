# sample-topbar.ps1 — 顶栏显隐性能采样

用真实鼠标点击驱动 Bili.Copilot 开发版播放器，然后解析应用自己的 `PERF` 日志，
输出顶栏（`TopBar.*`）的 p50 / p95 / max 汇总，供排查顶栏显隐抖动与回归用。

只读取日志，不修改应用源码；除 `LocalState\perf-probe.txt`（本脚本创建、`finally` 里删除）
和可选的 `-OutputCsv` 外，不写、不删任何文件。

---

## 快速开始

```powershell
# 默认：100 组点击、间隔 350ms、采样 BV13DBHBGEbs
pwsh -NoProfile -File .\sample-topbar.ps1

# 冒烟（2 组点击）+ 落 CSV
pwsh -NoProfile -File .\sample-topbar.ps1 -Cycles 2 -OutputCsv .\topbar.csv

# 应用已在运行：不重启，直接附着
pwsh -NoProfile -File .\sample-topbar.ps1 -Cycles 2 -SkipLaunch
```

| 参数 | 默认 | 说明 |
|---|---|---|
| `-Cycles` | 100 | 点击组数。一组 = 两次单击（同一点） |
| `-GapMs` | 350 | 每次点击后的等待。**别低于 250**：应用的双击窗口是 200ms，更快会被当成双击 → 切全屏 |
| `-Bvid` | `BV13DBHBGEbs` | 采样用视频，改它等于换片源（可能影响加载耗时，不影响顶栏路径） |
| `-SkipLaunch` | 关 | 附着到已在运行的 `BiliCopilot.UI`，不杀进程、不重启 |
| `-OutputCsv` | 无 | 每个探针一行：`Kind,Probe,N,P50Ms,P95Ms,MaxMs,Batches,TotalN,WorstMaxMs,Count,LastSeen` |

脚本退出码：`0` = 出报告；`2` = 某一步失败（报告里会写清第几步、为什么）；`1` = 未捕获异常。

---

## 它做什么

1. **开探针 + 起应用**
   - 应用只在 `LocalState\perf-probe.txt` 存在时写 PERF 日志；脚本先创建该文件。
   - 杀掉所有 `BiliCopilot.UI`（除非 `-SkipLaunch`），`Start-Process shell:AppsFolder\<PFN>!App`，
     最多等 60s 拿到主窗口。
2. **开视频**（UI Automation 优先）
   - 找 Name 为 `搜索` 的导航项，`mouse_event` 点它的中心；
   - 找 Name 为 `搜索视频、番剧或UP主` 的输入框，`ValuePattern.SetValue($Bvid)`，`SendKeys {ENTER}`；
   - 等 10s，按客户端区 **25% 宽 / 50% 高** 点结果卡片；
   - 最多等 30s 出现播放器窗口（判据见下）。
3. **点击采样**（每组）
   - 鼠标移到顶栏带（`rect.top + 30px`）——这是唯一能触发“显示顶栏”的鼠标路径；
   - 点播放区中心（`x = rect 中心`，`y = rect.top + rect.height * 0.55`），等 `GapMs`；
   - 再点同一点，等 `GapMs`。
   - 坐标每轮重算并夹在窗口内，不会把指针移出窗口。
4. **等 flush + 读日志**
   - 睡 12s 覆盖应用的 10s flush 窗口；
   - **再做一次显隐动作**（见“两个必须知道的应用事实”）逼出最后一批 PERF；
   - 读 `LocalState\Logger\log-<yyyy-MM-dd>.txt`（当天文件不存在则取最新的 `log-*.txt`），
     只保留时间戳 ≥ 脚本启动时刻的 `PERF` 行。
5. **出报告**
   - Markdown 表：每个探针**最后一批**的 `n/p50/p95/max`，外加跨批次汇总 `batches / total n / worst max`
     （最后一批通常只有约 10s 的样本，100 组跑下来不能只看最后一行）；
   - `PERF COUNT` 总量表；
   - 原始 `PERF` 行；无数据时打印原因。
   - 给了 `-OutputCsv` 就另写一份 CSV。

`PERF UiLag` 两种形态都解析：`PERF UiLag: n=... p50=...`，以及
`PERF UiLag buckets: <16.7=182 ... p50=...`（直方图行，`n` 取各桶计数之和，单独一行）。
不认识的 `PERF ...` 行原样保留在“Unparsed”里，不猜格式。

---

## 它测什么

探针来自 `src/Desktop/BiliCopilot.UI/Services/PerfProbe.cs` 和调用点：

| 探针 | 含义 | 触发点 |
|---|---|---|
| `TopBar.SetCaptionButtonVisibility` | 一次顶栏显隐里设置系统标题栏按钮前景色的耗时（注释说这是最贵的一步，2.5~3ms） | `PlayerViewModel.OnIsControlsVisibleChanged` |
| `TopBar.BottomProgressBar` | 同步底部进度条显隐的耗时 | 同上 |
| `TopBar.IsTopMost.Sync` | 顶栏显隐时同步窗口 z-order（只在真正变化时发生） | 同上 |
| `TopBar.IsControlsVisible.Set` | 计数：`IsControlsVisible` 被写的次数 | 同上 |
| `TopBar.Hover.ShowRequests` / `HideRequests` | 计数：指针移入顶栏带 / 移出顶栏带的次数 | `PlayerOverlay.OnPointerMoved` |
| `UiLag` | 计数：UI 线程排队延迟采样（帧间隔分布） | 应用侧计时器 |
| `Detail.FirstLoad.*` | 视频详情页首屏加载耗时，顺带采到，不是顶栏路径 | 详情页 |

一条 PERF 行的样子：

```
2026-11-12 07:11:05.541 +08:00 [INF] PERF TopBar.SetCaptionButtonVisibility: n=1 p50=2.70ms p95=2.70ms max=2.70ms
2026-11-12 07:11:05.541 +08:00 [INF] PERF COUNT TopBar.IsControlsVisible.Set=1
```

---

## 两个必须知道的应用事实（决定了脚本为什么这样写）

**1. 鼠标单击不会切 `IsControlsVisible`，只有触摸指针才会。**

`PlayerInteractivePanel.OnTapTimerTick`（`Controls/Core/PlayerInteractivePanel.xaml.cs:255`）：

```csharp
else if (_tapCount == 1 && !ViewModel.IsEnd && (...))
{
    if (_isTouch) { ViewModel.IsControlsVisible = !ViewModel.IsControlsVisible; ... }
    else if (_isLeftButton) { ViewModel.PlayPauseCommand.Execute(default); }
}
```

所以任务书里“单击播放区切换顶栏”在鼠标输入下实际是**播放/暂停**。
真正的鼠标显隐路径是 `PlayerOverlay.OnPointerMoved`：指针落在顶栏带（`Y <= 70` DIP）
或底部控制带（`Y >= ActualHeight - 160` DIP）→ 显示；落在别处 → 隐藏。

因此脚本在每组点击前**先把指针移到顶栏带**（一次真实的指针移动，不是点击），
让“显示”这半个动作真实发生；两次点击仍然按要求打在同一个点（`x=中心, y=0.55h`）。
副作用：每次点击会切一次播放/暂停（两次点击互相抵消），结束时视频可能停在暂停态——
对顶栏耗时没有影响。

> 实测（本机 1831x950 播放器窗口）：2 组点击 → `SetCaptionButtonVisibility` 有样本；
> 5 组点击 → `batches=3 total n=12`。窗口必须在前台，否则物理点击会落到别的窗口上，
> 脚本每轮都会检查并尝试把播放器拉回前台，失败会打印 `WARNING`。

**2. PERF 只在 `IsControlsVisible` 的 setter 里 flush，不在定时器里。**

`PerfProbe.FlushIfDue()` 只被 `PlayerViewModel.OnIsControlsVisibleChanged` 调用
（`ViewModels/Core/PlayerViewModel/PlayerViewModel.cs:846`），且要求距上次 flush ≥ 10s。
所以“跑完循环干睡 12s”本身不会写出最后一批样本——必须再制造一次显隐变化。
脚本在 12s 之后做两次“悬停顶栏 + 点播放区”，任一次触发 setter 就会把累计样本写盘。
如果应用状态导致这两次都没有产生变化（例如视频已结束、窗口失焦），最后的样本就写不出来，
报告里的 `total n` 会少于预期——这是应用的 flush 设计，不是脚本漏读。

---

## 已知限制

- **探针开关是进程级缓存**（`PerfProbe.IsEnabled` 是 `Lazy<bool>`）。应用启动时
  `perf-probe.txt` 不存在，这个进程就再也不会写 PERF。用 `-SkipLaunch` 附着到这种实例会得到
  “no PERF lines found”，报告里会明确说原因。要采样就让它重启（别加 `-SkipLaunch`）。
- **播放器判定**：顺序是 ① 带 `mpv` 子窗口的窗口（最可靠）→ ② HWND 类名
  `Microsoft.UI.Windowing.Window`（播放器由 `AppWindow.Create()` 创建，主窗口的类名是
  `WinUIDesktopWin32WindowClass`）→ ③ 任务书里的“新出现的、宽 > 1200px 的窗口”。
  显示器缩放 100% 时播放器默认宽 1120 DIP，③ 可能不成立，所以前两条才是主判据。
- **`-SkipLaunch` 有两种情况**：应用停主界面 → 正常走搜索；应用已经开着播放器 →
  第 2 步直接跳过，对现有播放器采样（省一次重启，适合反复调）。主窗口和播放器窗口的
  hwnd / 尺寸都会打进报告。
- **开发版可能崩**：本机跑过几次在播放器刚出现时崩溃（事件查看器 `MoAppCrash`，
  `CoreMessagingXP.dll`，`0xc000027b`），脚本会把它识别成
  `STEP 3 FAILED: the app process exited right after the player window appeared (crash?)`，
  清掉探针后以退出码 2 结束。这是应用的问题，重跑通常就过。
- **前台/遮挡**：脚本用 `SetForegroundWindow` + `BringWindowToTop` + 重试来抢前台，并每轮校验。
  如果别的窗口（IDE、浏览器）抢了焦点，点击会落到那个窗口上，数据会变少甚至为零。
  跑之前把屏幕让出来，别在采样期间操作鼠标键盘。
- **一次只跑一个实例**：脚本会先杀掉所有 `BiliCopilot.UI`。别和别的自动化脚本同时跑。
- **每批只覆盖约 10s**：flush 后计数器清零，`n/p50/p95/max` 是“最后一批”的值；
  跨批次看 `batches`/`total n`/`worst max`。想要整体分布请把 `-OutputCsv` 拿去做离线合并。
- **点击副作用**：每次点击切播放/暂停；`-GapMs < 250` 会被当成双击 → 切全屏（窗口尺寸变了，
  点击点会重算，但画面会闪一下）。
- **依赖登录与网络**：搜索、取流都要 B 站登录态；视频被地区/会员限制时第 2 步会超时失败。
  失败会打印 `STEP 2 FAILED: ...` 并以退出码 2 结束，不会无限重试。
- **不清理应用状态**：脚本结束时不关播放器、不退出应用，下次跑会先杀掉再重启。
- 日志时间戳按本机时区解析（带 `+08:00` 偏移）；跨零点运行会读当天的文件，找不到就退回最新文件。

---

## 文件

- `sample-topbar.ps1` — 本脚本（PowerShell 7+，`Add-Type` 引入 `mouse_event` / `GetWindowRect` /
  `SetForegroundWindow` / `SetCursorPos`，UIA 用 `UIAutomationClient` / `UIAutomationTypes`，
  `{ENTER}` 用 `System.Windows.Forms.SendKeys`）。
- `README.md` — 本文件。
