# .NET 11 性能验证报告（Bili.Copilot / bili-copilot-v2）

> 分支 `exp/net11-optimization`，HEAD `dfdf0bf`，未 push。数据采集分两轮：t11（2026-11-24 20:51–21:10，HEAD `555fabf`，NET11_VERIFY_RAW.md）与 t13（21:1x–21:3x，HEAD `dafb451` 前，NET11_P0_TICKPATH.md §5）。改动前数据来自同一日志文件里的更早会话（18:5x）。
> 本文只回答一个问题：**数据如何**。现状见 `NET11_OPTIMIZATION_AUDIT.md`，改动见 `NET11_MODERNIZATION_REPORT.md`。
> 每个数字都带对照等级与出处。**对照等级只有三种**：
> - **受控 A/B**：同一台机、同一 SDK、同一提交，只改一个变量，两套产物落在不同输出目录；
> - **非受控对照**：两次会话/两个时点，变量不止一个，只能并列展示，不能算收益；
> - **无法对照**：只有单边数据，或两组数据来自不同产物。

## 0. 结论

1. **能算受控 A/B 的只有构建产物那一组**：runtime-async 开/关（`-p:Features=` 单变量）下 UI 程序集的状态机 592 → 86、async 状态机 585 → 79、dll 5,057,536 → 4,489,728 B（−554 KB，−11.2%）；Visor 3 → 0（NET11_BUILD_AUDIT.md §4）。这两个是本文唯一可以称为"收益"的数字。
2. **播放期 UiLag 只能算非受控对照**：改后 p50 0.12–0.19 ms / p95 1.10–2.16 ms，改前 p50 0.35–2.07 ms / p95 2.05–9.07 ms（另有 519.79 / 238.68 / 617.61 ms 的起步尖峰）。两次会话相隔 1 小时 20 分、视频不同、机器负载不同（NET11_P0_TICKPATH.md §4；NET11_VERIFY_RAW.md §8.2）。t13 复测（p50 0.08–0.09 / p95 0.18–0.26）没有同期基线，标"无法对照"。
3. **`dotnet-counters` 的 0 样本不是工具问题**：部署目录里的 bin 当时是一份带 **27 条**裁剪开关的 AOT 产物，`System.Diagnostics.Tracing.EventSource.IsSupported=false` → EventCounter/EventPipe 采不到东西。用当前 HEAD 重建（runtimeconfig 只剩 **4 条**）后 counters 立刻正常，30 s 采到 **210 行**（30 采样点 × 7 计数器）（NET11_P0_TICKPATH.md §5a、§5.3）。t11 对 MSBuild 采到 163 行，证明工具本身可用（NET11_VERIFY_RAW.md §7.3）。
4. **播放期内存与 GC（单边，无法对照）**：Gen 2 GC 全程 **0**、% Time in GC **0%**、GC 堆 16.6–23.7 MB、工作集 421–586 MB、分配速率 40 KB/s–2.48 MB/s（NET11_P0_TICKPATH.md §5.3）。
5. **播放器开关 30 轮 30/30 通过**，失败 0，跑后无残留进程；打开中位 7.77 s（NET11_P0_TICKPATH.md §5.4）。这是绝对通过率，没有对照。
6. **冷启动三次中位 3,200 ms**（t13，3,200 / 3,155 / 3,422 ms）；t11 同一指标中位 2,611 ms（2,535 / 2,611 / 3,932 ms）。两轮之间部署产物不同（t11 测的是 20:41 那次已注册构建，不是它自己编的），**不可比**（NET11_VERIFY_RAW.md §4.2；NET11_P0_TICKPATH.md §5.1）。
7. **改前/改后受控 A/B 没做成**：回退两个文件后两次构建分别被 `WMC0601`（并发构建打乱 XAML 中间产物）与 `MSB3021/MSB3027`（运行中的实例锁 bin）打断，工作区已恢复 HEAD（NET11_P0_TICKPATH.md §4）。

## 1. 环境与口径

| 项 | 值 | 出处 |
|---|---|---|
| SDK | `11.0.100`（同机另有 9.0.320 / 10.0.403） | NET11_VERIFY_RAW.md §0 |
| OS / CPU / RAM | Windows NT 10.0.29712.0 / Ryzen 7 5800H，16 逻辑核 / 31.4 GB | 同上 |
| 构建输出（隔离） | `E:\build-temp-dsh\out-verifier\` | 同上 |
| 应用包 | `Richasy.BiliCopilot.Dev_1.0.4.0_x64__g7ew4fxhv1f46` | 同上 |
| UiLag 探针 | `Services/PerfProbe.cs`：每 50 ms 往 `DispatcherQueue` 投任务、量排队延迟，10 s 一个聚合窗，进播放器时启动（`PlayerViewModel.cs:158`） | NET11_P0_TICKPATH.md §4 |

两条环境约束会直接影响读数，先记下：

- **同一项目同一配置的两个 Rebuild 不能并发**：`Directory.Build.props:17-19` 只迁了 `OutDir`/`IntDir`，`IntermediateOutputPath` 仍在仓库内，`-p:BuildOutDir` 隔离救不了 `obj`（NET11_VERIFY_RAW.md §0.1 实测）。
- **隔离布局下 Debug 与 Release 共用同一个 `bin`**（`OutDir` 不含配置名），后一次构建会覆盖前一次产物；本报告每个配置的数据都在该配置构建完成后立刻采集（NET11_VERIFY_RAW.md §0.1）。

## 2. 构建产物与状态机计数

| 指标 | ON | OFF | 变化 | 等级 | 出处 |
|---|---|---|---|---|---|
| `BiliCopilot.UI.dll` 全部状态机（`d__`） | 86 | 592 | −506（−85.5%） | 受控 A/B | NET11_BUILD_AUDIT.md §4 |
| 　其中 `yield` 迭代器 | 7 | 7 | 0 | 受控 A/B | 同上 |
| 　其中 async 方法状态机 | 79 | 585 | −506（−86.5%） | 受控 A/B | 同上 |
| `TypeDef` 总数 | 2,490 | 3,008 | −518 | 受控 A/B | 同上 |
| dll 文件大小 | 4,489,728 B | 5,057,536 B | −554 KB（−11.2%） | 受控 A/B | 同上 |
| `BiliCopilot.Visor.dll` 状态机 | 0 | 3 | −3 | 受控 A/B | 同上 |

- 受控条件：同一台机器、同一 SDK、同一提交；OFF 侧用 `-p:Features=`（全局属性覆盖）+ 独立 `BuildOutDir` 强制重编。**注意**：`Features` 不是 MSBuild 的重编译输入，普通 build 是空操作，必须 `-t:Rebuild` 或换输出目录，否则会看到"改了没反应"（NET11_BUILD_AUDIT.md §7、附录 A 第 4 条）。
- 口径：86 是 `d__` 类型数。同一产物用元数据特性口径读出的是 **108 个 `AsyncStateMachineAttribute` 方法**、7 个迭代器方法、86 个 `d__` 类型，三类在 Debug / Release / 已注册构建上完全一致；108 与 86 差 22 的原因原文标为"推断（未 A/B 验证）"（NET11_VERIFY_RAW.md §2.2、§2.3）。

各配置的绝对产物（无对照，单次构建）：

| 配置 / 产物 | 大小 | 状态机（`d__`） | async 特性方法 | 等级 | 出处 |
|---|---|---|---|---|---|
| Debug `-t:Rebuild`（HEAD `555fabf`） | 4,495,360 B | 86 | 108 | 无法对照 | NET11_VERIFY_RAW.md §1.2、§2.2 |
| Release `-t:Rebuild`（HEAD `555fabf`） | 3,893,248 B | 86 | 108 | 无法对照 | 同上 |
| 已注册的 20:41 构建 | 4,494,848 B | 86 | 108 | 无法对照 | 同上 |
| Release 复测（审计阶段） | — | 86（`TypeDef` 2,466） | — | 无法对照 | NET11_BUILD_AUDIT.md §4 |

Debug 与已注册构建差 512 B，元数据计数一致（NET11_VERIFY_RAW.md §2.2 表注）。产物清单：Release 覆盖后 `bin` 共 **619 个文件**，含 `BiliCopilot.UI.exe`（99,328 B）与 `AppxManifest.xml`（Version 1.0.4.0），其余三个工程各 4 个文件（§1.2）。

runtimeconfig 的开关数（28 / 27 → 5 / 4）属于构建配置口径，不是性能读数，见 `NET11_OPTIMIZATION_AUDIT.md` §2.2；**但它是 counters 那次 0 样本的根因**，见本文 §6。

## 3. 冷启动（到主窗口可见）

| 轮次 | 三次实测 | 中位 | 等级 | 出处 |
|---|---|---|---|---|
| t11（未登录态，测已注册的 20:41 构建） | 3,932 / 2,611 / 2,535 ms | **2,611 ms** | 无法对照 | NET11_VERIFY_RAW.md §4.2 |
| t13（登录态恢复后，当前 HEAD 重建） | 3,200 / 3,155 / 3,422 ms | **3,200 ms** | 无法对照 | NET11_P0_TICKPATH.md §5.1 |
| 30 轮开关循环内的 30 次启动 | 范围 2,361–4,223 ms | 3,137.5 ms | 无法对照 | NET11_P0_TICKPATH.md §5.1 |

- 三次都 `Responding=True`，主窗口标题「哔哩助理」（两轮一致）。t11 的首轮含冷文件缓存：进程出现就花了 1,329 ms，后两次 108 / 111 ms（NET11_VERIFY_RAW.md §4.2）。
- **两轮中位不可比**：t11 测的是 20:41 那次已注册构建（脚本没有重注册包，按指示避免丢登录态），t13 用的是重建后的 bin；两次的启动路径、登录态、部署产物都不同。
- t11 脚本自己写出的 `medianMs` 字段是 3,932（算错：`[int](3/2)` 银行家舍入取到最大值），以原始三次值手算的 2,611 ms 为准（NET11_VERIFY_RAW.md §4.2 注）。

## 4. 播放期 UiLag

探针：每 50 ms 一次 `DispatcherQueue` 投递，量排队延迟，10 s 聚合（`Services/PerfProbe.cs`）。门槛按 2 ms 看。

### 4.1 改前 / 改后并列（非受控对照）

| 会话 | 起播时刻 | 播放期 p50 | 播放期 p95 | 其他 | 等级 | 出处 |
|---|---|---|---|---|---|---|
| 改前（18:5x，t6 之前的会话） | 18:51:53 `VideoReconfig` | 0.35–2.07 ms（8 个窗口） | 2.05–9.07 ms | 起步窗口 p95 519.79 ms、max 974.04 ms；另有 238.68 / 617.61 ms 尖峰 | 非受控对照 | NET11_VERIFY_RAW.md §8.2；NET11_P0_TICKPATH.md §4 |
| 改后（20:09 启动，含 `69785b5`+`08c0c0a`） | 20:10:16 `VideoReconfig` | 0.12–0.19 ms（7 个窗口） | 1.10–2.16 ms（中位 1.54） | 20:10:06 窗口 p95 785.12 ms 是**进播放页那一下**（在起播前） | 非受控对照 | NET11_P0_TICKPATH.md §4；NET11_VERIFY_RAW.md §8.1 |
| 改后复测（t13，当前 HEAD 重建） | 21:31:43 `VideoReconfig` | 0.08–0.09 ms（9 个窗口） | 0.18–0.26 ms | 21:31:20 窗口 p95 211 ms 在起播前 | 无法对照 | NET11_P0_TICKPATH.md §5.2 |

为什么第一组只能算非受控对照（原文逐条列的边界，照抄要点）：两次会话之间代码变了（`69785b5` 提交 20:12:48、`08c0c0a` 20:12:56，20:09 那次用的是改动后的工作树，18:5x 那次是改动前）；播放内容不同（20:09 是 `BV1iFyfzWEMk`，18:5x 的播放对象从日志无法确认）；窗口时长不同；机器状态不同（同期有 dotnet 构建）。因此 p50/p95 只能并列展示，不能当作改造收益的定量结论（NET11_VERIFY_RAW.md §8.2）。

改前的"更早 18:50:02–18:51:12 八行 p50 0.13–0.24 ms、p95 0.60–2.79 ms"属播放前空闲页，未计入；18:56 之后是退出播放器后的窗口，也未计入（同上）。

### 4.2 Responding 与进程指标

| 项 | 值 | 等级 | 出处 |
|---|---|---|---|
| 改后播放期 `Responding` | 10/10 `True`（20:10:16→20:12:06） | 无法对照 | NET11_P0_TICKPATH.md §4 |
| 改后播放期进程 CPU | 71.9 → 138.2 s | 无法对照 | 同上 |
| t13 播放 107 秒（21:31:43→21:33:30） | `Responding` 6/6 `True`；CPU 53.2→64.3 s；工作集 565–580 MB | 无法对照 | NET11_P0_TICKPATH.md §5.2 |
| 冷启动后工作集（t11） | 222.2 / 226.0 / 224.8 MB | 无法对照 | NET11_VERIFY_RAW.md §4.2 |

## 5. dotnet-counters（播放中 30 秒）

工具与命令：`dotnet-counters collect -p <pid> --counters "System.Runtime[gc-heap-size,alloc-rate,gen-0-gc-count,gen-1-gc-count,gen-2-gc-count,time-in-gc,working-set]" --duration 00:00:30`，工具版本 `dotnet-counters 10.0.750501`（NET11_P0_TICKPATH.md §5.3；NET11_VERIFY_RAW.md §7.3）。

| 计数器 | 范围 | 末值 | 等级 |
|---|---|---|---|
| Working Set | 421–586 MB | 578 MB | 无法对照 |
| GC Heap Size | 16.6–23.7 MB | 19.1 MB | 无法对照 |
| Allocation Rate | 40 KB/s–2.48 MB/s | 2.04 MB/s | 无法对照 |
| Gen 0 / Gen 1 GC | 0–1 / 0–1 次每秒 | 0 | 无法对照 |
| **Gen 2 GC** | **全程 0** | 0 | 无法对照 |
| % Time in GC | 0 % | 0 | 无法对照 |

采样量：**210 行 = 30 个采样点 × 7 个计数器**（NET11_P0_TICKPATH.md §5.3）。没有改前基线，所以这一组只能说"当前状态是这样"，不能说"改好了多少"。

**0 样本的根因（不是工具问题）**：t11 对应用进程三次采集（30 s / 10 s / 6 s）都只得到 CSV 表头，退出码 0；同一台机同一工具对 MSBuild（.NET 11）采 6 s 得到 **163 行**（NET11_VERIFY_RAW.md §7.2、§7.3）。t13 定位到原因：部署目录 `src/_build/x64/Debug/…/bin` 里 20:41 那次构建的 `runtimeconfig.json` 带 **27 条**裁剪开关，其中 `System.Diagnostics.Tracing.EventSource.IsSupported=false`，EventSource 关掉后 EventCounter/EventPipe 取不到任何计数；用当前 HEAD 重建（开关降到 **4 条**）后 counters 恢复正常（NET11_P0_TICKPATH.md §5a）。`dotnet-trace` 对同一进程能 attach，但 3 s `gc-collect` 只产出 1,926 B（NET11_VERIFY_RAW.md §7.3），与该根因一致。

## 6. 播放器开关循环

| 指标 | 结果 | 等级 | 出处 |
|---|---|---|---|
| 轮数 | 30 | — | NET11_P0_TICKPATH.md §5.4 |
| 成功打开播放 | 30/30，每次 `Responding=True`，打开中位 7.77 s | 无法对照 | 同上 |
| 成功关闭 | 30/30，失败 0 | 无法对照 | 同上 |
| 进程泄漏 | 跑前 UI=1 / Visor=1；跑后 UI=0 / Visor=0 | 无法对照 | 同上 |

方法说明：仓库外的 `toggle-player.ps1` 只能跑一轮（关闭播放器后应用停在播放页，没有导航栏与搜索框，第 2 轮起必然 `search-unavailable`，实测 `-Rounds 2` 是 1/2 打开），所以 30 轮改成"每轮重启 → 动态页 → 点开视频 → 关进程"的循环（`E:\build-temp-dsh\login-recovery\toggle30b.ps1`，结果 `E:\build-temp-dsh\verifier\results\toggle30-restart.json`）。每轮重启意味着这 30 轮同时含 30 次冷启动（§3 第三行）。

## 7. 构建耗时与单测耗时

| 项 | 值 | 等级 | 出处 |
|---|---|---|---|
| Debug `-t:Rebuild`（单工程，含 SolutionDir） | 152.4 s（`已用时间 00:02:31.03`） | 无法对照 | NET11_VERIFY_RAW.md §1.2 |
| Release `-t:Rebuild` | 148.3 s（`已用时间 00:02:27.74`） | 无法对照 | 同上 |
| CommentPoll 测试 | 84/84，82 ms | 无法对照 | NET11_VERIFY_RAW.md §3.1 |
| Diagnostics 测试 | 23/23，672 ms | 无法对照 | 同上 |

构建耗时是单机单次，没有改前基线；两次都是 0 警告 0 错误。单测耗时是测试框架自己报的持续时间，CommentPoll 那次是增量编译后运行（没做 `-t:Rebuild`），口径差异记录在案（NET11_VERIFY_RAW.md §3.2）。

## 8. 缺什么、怎么补

| 缺失项 | 为什么缺 | 补它需要的条件 | 补完能升到什么等级 |
|---|---|---|---|
| 播放期 UiLag 的受控 A/B | 回退代码后两次构建被 `WMC0601` / `MSB3021`+`MSB3027` 打断，工作区已恢复 HEAD（NET11_P0_TICKPATH.md §4） | 安静机器（无并发构建、无遗留实例锁 bin）+ 同一个视频 + 改前/改后各 3 轮取中位 | 受控 A/B |
| counters 的改前基线 | 首次采集时部署 bin 是 AOT 产物、EventSource 被关；现已被重建覆盖（NET11_P0_TICKPATH.md §5a） | 保留一份旧产物（带 27 条开关）单独采一次 30 s | 受控 A/B |
| 冷启动的改前基线 | 两轮都只有单边数据，且部署产物不同（§3） | 同一次构建、同一登录态下各跑 3 次的改前/改后对照 | 受控 A/B |
| 播放器开关的失败率对照 | 30/30 全通过，没有旧版本的同口径数据（§6） | 旧产物上跑同一脚本 30 轮 | 受控 A/B |
| 播放期 `Responding` 的长时观察 | 只覆盖 10/10（约 2 分钟）与 6/6（107 秒）两个窗口（§4.2） | ≥30 分钟连续播放 + 每 10 s 采样 | 仍为单边，但覆盖更长 |
| 多架构数据 | 只在 x64 上测（NET11_BUILD_AUDIT.md 附录 C） | ARM64 / x86 各一次构建与冒烟 | 无法对照（补齐覆盖面，不是对照） |
| 与 `PublishAot` 前产物的功能对拍 | 28 条裁剪开关的功能影响没逐条验（附录 C） | AOT=True 产物 + 关键路径清单（JSON 无 source-gen、`System.Management`、EventSource 诊断、Reactor devtools） | 受控 A/B（功能面） |
