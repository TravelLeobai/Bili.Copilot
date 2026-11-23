# 弹幕轨道重建计时：阶段定义、字段与开销

对应提交：`chore(debug): instrument danmaku track rebuild timings`。

## 1. 为什么不能照抄上游

上游 `9abb6cd` 改的是 `Controls/Danmaku/MpvAssDanmakuRenderer.cs` —— 一个把弹幕写成 **mpv ASS 字幕轨**的实现，耗时阶段是 `sub-add` / `secondary-sid` / `RemoveTrack` / 写 `.ass` 文件。

本仓没有这个东西。实测检索：

```
grep -rn "sub-add|secondary-sid|MpvAss" src/   → 0 命中
ls Controls/Danmaku/                            → CommonDanmakuParser / DanmakuControlBase /
                                                   DanmakuDisplayOptions / DanmakuSendDialog / VideoDanmakuPanel
```

本仓的弹幕是 **Richasy.Danmaku** 的两个进程内渲染器：

- `DanmakuFrostMaster`（Win2D 路线，默认）
- `DanmakuRenderer`（DirectX 路线）

由 `Controls/Danmaku/VideoDanmakuPanel.xaml.cs` 托管，并没有 mpv 字幕轨、没有临时 ASS 文件、没有 `Gate` 锁。

**所以测量点适配到当前真实的重建流程**，不伪造上游的 `add-ms` / `remove-ms`。

## 2. 本仓真实的「重建」是什么

`VideoDanmakuPanel.Redraw()`（`async void`）是唯一的重建入口，流程：

```
Redraw()  ─ 入口，记 requestedAt
  │  (若两个渲染器都还没建好) await Task.Delay(500)
  ├─ DispatcherQueue.TryEnqueue(Low, lambda)      ← UI 队列排队
  │     ├─ build : GetCachedDanmakus() → BilibiliDanmakuParser.GetDanmakuList(...) ×2（每个渲染器各一份）
  │     ├─ write : DanmakuFrostMaster.SetDanmakuList / DanmakuRenderer.SetDanmakuList
  │     ├─ select: UpdateDanmakuTime(_lastProgress)   ← 把渲染器时间对准当前进度
  │     ├─ style : ResetDanmakuStyle()                ← 字号/透明度/区域/速度/开关
  │     └─ resume: Resume() ×2（非暂停时）
  └─ finally：算总耗时并写日志
```

触发来源（全部来自源码真实路径，没有编的）：

| reason | 触发点 |
|---|---|
| `items` | `DanmakuRenderViewModel.Initialize(danmakus)` → `Redraw("items")`（弹幕数据首次就绪） |
| `visibility` | `DanmakuRenderViewModel.OnIsShowDanmakuChanged(true)` → `Redraw("visibility")`（显隐打开） |
| `renderer` | `VideoDanmakuPanel` 切渲染器（Win2D ↔ DirectX）后 `ViewModel.Redraw("renderer")` |
| `explicit` | 其它显式 `Redraw()` 调用（无原因参数时的兜底） |

上游的 `style` / `realtime` 两个 reason 在本仓**不是重建**：样式变更走 `RequestResetStyle` → `ResetDanmakuStyle()`（只套样式，不重建列表）；实时弹幕走 `AddRealtimeDanmaku`（单条追加，不重建）。所以本仓不输出这两个 reason 名——不是因为偷懒，是因为它们不对应重建。

## 3. 阶段定义（口径写死）

| 字段 | 起点 → 终点 | 是否含锁等待 | 是否含调度延迟 | 是否含 IO |
|---|---|---|---|---|
| `queue-ms` | `Redraw()` 入口 → lambda 真正开始执行 | — | **含**（`Task.Delay(500)` + UI 队列等待） | — |
| `build-ms` | 第一份 `GetDanmakuList` 之前 → 第二份返回之后 | 不含 | 不含 | 不含 |
| `write-ms` | 第一次 `SetDanmakuList` 之前 → 第二次返回之后 | 不含 | 不含 | 不含（本仓不落盘） |
| `select-ms` | `UpdateDanmakuTime` 之前 → 返回之后 | 不含 | 不含 | 不含 |
| `style-ms` | `ResetDanmakuStyle` 之前 → 返回之后 | 不含 | 不含 | 不含 |
| `resume-ms` | `Resume()` ×2 之前 → 返回之后 | 不含 | 不含 | 不含 |
| `work-ms` | = 上面五段之和 | — | — | — |
| `total-ms` | **`Redraw()` 入口 → lambda 结束（finally）** | **含**（含 UI 队列等待、`Task.Delay`、渲染层内部同步耗时） | **含** | 不含 |

**`total-ms` 的口径说清楚**：

- 它是**端到端**，从「请求重建」到「重建跑完」，包含入队等待与那个 500ms 延迟。
- 它**不是**分段相加。`work-ms` 才是分段相加。
- 两个都写，就是为了让「分段和比端到端小多少」在日志里直接可见。实测两者差 ~500ms（几乎全是 `queue-ms`），这正是 `Task.Delay(500)` + 队列等待的量级。
- 反过来也不能拿 `total-ms` 冒充「渲染工作耗时」：要判断渲染层本身慢不慢，看 `work-ms` 与各段。

**没有的阶段（明确不输出）**：

| 上游字段 | 本仓状态 |
|---|---|
| `add-ms`（mpv `sub-add`） | 无对应动作 |
| `remove-ms`（`RemoveTrack`） | 无对应动作 |
| `bytes`（ASS 文件字节数） | 本仓不写临时文件，无字节数；改报 `events`（送入渲染层的条目数） |

## 4. 日志字段

```
DANMAKU | rebuild session={Session} reason={Reason} events={Events} queue-ms={QueueMs}
          build-ms={BuildMs} write-ms={WriteMs} style-ms={StyleMs} select-ms={SelectMs}
          resume-ms={ResumeMs} work-ms={WorkMs} total-ms={TotalMs} stale={Stale} failed={Failed}
          tick={Tick} utc={Utc}
```

- `session`：与播放停滞日志同一个 `PlayerViewModel.DiagnosticsSessionId`，由 `BeginPlaybackDiagnosticsSession` 写进 `DanmakuRenderViewModel.DiagnosticsSessionId`。**这就是两条日志的关联键。**
- `events`：本次送入渲染层的弹幕条目数（代替上游的 `bytes`）。`0` 表示这次重建其实没有条目（例如显隐先于数据就绪），是有效信息。
- `stale`：这份计时跑完时，是否已经有更新的一次重建开始了（旧结果失效）。
- `failed`：lambda 体里是否抛了异常。
- `tick` / `utc`：`Stopwatch.GetTimestamp()`（进程内单调）与 ISO-8601 UTC，用于和停滞日志对齐时间轴。

### 关于 `stale` 与「取消」

**本仓这段重建成没有 `CancellationToken`，也没有渲染侧的 epoch（generation）**，所以：

- 没有新增取消语义 —— 加了就等于改渲染时序，属于「改播放行为」。
- 诊断自己维护一个 `DanmakuRebuildGeneration`（`Interlocked.Increment`），**只用来给计时结果打过期标记**，不参与任何渲染决策。
- 因此日志里的 `stale=true` 含义是「这份耗时对应的那次重建，在跑完前后被一次更新的重建请求追上了」，**不是**「这次重建被取消了」。

这条差异在测试里也照实写：测的是「代号语义」与「旧结果被标记为过期」，不假装渲染结果会被撤销。

## 5. 采样开销

高频路径的成本控制做了四件事：

1. **只取数值，不做格式化**：全程 `Stopwatch.GetTimestamp()` → long，毫秒只在最后算一次。
2. **日志级别是硬闸门**：`if (!Serilog.Log.IsEnabled(LogEventLevel.Information)) return;` —— 关掉 Information 时连字符串构造函数都不会被调（`ToString("0.0", CultureInfo.InvariantCulture)` 全在闸门之后）。
3. **没有无条件大字符串拼接**：唯一一次拼接是 `Serilog.Log.Information(...)` 的结构化调用，参数是 13 个已算好的值。
4. **重建本身是低频事件**：去抖延迟 0 / 200 / 500ms，一次 `Redraw` 才写一行；不是每次进度通知都写。对比：位置通知实测 ~24 次/秒，而弹幕重建在整场播放里只有个位数到几十次。

**没有做的事**（避免「为了测而改」）：

- 没加 `volatile` 计数器做全局统计（会增加写入负担且没必要）。
- 没把日志改成异步 sink（本仓 Serilog 配置是同步 File sink，但每场只有几十行，实测 UiLag 无变化）。
- 没有为此新增任何线程、锁或定时器。

## 6. 行为不变性（改动边界）

明确保证不被破坏的项，逐条对应到实现：

| 约束 | 怎么保证 |
|---|---|
| 不延长持锁范围 | 本仓这段没有锁；`DanmakuRebuildGeneration` 是 `Interlocked`，不进任何临界区 |
| 不破坏 CancellationToken | 没有新增/移除任何 token；原代码这段本来就没有 token |
| 不破坏 epoch / 旧结果丢弃 | 本仓原本没有 epoch；新增的 generation **只写日志**，不参与渲染决策 |
| 不破坏 ASS 临时文件清理 | 本仓没有 ASS 临时文件（无对应逻辑） |
| 不破坏显隐与样式行为 | `SetDanmakuList` / `UpdateDanmakuTime` / `ResetDanmakuStyle` / `Resume` 的**调用顺序与参数完全不变**，只是在两侧加了时间戳 |
| 不改变抛异常行为 | lambda 体**没有加 `catch`**；异常照旧向上抛（`async void` 语义不变），只在 `finally` 里写日志，且日志本身包了 `try/catch` 以免掩盖原异常 |
| 不合并两次 `GetDanmakuList` | 仍按「每个渲染器各转一份」执行，原代码在渲染器为 null 时不转换；新代码用 `_danmakuMaster is null ? null : ...` 保持同样的短路 |
| 不合并两次 `SetDanmakuList` | 保持两次独立调用 |

## 7. 已知局限

1. **`write-ms` 只覆盖 C# 侧的 `SetDanmakuList` 调用**。Win2D/DirectX 渲染器内部的异步建池、纹理上传不在这个区间里（它们的耗时体现在 `work-ms` 与 `total-ms` 的差里，但无法再细分）。
2. **`total-ms` 在「两个渲染器都还没建好」时会包含固定的 500ms `Task.Delay`**。这是原代码既有行为，不是重建工作量；日志里可以靠 `queue-ms` 识别（`queue-ms ≈ 500+` 即属此情况）。实测就出现过 `queue-ms=500.7 / total-ms=507.5` 这种样本。
3. **`stale` 只表示「有更新的重建开始了」**，不区分是哪一个 reason 追上来的。
4. **没有覆盖 `AddRealtimeDanmaku` 单条追加**：它不是重建，代价很小，不属于本任务的测量面。
5. 弹幕计时**不做 Debug 隔离**（与上游一致）：Release 也会写这些日志。若现场觉得刷屏，把 Serilog 的 MinimumLevel 提到 Warning 即可，代码里没有额外开销。
