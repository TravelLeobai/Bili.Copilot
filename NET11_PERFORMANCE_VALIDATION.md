# .NET 11 性能验证报告（Bili.Copilot / bili-copilot-v2）

> 分支 `exp/net11-optimization`，报告基线 HEAD `77ed06a`（四份报告在 `7a81ab6` 新增，其后几次补记只改这四份文件），未 push。
> 数据来自三轮独立测量：**t11**（2026-11-24 20:51–21:10，HEAD `555fabf`，NET11_VERIFY_RAW.md §1–§8）、**t13**（21:1x–21:3x，NET11_P0_TICKPATH.md §5）、**t14**（21:37–22:10，HEAD `dafb451` 工作区干净，NET11_VERIFY_RAW.md 附·A1–A8）。改动前数据来自同一日志文件里的更早会话（18:5x）。
> 本文只回答一个问题：**数据如何**。现状见 `NET11_OPTIMIZATION_AUDIT.md`，改动见 `NET11_MODERNIZATION_REPORT.md`。
> 每个数字都带对照等级与出处。**对照等级只有三种**：
> - **受控 A/B**：同一台机、同一 SDK、同一提交，只改一个变量，两套产物落在不同输出目录；
> - **非受控对照**：两次会话/两个时点，变量不止一个，只能并列展示，不能算收益；
> - **无法对照**：只有单边数据，或两组数据来自不同产物/不同场景。

## 0. 结论

1. **能算受控 A/B 的只有构建产物那一组**：runtime-async 开/关（`-p:Features=` 单变量）下 UI 程序集状态机 592 → 86、async 状态机 585 → 79、dll 5,057,536 → 4,489,728 B（−554 KB，−11.2%）；Visor 3 → 0（NET11_BUILD_AUDIT.md §4）。这是本文唯一可以称为"收益"的数字。
2. **播放期 UiLag 只能算非受控对照**：改后 p50 0.12–0.19 ms / p95 1.10–2.16 ms，改前 p50 0.35–2.07 ms / p95 2.05–9.07 ms（另有 519.79 / 238.68 / 617.61 ms 的起步尖峰）。两次会话相隔 1 小时 20 分、视频不同、机器负载不同（NET11_P0_TICKPATH.md §4；NET11_VERIFY_RAW.md §8.2）。t13 复测（p50 0.08–0.09 / p95 0.18–0.26）没有同期基线，标"无法对照"。
3. **HEAD 重建产物上播放没有成立**：t14 的 5 次尝试全部卡在 mpv 初始化 20 s 超时（`Load.MpvInit p50=20012.34ms`、`Load.MpvInit.Timeout=1`），播放窗口在 730–781 ms 内出现但播放从未进入稳定态；同一采集窗口 `[WRN]` 363 条、`[ERR]` 0 条，其中 360 条是播放器属性回调的 `NullReferenceException`（Position 332）（NET11_VERIFY_RAW.md 附·A5.2、A7.4）。所以 §4、§5 里"成功播放"的数值只能来自 20:0x / 21:3x 两次旧会话，不能当作 HEAD 的复现结果。
4. **`dotnet-counters` 的 0 样本不是工具问题**：部署目录里的 bin 当时是一份带 **27 条**裁剪开关的 AOT 产物，`System.Diagnostics.Tracing.EventSource.IsSupported=false` → EventCounter/EventPipe 采不到东西。换成 HEAD 构建（runtimeconfig 只剩 **4 条**）后同一命令采到 **210 行**（30 采样点 × 7 计数器）（NET11_P0_TICKPATH.md §5a、NET11_VERIFY_RAW.md 附·A0）。t11 对 MSBuild 采到 163 行，证明工具本身可用（NET11_VERIFY_RAW.md §7.3）。
5. **GC 与内存（两轮，场景不同）**：Gen 2 GC **全程 0**、% Time in GC **0%**。t13（成功播放 30 s）GC 堆 16.6–23.7 MB、工作集 421–586 MB、分配速率 40 KB/s–2.48 MB/s；t14（播放窗口开、mpv 超时）GC 堆 26.85–31.45 MB、工作集 424.6–470.8 MB、分配速率 24,552–4,085,432 B/s（均值 331,493）（NET11_P0_TICKPATH.md §5.3；附·A7.2）。两组只有单边数据，都是"无法对照"。
6. **播放器开关循环两轮结果不同**：t13 报 30/30 打开、30/30 关闭、打开中位 7.77 s（NET11_P0_TICKPATH.md §5.4）；t14 用新脚本报打开 29/30、关闭 29/30，失败 1 次是 UIA 读元素抛 `Unrecognized error`（非应用崩溃），启动中位 2,291 ms、打开中位 1,177 ms、关闭中位 232 ms，进程维度无泄漏（附·A6）。两次脚本、判据不同，不能合并成一个数。
7. **冷启动三轮**：t11 中位 **2,611 ms**（未登录态，测已注册的 20:41 构建）；t13 中位 **3,200 ms**（登录态恢复后）；t14 中位 **2,479 ms**（登录态、HEAD 构建、UIA 元素 304）。三次部署产物与登录态都不同，**互不可比**（NET11_VERIFY_RAW.md §4.2；NET11_P0_TICKPATH.md §5.1；附·A4）。
8. **改前/改后受控 A/B 没做成**：回退两个文件后两次构建分别被 `WMC0601`（并发构建打乱 XAML 中间产物）与 `MSB3021/MSB3027`（运行中的实例锁 bin）打断，工作区已恢复 HEAD（NET11_P0_TICKPATH.md §4）。

## 1. 环境与口径

| 项 | 值 | 出处 |
|---|---|---|
| SDK | `11.0.100`（同机另有 9.0.320 / 10.0.403） | NET11_VERIFY_RAW.md §0 |
| OS / CPU / RAM | Windows NT 10.0.29712.0 / Ryzen 7 5800H，16 逻辑核 / 31.4 GB | 同上 |
| 构建输出（隔离） | `E:\build-temp-dsh\out-verifier\` | 同上 |
| 应用包 | `Richasy.BiliCopilot.Dev_1.0.4.0_x64__g7ew4fxhv1f46` | 同上 |
| UiLag 探针 | `Services/PerfProbe.cs`：每 50 ms 往 `DispatcherQueue` 投任务、量排队延迟，10 s 一个聚合窗，进播放器时启动（`PlayerViewModel.cs:158`） | NET11_P0_TICKPATH.md §4 |

三条环境约束会直接影响读数，先记下：

- **同一项目同一配置的两个 Rebuild 不能并发**：`Directory.Build.props:17-19` 只迁了 `OutDir`/`IntDir`，`IntermediateOutputPath` 仍在仓库内，`-p:BuildOutDir` 隔离救不了 `obj`（NET11_VERIFY_RAW.md §0.1 实测）。配置不同的两个构建可以并行（t14 的 Debug 与另一条 Release 同时跑没有撞车，附·A1）。
- **隔离布局下 Debug 与 Release 共用同一个 `bin`**（`OutDir` 不含配置名），后一次构建会覆盖前一次产物；每个配置的数据都在该配置构建完成后立刻采集（NET11_VERIFY_RAW.md §0.1）。
- `-p:BuildOutDir` 对测试工程不生效——`tests\` 不在 `src\` 下、不继承 `src/Directory.Build.props`，产物落 `tests\<proj>\bin\Debug\net11.0\`（NET11_VERIFY_RAW.md §3.2、附·A3）。

## 2. 构建产物与状态机计数

| 指标 | ON | OFF | 变化 | 等级 | 出处 |
|---|---|---|---|---|---|
| `BiliCopilot.UI.dll` 全部状态机（`d__`） | 86 | 592 | −506（−85.5%） | 受控 A/B | NET11_BUILD_AUDIT.md §4 |
| 　其中 `yield` 迭代器 | 7 | 7 | 0 | 受控 A/B | 同上 |
| 　其中 async 方法状态机 | 79 | 585 | −506（−86.5%） | 受控 A/B | 同上 |
| `TypeDef` 总数 | 2,490 | 3,008 | −518 | 受控 A/B | 同上 |
| dll 文件大小 | 4,489,728 B | 5,057,536 B | −554 KB（−11.2%） | 受控 A/B | 同上 |
| `BiliCopilot.Visor.dll` 状态机 | 0 | 3 | −3 | 受控 A/B | 同上 |

- 受控条件：同一台机器、同一 SDK、同一提交；OFF 侧用 `-p:Features=`（全局属性覆盖）+ 独立 `BuildOutDir` 强制重编。**注意**：`Features` 不是 MSBuild 的重编译输入，普通 build 是空操作，必须 `-t:Rebuild` 或换输出目录（NET11_BUILD_AUDIT.md §7、附录 A 第 4 条）。
- 口径：86 是 `d__` 类型数。同一产物按元数据特性口径读出的是 **108 个 `AsyncStateMachineAttribute` 方法**、7 个迭代器方法、86 个 `d__` 类型，三类在 Debug / Release / 已注册构建上完全一致；108 与 86 差 22 的原因原文标为"推断（未 A/B 验证）"（NET11_VERIFY_RAW.md §2.3）。

各配置的绝对产物（无对照，单次构建）：

| 轮次 / 配置 | 大小 | 状态机（`d__`） | async 特性方法 | 等级 | 出处 |
|---|---|---|---|---|---|
| t11 Debug `-t:Rebuild` | 4,495,360 B | 86 | 108 | 无法对照 | NET11_VERIFY_RAW.md §1.2、§2.2 |
| t11 Release `-t:Rebuild` | 3,893,248 B | 86 | 108 | 无法对照 | 同上 |
| t11 已注册的 20:41 构建 | 4,494,848 B | 86 | 108 | 无法对照 | 同上 |
| t14 Debug `-t:Rebuild`（21:41） | 4,495,360 B | 86 | 108 | 无法对照 | 附·A1、A2 |
| t14 Release `-t:Rebuild`（21:47，sha16 `DB9DA0131D04850B`） | 3,893,248 B | 86 | 108 | 无法对照 | 同上 |
| 审计阶段 Release 复测 | — | 86（`TypeDef` 2,466） | — | 无法对照 | NET11_BUILD_AUDIT.md §4 |

两轮构建的 dll 字节数完全相同（Debug 4,495,360 B / Release 3,893,248 B），状态机计数也相同——这说明 t11 与 t14 之间没有代码变化影响产物。t14 的 runtimeconfig 是 Debug **4 条** / Release **5 条**（多 `MetadataUpdater.IsSupported`），与拆掉 `PublishAot` 后的口径一致（附·A0、A1；NET11_P0_FIXES.md 批 2）。

产物清单：Release 覆盖后 `bin` 共 **619 个文件**，含 `BiliCopilot.UI.exe`（99,328 B）与 `AppxManifest.xml`（Version 1.0.4.0），其余三个工程各 4 个文件（NET11_VERIFY_RAW.md §1.2）。

runtimeconfig 的开关数（28 / 27 → 5 / 4）属构建配置口径，不是性能读数，见 `NET11_OPTIMIZATION_AUDIT.md` §2.2；**但它是 counters 那次 0 样本的根因**，见本文 §5。

## 3. 冷启动（到主窗口可见）

| 轮次 | 三次实测 | 中位 | 登录态 / UIA 元素 | 等级 | 出处 |
|---|---|---|---|---|---|
| t11（测已注册的 20:41 构建） | 3,932 / 2,611 / 2,535 ms | **2,611 ms** | 未登录 / 19 | 无法对照 | NET11_VERIFY_RAW.md §4.2 |
| t13（登录态恢复后） | 3,200 / 3,155 / 3,422 ms | **3,200 ms** | 已登录 | 无法对照 | NET11_P0_TICKPATH.md §5.1 |
| t14（登录态、HEAD 构建） | 2,538 / 2,479 / 2,309 ms | **2,479 ms** | 已登录 / 304（导航项 4 个） | 无法对照 | NET11_VERIFY_RAW.md 附·A4 |
| t13 的 30 轮循环内启动样本 | 范围 2,361–4,223 ms（30 次） | 3,137.5 ms | 已登录 | 无法对照 | NET11_P0_TICKPATH.md §5.1 |
| t14 的 30 轮循环内启动样本 | — | 2,291 ms | 已登录 | 无法对照 | NET11_VERIFY_RAW.md 附·A6 |

- 三轮的 3 次样本都 `Responding=True`，主窗口标题「哔哩助理」。
- t11 首轮含冷文件缓存：进程出现就花了 1,329 ms，后两次 108 / 111 ms（NET11_VERIFY_RAW.md §4.2）。t14 三次是 169 / 115 / 116 ms。
- 工作集：t11 222.2 / 226.0 / 224.8 MB（登录页）；t14 417.2 / 509.6 / 458.6 MB（已登录、304 元素）。两次的界面状态不同，差值不能归因到代码。
- t11 脚本自己写出的 `medianMs` 字段是 3,932（算错：`[int](3/2)` 银行家舍入取到最大值），以原始三次值手算的 2,611 ms 为准（NET11_VERIFY_RAW.md §4.2 注）。

## 4. 播放期 UiLag

探针每 50 ms 一次 `DispatcherQueue` 投递，量排队延迟，10 s 聚合（`Services/PerfProbe.cs`）。门槛按 2 ms 看。

### 4.1 改前 / 改后并列（非受控对照）

| 会话 | 起播时刻 | 播放期 p50 | 播放期 p95 | 其他 | 等级 | 出处 |
|---|---|---|---|---|---|---|
| 改前（18:5x，t6 之前的会话） | 18:51:53 `VideoReconfig` | 0.35–2.07 ms（8 个窗口） | 2.05–9.07 ms | 起步窗口 p95 519.79 ms、max 974.04 ms；另有 238.68 / 617.61 ms 尖峰 | 非受控对照 | NET11_VERIFY_RAW.md §8.2；NET11_P0_TICKPATH.md §4 |
| 改后（20:09 启动，含 `69785b5`+`08c0c0a`） | 20:10:16 `VideoReconfig` | 0.12–0.19 ms（7 个窗口） | 1.10–2.16 ms（中位 1.54） | 20:10:06 窗口 p95 785.12 ms 是**进播放页那一下**（在起播前） | 非受控对照 | NET11_P0_TICKPATH.md §4；NET11_VERIFY_RAW.md §8.1 |
| 改后复测（t13，播放成立 107 s） | 21:31:43 `VideoReconfig` | 0.08–0.09 ms（9 个窗口） | 0.18–0.26 ms | 21:31:20 窗口 p95 211 ms 在起播前 | 无法对照 | NET11_P0_TICKPATH.md §5.2 |
| t14（HEAD 构建，播放**未**成立） | 21:56–22:00，mpv 初始化 20 s 超时 | 0.09–0.11 ms | 0.17–419.71 ms（逐窗口：248.85 / 302.61 / 168.61 / 234.46 / 0.20） | 播放窗口存在但 mpv 未就绪；同窗口 360 条 NRE | 无法对照（且不是成功播放场景） | 附·A7.3、A7.4 |

为什么第一组只能算非受控对照（原文逐条列的边界，照抄要点）：两次会话之间代码变了（`69785b5` 提交 20:12:48、`08c0c0a` 20:12:56，20:09 那次用的是改动后的工作树，18:5x 那次是改动前）；播放内容不同（20:09 是 `BV1iFyfzWEMk`，18:5x 的播放对象从日志无法确认）；窗口时长不同；机器状态不同（同期有 dotnet 构建）。因此 p50/p95 只能并列展示，不能当作改造收益的定量结论（NET11_VERIFY_RAW.md §8.2）。

改前的"更早 18:50:02–18:51:12 八行 p50 0.13–0.24 ms、p95 0.60–2.79 ms"属播放前空闲页，未计入；18:56 之后是退出播放器后的窗口，也未计入（同上）。

t14 那行的读法：p50 好看（0.09–0.11 ms）**不等于**没有异常——UiLag 只在播放窗口存在时输出，同窗口的 WRN 有 363 条（附·A7.3 口径提醒）。这一行不能拿来支撑"播放期延迟低"这个结论。

### 4.2 Responding 与进程指标

| 项 | 值 | 等级 | 出处 |
|---|---|---|---|
| 改后播放期 `Responding` | 10/10 `True`（20:10:16→20:12:06） | 无法对照 | NET11_P0_TICKPATH.md §4 |
| 改后播放期进程 CPU | 71.9 → 138.2 s | 无法对照 | 同上 |
| t13 播放 107 秒（21:31:43→21:33:30） | `Responding` 6/6 `True`；CPU 53.2→64.3 s；工作集 565–580 MB | 无法对照 | NET11_P0_TICKPATH.md §5.2 |
| t14 播放尝试（5 次，均超时） | 播放窗口出现 730–781 ms；前 3 次 `create-player:timeout` 分别 20,726 / 20,762 / 20,741 ms；`Load.MpvInit p50=20012.34ms` | 无法对照 | 附·A5.2 |
| t14 开关 30 轮内 Responding | 29 次采样全 `True`（第 26 轮无采样） | 无法对照 | 附·A6 |
| 冷启动后工作集（t11） | 222.2 / 226.0 / 224.8 MB | 无法对照 | NET11_VERIFY_RAW.md §4.2 |
| 冷启动后工作集（t14） | 417.2 / 509.6 / 458.6 MB | 无法对照 | 附·A4 |

## 5. dotnet-counters（30 秒）

工具与命令：`dotnet-counters collect -p <pid> --counters "System.Runtime[gc-heap-size,alloc-rate,gen-0-gc-count,gen-1-gc-count,gen-2-gc-count,time-in-gc,working-set]" --duration 00:00:30`，工具版本 `dotnet-counters 10.0.750501`（NET11_P0_TICKPATH.md §5.3；NET11_VERIFY_RAW.md §7.3）。

### 5.1 t13（播放成立 107 s 的那次会话）

| 计数器 | 范围 | 末值 | 等级 |
|---|---|---|---|
| Working Set | 421–586 MB | 578 MB | 无法对照 |
| GC Heap Size | 16.6–23.7 MB | 19.1 MB | 无法对照 |
| Allocation Rate | 40 KB/s–2.48 MB/s | 2.04 MB/s | 无法对照 |
| Gen 0 / Gen 1 GC | 0–1 / 0–1 次每秒 | 0 | 无法对照 |
| **Gen 2 GC** | **全程 0** | 0 | 无法对照 |
| % Time in GC | 0 % | 0 | 无法对照 |

采样量：**210 行 = 30 个采样点 × 7 个计数器**（NET11_P0_TICKPATH.md §5.3）。

### 5.2 t14（HEAD 构建，播放窗口开、mpv 超时）

| 计数器 | 最小 | 最大 | 平均 | 等级 |
|---|---|---|---|---|
| Gen 2 GC Count | 0 | 0 | 0 | 无法对照 |
| Gen 1 GC Count | 0 | 0 | 0 | 无法对照 |
| Gen 0 GC Count | 0 | 1 次/秒 | 0 | 无法对照 |
| Allocation Rate | 24,552 B/s | 4,085,432 B/s | 331,493 B/s | 无法对照 |
| GC Heap Size | 26.85 MB | 31.45 MB | 29.2 MB | 无法对照 |
| % Time in GC | 0 | 0 | 0 | 无法对照 |
| Working Set | 424.6 MB | 470.8 MB | 436.4 MB | 无法对照 |

采样量同为 **210 行**（附·A7.2）。采集窗口是 22:00:00–22:00:27，"第 1 轮开关循环里，播放窗口已打开"，但 mpv 在 22:00:18 超时——这段是"播放窗口打开、播放未成立"的采样，不是成功播放场景（附·A7.1 注）。与 t13 的数值不可直接比：堆 26.85–31.45 vs 16.6–23.7 MB、工作集 424.6–470.8 vs 421–586 MB，场景与产物都不同。

### 5.3 0 样本的根因（不是工具问题）

t11 对应用进程三次采集（30 s / 10 s / 6 s）都只得到 CSV 表头，退出码 0；同一台机同一工具对 MSBuild（.NET 11）采 6 s 得到 **163 行**（NET11_VERIFY_RAW.md §7.2、§7.3）。t13 定位到原因：部署目录 `src/_build/x64/Debug/…/bin` 里 20:41 那次构建的 `runtimeconfig.json` 带 **27 条**裁剪开关，其中 `System.Diagnostics.Tracing.EventSource.IsSupported=false`，EventSource 关掉后 EventCounter/EventPipe 取不到任何计数（NET11_P0_TICKPATH.md §5a）。t14 在 HEAD 构建（**4 条**开关、无 `EventSource.IsSupported`）上复现了这一点：同一条命令采到 210 行，两轮差异只在部署产物这一处（附·A0）。`dotnet-trace` 对同一进程能 attach，但 3 s `gc-collect` 只产出 1,926 B（NET11_VERIFY_RAW.md §7.3），与该根因一致。

## 6. 播放器开关循环

| 轮次 | 轮数 | 打开 | 关闭 | 失败 | 其他 | 等级 | 出处 |
|---|---|---|---|---|---|---|---|
| t13（`toggle30b.ps1`，每轮重启 → 动态页 → 点开 → 关进程） | 30 | 30/30 | 30/30 | 0 | 每次 `Responding=True`，打开中位 7.77 s；跑前 UI=1/Visor=1，跑后 0/0 | 无法对照 | NET11_P0_TICKPATH.md §5.4 |
| t14（`r2-toggle-final.ps1`，冷启动计时 → 点大卡片 → 关窗口 → 杀进程） | 30 | 29/30 | 29/30 | 1（第 26 轮 UIA `FromHandle` 抛 `Unrecognized error`，非应用崩溃） | 启动中位 2,291 ms；打开中位 1,177 ms（741–2,062）；关闭中位 232 ms（220–551）；关窗后每轮 UI=1/Visor=1；杀进程后 0 残留；跑前 1/1 → 跑后 0/0 | 无法对照 | 附·A6 |

两次的数字不能合并：脚本不同（一个靠重启，一个靠 WM_CLOSE）、打开判据不同（播放窗口出现 vs 播放成立）、线程状态不同（t13 播放成立，t14 播放初始化超时）。t14 的失败是 UIA 侧异常，原文明确"30/30 这个数字没有取到"（附·A8 未验证 3）。

泄漏判定只到进程维度：t14 实测每轮关窗后只剩 1 个 UI + 1 个 Visor，杀进程后 0 残留；但 `create-player:timeout` 日志写的是 `abandoned, no fallback, no dispose`，即失败路径上播放器 VM 没走 dispose——进程数看不出来，不代表对象没留下（附·A6、A8 推断 1）。

## 7. 构建耗时与单测耗时

| 项 | t11 | t14 | 等级 | 出处 |
|---|---|---|---|---|
| Debug `-t:Rebuild`（单工程，含 SolutionDir） | 152.4 s | 167.5 s（`已用时间 00:02:46.77`） | 无法对照 | NET11_VERIFY_RAW.md §1.2；附·A1 |
| Release `-t:Rebuild` | 148.3 s | 170.0 s（`已用时间 00:02:49.08`） | 无法对照 | 同上 |
| CommentPoll 测试 | 84/84，82 ms | 84/84，128 ms | 无法对照 | NET11_VERIFY_RAW.md §3.1；附·A3 |
| Diagnostics 测试 | 23/23，672 ms | 23/23，1 s | 无法对照 | 同上 |

构建耗时是单机单次，没有改前基线；四次构建都是 0 警告 0 错误。t14 的 Release 构建等了另一条线的 Release 构建约 2 分 45 秒才落锁，`已用时间` 只统计构建本身（附·A1）。单测耗时是测试框架报的持续时间；CommentPoll 在 t11 是增量编译后运行（没做 `-t:Rebuild`），口径差异记录在案（NET11_VERIFY_RAW.md §3.2）。

## 8. 缺什么、怎么补

| 缺失项 | 为什么缺 | 补它需要的条件 | 补完能升到什么等级 |
|---|---|---|---|
| 播放能否成立 | t14 5 次尝试全部 mpv 初始化 20 s 超时；21:40 那次成功（`elapsed=11555ms`）与 21:56 之后连续失败的原因未查明（附·A5、A8 未验证 1） | 定位 `create-player:timeout`（`no fallback, no dispose`）的成因后重测 | 从"未验证"升为可测量 |
| 成功播放场景的 counters / UiLag（HEAD 产物） | 两轮 counters 分别来自 t13 的旧会话与 t14 的失败窗口（§5） | 播放成立后按同一条命令与 10 s UiLag 聚合各采 30 s | 无法对照（仍无基线） |
| 播放期 UiLag 的受控 A/B | 回退代码后两次构建被 `WMC0601` / `MSB3021`+`MSB3027` 打断，工作区已恢复 HEAD（NET11_P0_TICKPATH.md §4） | 安静机器（无并发构建、无遗留实例锁 bin）+ 同一个视频 + 改前/改后各 3 轮取中位 | 受控 A/B |
| counters 的改前基线 | 首次采集时部署 bin 是 AOT 产物、EventSource 被关；现已被重建覆盖（NET11_P0_TICKPATH.md §5a） | 保留一份旧产物（带 27 条开关）单独采一次 30 s | 受控 A/B |
| 冷启动的改前基线 | 三轮都只有单边数据，部署产物与登录态都不同（§3） | 同一次构建、同一登录态下改前/改后各 3 次 | 受控 A/B |
| 开关循环的失败率对照 | t13 30/30、t14 29/30，脚本与判据不同（§6） | 固定一个脚本与判据，在旧/新产物上各跑 30 轮 | 受控 A/B |
| 长时播放观察 | 只覆盖 10/10（约 2 分钟）与 6/6（107 秒）两个窗口（§4.2） | ≥30 分钟连续播放 + 每 10 s 采样 | 仍为单边，但覆盖更长 |
| 多架构数据 | 只在 x64 上测（NET11_BUILD_AUDIT.md 附录 C） | ARM64 / x86 各一次构建与冒烟 | 无法对照（补齐覆盖面，不是对照） |
| 与 `PublishAot` 前产物的功能对拍 | 28 条裁剪开关的功能影响没逐条验（附录 C） | AOT=True 产物 + 关键路径清单（JSON 无 source-gen、`System.Management`、EventSource 诊断、Reactor devtools） | 受控 A/B（功能面） |
