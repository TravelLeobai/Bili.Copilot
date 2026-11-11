# 阶段 4：最小修复（第 1 项）

> 只做了一项：把顶栏显隐路径上"重复的窗口非客户区操作"去掉。没有碰 VSR、画质解锁、mpv 渲染。

## 改了什么

| 文件 | 改动 | 理由（有探针数据） |
|---|---|---|
| `Forms\PlayerWindow.cs` | `SetCaptionButtonVisibility` 加状态缓存，颜色没变直接 return | 这一步实测 **2.45~2.99ms**，是顶栏路径上最贵的一步；同色重复写没有意义 |
| `ViewModels\Core\PlayerViewModel\PlayerViewModel.cs` | 顶栏显隐时不再无条件 `IsTopMost = Window.IsTopMost()`；改成先读当前值，只有与上次同步值不同才写窗口 z-order | 原来每次显隐都查一次窗口置顶并赋值（会走 `SetWindowPos`），和 mpv 子窗口的合成互相打扰；实测查询本身 0.01~0.58ms，但赋值是跨进程窗口操作 |
| 同上 | 新增 `_lastTopMostSynced` 字段 | 记录上次同步值，作为去重依据 |

## 修复前后（同一台机器，同一视频 BV13DBHBGEbs）

| 探针 | 修复前 | 修复后 |
|---|---|---|
| `TopBar.SetCaptionButtonVisibility` | 每次切换都执行，2.45~2.99ms | 仍执行，但**只在颜色真正变化时**（同色切换直接 return） |
| `TopBar.IsTopMost.Query` + 赋值 | 每次切换都执行（查询 0.01~0.58ms + 一次窗口 z-order 写入） | 修复后该探针**未再出现**（说明没有发生需要同步的变化，赋值被跳过） |
| `TopBar.BottomProgressBar` | 0.09~0.31ms | 不变（这条本来就很便宜） |

## 是不是在掩盖症状

不是。判断依据：

1. 改的是**重复执行**：值没变就不做，属于纯去重，不改变任何可见行为。
2. 没有引入延时、没有降频、没有隐藏控件、没有改渲染参数。
3. 探针证明被去掉的那次调用真实存在且每次都发生（修复前每轮 1 次 Query + 赋值）。
4. 视觉与功能没变：顶栏照常显示/隐藏，标题栏按钮颜色逻辑不变，视频照常播放（1.0.12.3 冒烟通过，进程无崩溃）。

## 对边界的影响

- Reactor/XAML：无（只改 ViewModel 与 Window 封装）。
- mpv 生命周期：无（没碰 `MpvSurfaceService` 与播放器实例）。
- VSR / 画质解锁：无（`QualityUnlockService`、`VideoMediaSourceResolver` 未改）。

## 回滚

```
git revert <本提交>
```
字段 `_lastTopMostSynced`、`_lastCaptionForegroundVisible` 都是新增的私有状态，回滚无残留。

## 仍未解决的

**"概率性明显卡顿"本身还没定位。** 这次只是把顶栏路径上两处确定的冗余去掉了（合计最多省掉一次窗口 z-order 写入 + 一次标题栏颜色写入）。探针数据显示顶栏路径同步开销约 3ms，不足以解释"明显卡顿"，所以：

- 首要根因仍未确认（可能不在顶栏路径上，而在 UI 线程被别的东西占住，例如详情面板模板实例化 / 图片解码 / 弹幕批量更新）。
- 详情面板那条链**完全没有量过**。
- UI 线程排队延迟**没有探针**。
- GPU 侧（VSR 是否生效、呈现延迟）没有测。

下一步应优先补"UI 线程排队延迟探针 + 详情面板三点打点"，而不是继续猜顶栏。
