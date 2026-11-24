# .NET 11 正式版特性清单（Bili.Copilot 可落地版）

- **任务**：t1 — 研究 .NET 11 正式版可用优化并落成清单
- **调研日期**：2026-11-24
- **目标版本**：.NET 11.0.0 GA（2026-11-10 发布，STS，两年支持）
- **仓库基线**：本地分支 `exp/net11-optimization`；主项目 `net11.0-windows10.0.22621.0`，已开 `<Features>runtime-async=on</Features>`
- **产出性质**：只写文档，不改代码，不推送远程

## 0. 证据约定与状态判定

本文所有结论都带一手来源链接，来源优先级固定为：

1. `learn.microsoft.com`（dotnet/docs 仓库的 GA 版文档）
2. `dotnet/core` 的 `release-notes/11.0/**`
3. `dotnet/runtime`、`dotnet/roslyn` 的设计文档与 PR
4. `devblogs.microsoft.com`（官方博客）

第三方文章仅用于**交叉印证**，且在正文中显式标注为"第三方"。凡本文无法从上述一手来源确认的内容，统一收进第 7 节"未决项"，不在正文里当事实用。

**状态四档的定义**（后文严格按此标注）：

| 标记 | 含义 | 判定依据 |
|---|---|---|
| **正式** | GA 可用，不需要任何开关或诊断豁免 | GA 文档直接描述，无 WARNING/诊断号 |
| **实验** | 需要 `#pragma warning disable SYSLIB5xxx` 之类显式豁免，可能变更 | 文档给出诊断号并在 WARNING 中写明"experimental" |
| **预览** | 需要语言/构建开关（`LangVersion=preview`、`<Features>` 等），不随默认行为生效 | 需要显式开关才能编译或启用 |
| **已取消** | 本版本被移除 / 需求被废弃 / 开关被删除 | 文档或发布说明明确写 removed / no longer / obsolete |

---

## 1. Runtime Async V2

### 1.1 它改的是什么

编译器不再为 `async` 方法生成状态机类，而是把方法体基本按原样发成 IL，并在元数据上标记该方法为 async（`MethodImplOptions.Async`，IL 里的 `async` 标志位）。挂起/恢复、续体对象的布局、哪些局部变量要跨越挂起点保存（hoist），都交给 JIT 与运行时完成。

- 一手依据：ECMA-335 修订草案 [dotnet/runtime docs/design/specs/runtime-async.md](https://github.com/dotnet/runtime/blob/main/docs/design/specs/runtime-async.md)（新增 `MethodImplAttributes.Async = 0x2000`、`System.Runtime.CompilerServices.AsyncHelpers.Await/AwaitAwaiter/UnsafeAwaitAwaiter`）
- 编译器那一侧的实现说明：[dotnet/roslyn Runtime Async Design.md](https://github.com/dotnet/roslyn/blob/main/docs/compilers/CSharp/Runtime%20Async%20Design.md)
- GA 文档：[What's new in the .NET 11 runtime#Runtime Async](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/runtime#runtime-async)
- 逐项优化的展开：[Performance Improvements in .NET 11#Runtime Async](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-11/#runtime-async)

### 1.2 状态与开关

| 项 | 结论 | 出处 |
|---|---|---|
| 启用方式 | 项目文件加 `<Features>$(Features);runtime-async=on</Features>`，**没有**"默认开启"这回事 | [runtime.md](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/runtime#runtime-async) |
| `EnablePreviewFeatures` | 面向 `net11.0` 的项目**不再需要** | 同上 |
| 环境变量 `DOTNET_RuntimeAsync` / `UNSUPPORTED_RuntimeAsync` | **已移除**（V1 时代的开关） | 同上（NOTE 段） |
| 按项目退出 | 用 `<UseRuntimeAsync>false</UseRuntimeAsync>` | 同上 |
| BCL 自身 | 运行时库整体以 `runtime-async=on` 编译，库内已无编译器生成的状态机 | 同上 |
| 规范状态 | ECMA-335 修订仍是 **draft**，原文写"when the feature is officially supported" | [runtime-async.md 首段](https://github.com/dotnet/runtime/blob/main/docs/design/specs/runtime-async.md) |

> **状态判定**：本文标为 **预览（opt-in）**。理由有两条：一是必须显式打开构建开关，默认不开；二是规范本身仍是草案。第三方报道把它称为 "remains a preview and is turned on with a feature flag"（[InfoQ](https://infoq.com/news/2026/11/dotnet-11-release)），与官方文档"不再需要 EnablePreviewFeatures"并不冲突——那是两件事：不要求 *PreviewFeatures* 开关，不等于默认启用。

### 1.3 哪些 async 方法真的会走 runtime async

编译器**逐方法**决策，运行时支持面由规范限定。

| 方法形态 | 是否走 runtime async | 依据 |
|---|---|---|
| `async Task M()` | 是 | 规范：四种返回类型之一 |
| `async Task<T> M()` | 是 | 同上 |
| `async ValueTask M()` | 是 | 同上 |
| `async ValueTask<T> M()` | 是 | 同上 |
| 返回自定义 task-like 类型 | 否，回退编译器状态机 | 规范："Only four types will be supported as the return type"；Roslyn 设计文档："Any method returning a different `Task`-like type is not transformed to runtime async form" |
| 用自定义 `[AsyncMethodBuilder]`（如 `PoolingAsyncValueTaskMethodBuilder`） | 否，回退状态机 | 该 builder 的语义依赖编译器生成的 `MoveNext`/字段，运行时挂起模型给不出 builder 需要的钩子 |
| `async void` | 否，回退状态机 | `void` 不在规范允许的四种返回类型内 |
| 异步迭代器 `async IAsyncEnumerable<T>`（`yield return`） | 否，回退状态机 | 规范只列四种返回类型；Roslyn 设计文档该场景仍标为 `TODO: Async iterators` |
| 目标框架 ≤ net10.0 / netstandard | 否 | 老 corlib 没有 `AsyncHelpers`（`RuntimeFeature.Async` 检测，见规范第 I.8.4.5 节） |

`await` 表达式本身另有规则：被 await 的东西不是 `Task`/`Task<T>`/`ValueTask`/`ValueTask<T>`（含对应的 `Configured*Awaitable`）时，编译器改用 `AsyncHelpers.AwaitAwaiter` / `UnsafeAwaitAwaiter` 走通用等待路径（Roslyn 设计文档 "`AsyncHelpers.Await` Scenarios" / "await any other type"）。也就是说：**方法可以是 runtime async，而其中某个 `await` 仍走传统 awaiter 协议**。

### 1.4 `async void` 与迭代器为什么不行（工程上的因果）

1. **没有可返回的 task 对象**：runtime async 的挂起/恢复契约建立在"方法返回 `Task`/`ValueTask` 家族"之上——规范规定返回 `Task`/`ValueTask` 时 `ret` 前栈为空、返回泛型版本时栈上留类型参数。`async void` 没有这个返回槽，运行时无从挂接续体。
2. **`void` 无法被 await，也就无法观测完成**：`async void` 的异常直接进同步上下文，没有 task 承载；把它纳入 runtime async 需要另设一套完成通知机制，规范未定义。
3. **迭代器是两种状态机的复合**：`IAsyncEnumerable<T>` + `yield return` 需要"每次 MoveNextAsync 一个子状态"的语义，编译器当前生成的 `AsyncIteratorStateMachine` 同时承担枚举器与异步方法双重职责。Roslyn 设计文档把这一项留作 TODO，说明 V2 未覆盖。
4. **规范另有永久限制，与迭代器天然冲突**：挂起点不允许出现在 `catch`/`filter`/`finally`/`fault` 块里（`try` 保护块内允许），`tail` 前缀与 `localloc` 暂时禁用，byref 与 byref-like 局部不能跨挂起点 hoist。这些限制先落在"方法体形态"上，再谈返回类型。

### 1.5 语义边界：写代码时会撞到的地方

| 限制 | 后果 | 出处 |
|---|---|---|
| byref 局部不能跨挂起点 hoist | 挂起点之后读到的是 `null` | 规范第 I.8.4.5 节 |
| byref-like（`Span<T>` 等）不能 hoist | 挂起点之后是 `default` | 同上 |
| `fixed`/pinning 局部不能 hoist | 挂起点之后为 `null` | 同上 |
| 挂起点不得出现在 handler 块（`catch`/`filter`/`finally`/`fault`） | `try` 内可以，handler 内不行 | 同上 |
| `tail` 前缀禁用、`localloc` 禁用 | 这两条标为"临时限制" | 同上 |
| `MethodImplOptions.Async` 与 `Synchronized` 组合非法；`byref`/`ref-like` 返回值非法；vararg 方法非法 | 编译期报错 | 同上 |
| 语义本身不变 | 首个真正挂起的 `await` 之前同步执行、异常仍存在 task 里、`ExecutionContext`/`AsyncLocal` 仍流动、`ConfigureAwait(false)` 行为不变 | [runtime.md](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/runtime#runtime-async) 与第三方 [ndepend 说明](https://blog.ndepend.com/c-async-await-explained)（仅作交叉印证） |

### 1.6 性能增量（GA 文档口径）

- **活的调用栈变干净**：`new StackTrace()` / 调试器 / profiler 看到的帧数从"每个 async 方法 3 帧"降到 1 帧（文档示例：13 帧 → 5 帧）。异常栈不受影响，因为 `ExceptionDispatchInfo` 那条路径本来就已经清理过。
- **JIT 直接编译"同步版 task 返回方法"**：不再经过 thunk；把方法尾部调用转成 runtime-async 调用并 await 原本要返回的 task，省一层间接。
- **挂起点尾部合并**：生成的代码更小。
- **续体缓存**：runtime-async 可调用 task thunk 的续体复用。
- **已池化方法退出**：本身就走对象池的方法不再套 runtime async。
- **异步方法进分层编译**：热 async 方法能在预热后拿到 tier 1 优化。
- **Task/ValueTask 工厂内建识别**：`Task.FromResult`、`Task.CompletedTask`、`ValueTask.FromResult` 被折叠进更快的 async 路径。
- **尾部 await 优化**：直接返回另一个 async 结果时恢复隐式尾调用；`await Task.Yield()` 在 runtime-async 路径下不再额外分配。
- **跳过空的 ExecutionContext 捕获**：续体没有需要恢复的环境状态时，跳过整轮 capture/restore；`Task`/`Task<T>`/`ValueTask`/`ValueTask<T>` 都受益。

以上全部出自 [runtime.md#Runtime Async 性能段](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/runtime#runtime-async)，逐条 PR 与基准在 [Performance Improvements in .NET 11#Runtime Async](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-11/#runtime-async)。

### 1.7 AOT / R2R 支持情况

| 场景 | 结论 | 出处 |
|---|---|---|
| NativeAOT | 支持。文档明确 "Runtime Async supports NativeAOT and ReadyToRun compilation." | [runtime.md#NativeAOT and ReadyToRun support](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/runtime#nativeaot-and-readytorun-support) |
| ReadyToRun / crossgen2 | 之前禁止 runtime-async 方法参与内联的限制**已移除**；异步测试在 crossgen2 与 composite R2R 下全绿；无 await 的 async 调用（同步快路径）确认可端到端内联 | 同上 |
| 泛型协变返回 `Task` → `Task<T>` 的覆盖 | 运行时生成 void 返回的 thunk 桥接调用约定差异，虚派发对两种形态都成立；**NativeAOT 同样修复** | 同上 |
| 池化/解释器 | 池化方法 opt-out；解释器 perf-map 日志支持改进 | 同上 + [runtime.md#Runtime diagnostics](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/runtime#runtime-diagnostics) |

对本仓库的直接含义：`BiliCopilot.Visor` 是 `PublishAot=true` + `IsAotCompatible=true`（`src/Desktop/BiliCopilot.Visor/BiliCopilot.Visor.csproj:9,14`），runtime async 在它这条发布路径上是被官方支持的；`BiliCopilot.UI` 目前 `PublishReadyToRun=False`、`PublishAot=True`（`BiliCopilot.UI.csproj:1499-1500`），所以 crossgen2 内联这条对本仓库当前发布配置**没有实际收益**，只有打开 R2R 时才吃到。

### 1.8 落到本仓库要注意的三件事

1. **改 `<Features>` 必须 `-t:Rebuild`**：仓库自己已经在 `BiliCopilot.UI.csproj:23-26` 注释里记了这个坑——MSBuild 不把 `Features` 当重编译输入，普通 build 是空操作，编出来的还是老状态机程序集。
2. **`async void` 那 81 处不会变快**：全仓 `async void` 命中 81 行，绝大多数是 WinUI 事件处理器（`OnLoadedAsync`、`OnListUpdatedAsync`、`OnClick` 之类）。它们按第 1.3 节一定回退状态机，**收益为零、风险为零**——不要把它们算进 runtime-async 的收益估算里。真正受益的是返回 `Task` 的 MVVM 命令与 Service 方法（如 `AsyncRelayCommand` 包的那些）。
3. **无 await 的 async 方法反而是快路径**：`BiliCopilot.UI.csproj` 的 `NoWarn` 里带 `CS1998`，说明仓库确实存在"async 但没 await"的方法。这类方法在 runtime-async + R2R 下正是文档点名的"可被内联的同步快路径"，值得单独统计（留给后续扫描任务）。

---

## 2. JIT、PGO、分层编译的实际状态

### 2.1 内联（inlining）

.NET 11 没有"默认内联策略大变"，增量集中在**给内联创造机会**和**放松内联预算**：

- 去虚拟化成功后才能内联，内联后再触发常量折叠/BCE/栈分配——这是 .NET 11 收益的主链条（[Performance Improvements in .NET 11#Deabstraction](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-11/#deabstraction)）。
- 放宽 `Vector128/256/512`、`Vector<T>` 这类"薄包装"类型的 inliner 预算，让它们更容易被内联成 JIT 内建节点（[perf 博客 JIT 段](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-11/#deabstraction)，对应 dotnet/runtime#127433）。
- crossgen2（R2R）对 runtime-async 方法的内联限制**已取消**（见 1.7）。
- 内联进来的代码现在也会被 PGO 插桩（dotnet/runtime#119658），后续 tier 1 的 profile 更完整（[perf 博客 JIT 段](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-11/#deabstraction)）。

### 2.2 去虚拟化（devirtualization / GDV）

GA 文档单列一节：

- 泛型虚方法（GVM）可去虚拟化，含非共享实例化与不需要运行时查找的共享实例化。
- 泛型接口上的**默认接口方法**可去虚拟化。
- `Activator.CreateInstance<T>()` 的返回值按"精确类型"处理，后续调用可去虚拟化。
- 典型效果：接口调用链内联后整段折成 `Console.WriteLine("test")`。

出处：[runtime.md#Devirtualization improvements](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/runtime#devirtualization-improvements)。逐 PR 展开（#120866/#122023/#128702/#123183 R2R/#130202 NativeAOT/#128485）见 [perf 博客 Deabstraction](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-11/#deabstraction)。

注意区分两件事：**静态去虚拟化**（JIT 靠精确类型直接定目标）与 **GDV**（PGO 猜类型 + 类型检查守卫）。后者属于 Dynamic PGO，见 2.4。

### 2.3 边界检查消除（BCE）

- `i + cns < len` 这类"索引加常量与长度比较"现在能消掉边界检查。
- 从末尾索引（`values[^1]`）的冗余检查消得更多。
- 空 span 守卫里的 `length != 0` 断言可传递给 `span[0]` 的检查。
- 冗余 checked 上下文可被证明并移除。
- 冗余分支/重复测试消除（外层谓词被内层蕴含时折掉）。

出处：[runtime.md#JIT improvements](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/runtime#jit-improvements) 与 [runtime.md#Eliminating bounds checks after an empty-span guard](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/runtime#eliminating-bounds-checks-after-an-empty-span-guard)。逐 PR（#125056/#124184/#124705/#127439/#127488/#129349）见 [perf 博客 Bounds Checks](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-11/#bounds-checks)。

### 2.4 Dynamic PGO

| 问题 | 结论 | 出处 |
|---|---|---|
| .NET 11 是否新增 | **不是新增**。Dynamic PGO 从 **.NET 8 起默认开启**，不再需要配置项 | [What's new in .NET 8 runtime#Dynamic PGO](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-8/runtime)（原文："dynamic profile-guided optimization (PGO) ... is now enabled by default. You no longer need to use a runtime configuration option to enable it."） |
| 开关在哪 | `TieredPGO` / `System.Runtime.TieredPGO` / `DOTNET_TieredPGO` | [Compilation config#Profile-guided optimization](https://learn.microsoft.com/dotnet/core/runtime-config/compilation#profile-guided-optimization) |
| 依赖关系 | 关掉分层编译等于关掉 Dynamic PGO（没有 tier 0 就没有插桩） | 同上 |
| .NET 11 的增量 | ① 内联后的代码也插桩（profile 更全）；② async 方法纳入分层编译，热 async 方法能升 tier 1；③ R2R 镜像里 `Comparer<T>.Default`/`EqualityComparer<T>.Default` 专用化，官方基准称依赖默认比较器的集合操作最多快 20× | [runtime.md#Runtime Async 性能](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/runtime#runtime-async)、[runtime.md#ReadyToRun improvements](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/runtime#readytorun-improvements)、[perf 博客](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-11/#deabstraction) |

补充：`DynamicPgo-InstrumentedTiers` 描述了"只插桩热代码"的 instrumented tier 机制，用来避免 tier 0 插桩拖慢启动，并让 R2R 代码也吃到 PGO（[dotnet/runtime 设计文档](https://github.com/dotnet/runtime/blob/main/docs/design/features/DynamicPgo-InstrumentedTiers.md)）。

**对本仓库的实操结论**：Dynamic PGO 已经在生效，不需要动；反而是**不要**为"提速"去设 `TieredPGO=false` 或 `TieredCompilation=false`。WinUI 桌面应用属长生命周期进程，tier 1 收益能吃满。

### 2.5 分层编译

- 默认状态：**.NET Core 3.0 起默认开启**，Quick JIT 亦默认开（[Compilation config#Tiered compilation](https://learn.microsoft.com/dotnet/core/runtime-config/compilation#tiered-compilation)）。
- 设计文档（历史与策略：30 次调用阈值、100ms 启动计时器）：[dotnet/runtime docs/design/features/tiered-compilation.md](https://github.com/dotnet/runtime/blob/main/docs/design/features/tiered-compilation.md)。
- .NET 11 增量：async 版本进分层编译（1.6）、R2R 默认比较器专用化（2.4）、"单 IG prolog"限制取消（寄存器多的复杂 prolog 不再走回退路径）（[runtime.md#JIT improvements](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/runtime#jit-improvements)）。

### 2.6 其他 JIT / 代码生成增量（正式，无需开关）

常量折叠 `string.Equals`/`SequenceEqual`（含 UTF-8 字面量）、`switch` 表达式折叠成无分支检查、`uint→float/double` 加速、`Math.BigMul(long,long,out long)` 在 x64 出单条 `MUL`、`SELECT(cond,cns,cns)` 折叠、饱和式浮点→小整数转换、`Half` 走硬件 FP16 指令、AVX-VNNI-512 内建、SVE2 `ShiftRightLogicalNarrowingSaturate(Even|Odd)`、`Vector<T>` 在 SVE 下按引用传递。

出处：[runtime.md#JIT improvements](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/runtime#jit-improvements) / [#Hardware intrinsics and code generation](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/runtime#hardware-intrinsics-and-code-generation)。

### 2.7 硬件基线变化（发布前必须知道）

.NET 11 **提高了最低硬件要求**，这是会改变"能不能跑"的破坏性变更：

| 平台 | 旧 JIT/AOT 最低 | 新 JIT/AOT 最低 | 旧 R2R 目标 | 新 R2R 目标 |
|---|---|---|---|---|
| Windows x64 | `x86-64-v1` | **`x86-64-v2`**（CMOV/CX8/SSE/SSE2 + CX16/POPCNT/SSE3/SSSE3/SSE4.1/SSE4.2） | `x86-64-v2` | **`x86-64-v3`**（+AVX/AVX2/BMI1/BMI2/F16C/FMA/LZCNT/MOVBE） |
| Windows Arm64 | `armv8.0-a` | **`armv8.0-a + LSE`** | `armv8.0-a` | **`armv8.2-a + RCPC`** |

不满足时会直接拒绝启动，提示 "The current CPU is missing one or more of the baseline instruction sets."。出处：[runtime.md#Updated minimum hardware requirements](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/runtime#updated-minimum-hardware-requirements)、[兼容性说明](https://learn.microsoft.com/dotnet/core/compatibility/jit/11/minimum-hardware-requirements)。

对本仓库：桌面应用面向普通用户，2013 年前后的老机器（无 SSE4.2）在 .NET 11 下**起不来**。当前 `PublishReadyToRun=False`，所以没有"R2R 镜像按 v3 编译、老 CPU 退化"的问题；一旦将来打开 R2R，就要按 v3 评估。

---

## 3. 库 API 增量（按域，带状态与仓库相关性）

先给三条**全局适用性判断**，它们会影响下面所有"可落地"结论：

1. 本仓库**不使用** `Microsoft.Extensions.*`（DI/Hosting/Options/Configuration/Caching）。`src/` 下所有 csproj 里没有任何 `Microsoft.Extensions` 的 `PackageReference`。所以 3.13 那一批（MemoryCache OTel 指标、`IAsyncStartupValidator`、`ChangeToken.OnChange` 异步重载、DataAnnotations 异步校验）**当前不适用**，除非后续引入 DI 容器。
2. JSON 走的是 **source generation**（`GlobalSerializeContext`、`JsonGenContext` 两个 `JsonSerializerContext`），并且仓库注释明确"这个应用全局禁用了 JSON 反射序列化"（`Services/BiliJumpService.cs:62`）。所以新 JSON 特性要看**是否支持源生成**，不支持的就直接排除。
3. HTTP 是**多实例 `HttpClient` + `SocketsHttpHandler`**（`Services/` 下十几处，多处带 `ConnectCallback = IpV4Connector.ConnectAsync`）。3.9 的 handler 级新属性可以直接用在这些地方。

### 3.1 GC / 分配

| 项 | 状态 | 说明 | 出处 |
|---|---|---|---|
| 依赖句柄（dependent handle）随引用对象老化 | 正式（运行时行为） | `ConditionalWeakTable` 这类机制的句柄不再被每代回收反复扫描；官方基准 `CollectGen0` 在 10 万/100 万句柄下分别 1.522ms→255.5μs、10.737ms→310.7μs | [perf 博客#Write Barriers and Garbage Collection](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-11/#write-barriers-and-garbage-collection) |
| Arm64 mark-list 排序向量化 | 正式 | 压缩式回收里 "plug" 规划的排序在 Arm64 上也走向量化 | 同上（dotnet/runtime#110692） |
| 写屏障消除/合并 | 正式 | 精确类型数组存储可去掉协变检查并优化屏障；整结构体拷贝可批量写；相邻字段合并成更宽的写 | 同上 |
| >1024 逻辑 CPU 的机器可初始化 | 正式 | `sched_getaffinity` 动态分配 CPU 集；GC 仍有 1024 heap 上限 | [runtime.md#Platform support for more than 1024 CPUs](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/runtime#platform-support-for-more-than-1024-cpus) |
| 新增 GC 模式 / 新配置旋钮 | **无** | .NET 11 GA 文档没有新增 GC 模式；`runtime-config/garbage-collector` 的既有旋钮不变 | [GC config](https://learn.microsoft.com/dotnet/core/runtime-config/garbage-collector) |

⚠️ 常见的误报：把"依赖句柄老化""写屏障优化"写成"GC 新增 API"。它们是运行时内部行为，**没有对应的公开 API**，也不需要配置。

### 3.2 集合

| API / 变更 | 状态 | 说明 | 仓库相关性 |
|---|---|---|---|
| `BitArray(ReadOnlySpan<bool/byte/int>)` | 正式 | 省掉 `span.ToArray()` 的中间数组 | 低 |
| `BitArray.PopCount()` | 正式 | 直接数置位 | 低 |
| `EqualityComparer<T>.Create(keySelector, comparer?)` | 正式 | 从 key selector 造比较器 | 中（弹幕/评论去重场景可用） |
| `ConcurrentQueue<T>.TryDequeue/TryPeek` 可靠性修复 | 正式 | 段冻结期间不再误报空队列 | 中（并发队列用在日志/事件总线） |
| `System.Numerics.Tensors` 形状对齐/重叠布局校验 | 正式（包） | 依赖特定 shape 或重叠拷贝的代码要复查 | 低 |

出处：[libraries.md#Collections improvements](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/libraries#collections-improvements)。

### 3.3 LINQ

| API | 状态 | 说明 |
|---|---|---|
| `LeftJoin` / `RightJoin` / `FullJoin` | 正式 | 三个外连接**直接返回元组**，不需要 result selector；有可选 `IEqualityComparer<T>` 重载 |
| `Join` / `GroupJoin` 的元组重载 | 正式 | 省掉 result selector 的样板 |
| 以上三者同时在 `Enumerable` / `Queryable` / **`AsyncEnumerable`** 上提供 | 正式 | 这点对本仓库重要：异步流式 LINQ 也能用外连接 |

出处：[libraries.md#LINQ join improvements](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/libraries#linq-join-improvements)。性能侧：`SequenceEqual` 在"另一边是数组/List/其它 span 可比序列"时保持快路径（925.8ns→122.2ns）、`Slice` 改走 `CopyTo`、`FindAll` 前四个匹配用栈内缓冲（[perf 博客#Collections and LINQ](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-11/#collections-and-linq)）。

### 3.4 Span / 内存流 / 低层 I/O

| API | 状态 | 说明 | 仓库相关性 |
|---|---|---|---|
| `ReadOnlyMemoryStream` | 正式 | 把 `ReadOnlyMemory<byte>` 当只读流，零拷贝 | 高（图片/音视频字节直接喂 `StreamContent`） |
| `WritableMemoryStream` | 正式 | 可写 `Memory<byte>` 当定长流 | 中 |
| `ReadOnlySequenceStream` | 正式 | 直接把 `ReadOnlySequence<byte>`（`System.IO.Pipelines` 的缓冲）当流，不摊平 | 中高 |
| `StringStream` | 正式 | `string`/`ReadOnlyMemory<char>` 按指定编码当流 | 高（把 JSON/XML 文本喂给解析器） |
| `SafeFileHandle.Type` | 正式 | 报告句柄是文件/管道/套接字/目录 | 中 |
| `SafeFileHandle.CreateAnonymousPipe(...)` | 正式 | 创建两端异步特性独立的匿名管道对 | 中（Visor 子进程通信可考虑） |
| `RandomAccess.Read/Write` 支持非寻址句柄（管道） | 正式 | 之前只支持普通文件 | 中 |
| Windows 上 `Process` 重定向 stdout/stderr 改走 overlapped I/O | 正式 | 减少线程池阻塞 | 中（Visor 启动/日志抓取） |
| `System.IO.Pipelines` 读写锁竞争降低 | 正式 | 高扇入场景 | 中 |

出处：[libraries.md#In-memory stream adapters](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/libraries#in-memory-stream-adapters)、[#Low-level I/O improvements](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/libraries#low-level-io-improvements)。

### 3.5 SearchValues（重点澄清）

| 事实 | 结论 | 证据 |
|---|---|---|
| .NET 11 是否新增 `SearchValues` API | **没有**。`SearchValues.Create` 的三个重载（`ReadOnlySpan<byte>`、`ReadOnlySpan<char>`、`ReadOnlySpan<string>, StringComparison`）标注的适用版本都是 **.NET 8 及以后** | [SearchValues.Create API 页（?view=net-11.0）](https://learn.microsoft.com/dotnet/api/system.buffers.searchvalues.create?view=net-11.0)；`libraries.md` 全文无 SearchValues 条目 |
| .NET 11 与 SearchValues 相关的实际变化 | 只在**底层搜索内核**：Arm64 上的 `Vector*.Count`/`IndexOf`/`LastIndexOf` 不再绕道 `ExtractMostSignificantBits`，官方口径是相关工作负载提升 5–50%；即 `IndexOfAnyAsciiSearcher` 变快 | [runtime.md#Hardware intrinsics and code generation](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/runtime#hardware-intrinsics-and-code-generation) |
| 仓库现状 | `src/` 下 `SearchValues` 命中 0 处 | 全仓 grep |

结论：`SearchValues` 属于"既有能力"（.NET 8），本仓库还没用；把它引入属**新用既有 API**，不能记到 .NET 11 头上。

### 3.6 字符串 / 文本 / Base64 / UTF

| API / 变更 | 状态 | 说明 |
|---|---|---|
| `String` 系列 `Rune` 重载：`Contains`/`StartsWith`/`EndsWith`/`IndexOf`/`LastIndexOf`/`Replace`/`Split`/`Trim`/`TrimStart`/`TrimEnd` | 正式 | 直接按 Unicode 标量值操作；多数带 `StringComparison` 重载 |
| `Char.Equals(char, StringComparison)` | 正式 | 单字符也能用文化感知比较 |
| `TextInfo.ToLower(Rune)` / `ToUpper(Rune)` | 正式 | 单标量大小写转换 |
| `StringBuilder.MoveChunks(StringBuilder)` | 正式 | 静态方法，把源内容**搬**进新 builder（源长度变 0），不复制字符 |
| `Base64.EncodeToChars/EncodeToString/EncodeToUtf8/DecodeFromChars/DecodeFromUtf8` | 正式 | 高层（返回数组/字符串）+ 低层 span 两套；替换掉 `Convert.ToBase64String` 的分配 |
| `Unicode.Utf16.IsValid(ReadOnlySpan<char>)`、`Utf8/Utf16.IndexOfInvalidSubsequence` | 正式 | 一次扫描给出合法性/首个非法子序列位置，错误信息能定位 |
| `double/float/Half` 十六进制格式化与解析 | 正式 | IEEE-754 位精确，可对照 C 的 `%a`；适合 golden file 测试 |
| `Console` 支持 `FORCE_COLOR` | 正式 | 输出被重定向时不再吞掉 ANSI |

出处：[libraries.md#String and character enhancements](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/libraries#string-and-character-enhancements)、[#Base64 encoding improvements](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/libraries#base64-encoding-improvements)、[#UTF validation and invalid-subsequence search](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/libraries#utf-validation-and-invalid-subsequence-search)、[#Console FORCE_COLOR support](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/libraries#console-force_color-support)。

性能侧（Arm64 与 x86 分别有份）：UTF-16↔UTF-8 的代理对计数/校验向量化（2.994μs→856.8ns）、UTF-8 解码全 ASCII 快路径（443.5ns→210.6ns）、`ToBase64String(..., InsertLineBreaks)` 走向量化核心（[perf 博客#Strings and Spans](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-11/#strings-and-spans)）。

### 3.7 正则

| 项 | 状态 | 说明 |
|---|---|---|
| `RegexOptions.AnyNewLine` | 正式 | `^`/`$`/`.` 把整套 Unicode 换行（`\r\n`、`\n`、`\u0085`、`\u2028`、`\u2029`）都当行终止符 |
| 非回溯引擎超线性修复 | 正式 | 某些嵌套循环模式不再超线性，之前发散的用例现在结果正确 |
| 正则编译器/源生成器 `resumeAt` 修复 | 正式 | 条件出现在循环体内的场景 |
| SYSLIB1045 代码修复重复类名修复 | 正式 | 多处 partial 声明同一类时不再造重复类名 |
| 编译/源生成正则最终清理趟 | 正式（性能） | 整模式优化后再做一次清理，共享前缀可被提出；官方基准 550.9ns→282.8ns |

出处：[libraries.md#Regular expression improvements](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/libraries#regular-expression-improvements)、[perf 博客#Searching and Comparing](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-11/#searching-and-comparing)。

注意：**正则源生成器本身是 .NET 7 的能力**，不是 11 新增（见第 5 节）。.NET 11 新增的是 `AnyNewLine` 选项 + 引擎/生成器修复。

### 3.8 System.Text.Json

| API / 变更 | 状态 | 源生成是否支持 | 说明 |
|---|---|---|---|
| **C# union 序列化** | 正式 | 支持 | 直接写当前 case，无包装、无 `$type`；按 JSON token 种类选 case；对象 case 可用 `JsonUnionAttribute` + `JsonUnionTypeStructuralClassifier` 按根级属性名消歧 |
| **F# 判别联合** | 正式 | **不支持**（仅反射；不兼容 source gen / trimming / NativeAOT） | 对 Visor（AOT）不可用 |
| **JSON Lines 输出**（`SerializeAsyncEnumerable*` + `topLevelValues: true`） | 正式 | 支持 | 写到 `Stream` 或 `PipeWriter`，逐条流式、不整条缓冲；固定 `\n`，忽略 `WriteIndented` |
| `closed` 继承体系的多态推断 | 正式 | 支持 | `InferClosedTypePolymorphism`，可在 options / 源生成 / 单类型三处开关 |
| 开放泛型派生类型注册 | 正式 | 支持 | `[JsonDerivedType(typeof(Derived<>), "derived")]`，支持直接绑定/重排/部分具体/嵌套数组四种模式 |
| `JsonNamingPolicy.PascalCase` + `[JsonNamingPolicy]`（类型/成员级） | 正式 | 支持 | 优先级：`[JsonPropertyName]` > 成员级 > 类型级 > options > 原名 |
| 类型级 `[JsonIgnore]` 设默认忽略条件 | 正式 | 支持 | `Always` 不允许用在类型级（反射抛异常，源生成报 SYSLIB1226 并忽略） |
| `JsonSerializerOptions.GetTypeInfo<T>()` / `TryGetTypeInfo<T>()` | 正式 | — | 泛型返回 `JsonTypeInfo<T>`，不用手写 cast；AOT/源生成场景更顺 |
| 内置转换器：`BFloat16` / `Decimal32` / `Decimal64` / `Decimal128` | 正式 | 支持 | 支持 number handling、字典键、JSON Schema 导出 |
| 开放泛型转换器 `[JsonConverter(typeof(OptionConverter<>))]` | 正式 | 支持 | 不再需要 `JsonConverterFactory` |
| 扩展数据支持 `IReadOnlyDictionary<string, object/JsonElement>` | 正式 | 支持 | 反序列化时给可写 `Dictionary` |
| `IReadOnlySet<T>` 序列化/反序列化为数组/`HashSet<T>` | 正式 | 支持 | 源生成用 `JsonMetadataServices.CreateIReadOnlySetInfo*` |
| `Utf8JsonWriter.Reset(options, stream/IBufferWriter)` | 正式 | — | 写出后可换目标换 options 复用，便于池化 |
| 源生成对 `[JsonInclude]` 私有访问器/字段、`[JsonConstructor]` 不可访问构造、`in`/`ref`/`out`/`ref readonly` 构造函数参数 | 正式 | 支持 | `init`-only 属性缺省时保留初始化值 |

出处：[libraries.md#System.Text.Json improvements](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/libraries#systemtextjson-improvements)、[union 序列化专文](https://learn.microsoft.com/dotnet/standard/serialization/system-text-json/union-types)、[多态 closed 体系](https://learn.microsoft.com/dotnet/standard/serialization/system-text-json/polymorphism#infer-polymorphism-from-a-closed-hierarchy)。

对本仓库的强约束：反射被全局禁用，所以 **F# 判别联合那条直接排除**；C# union / JSONL / closed 多态 / PascalCase 命名策略都能走源生成，可用。但要用 C# union，得先引入 C# 15 的 `union` 类型（见第 4 节）。

### 3.9 HTTP / 网络 / TLS / DNS / QUIC

| API / 变更 | 状态 | 说明 | 仓库相关性 |
|---|---|---|---|
| `GZipCompressedContent` / `BrotliCompressedContent` / `ZstandardCompressedContent` | 正式 | **请求体**压缩包装器，自动设 `Content-Encoding` 并流式压缩 | 高（上传弹幕/评论/日志聚合） |
| `SocketsHttpHandler.ShouldEvictConnection` | 正式 | 按连接年龄/端点/DNS 变化决定是否淘汰连接，替代只看生命周期的策略 | 高（CDN 池/加速器场景） |
| `SocketsHttpHandler.InitialHttp2MaxConcurrentStreams` | 正式 | 服务端 `SETTINGS` 到达前，限制每条新 HTTP/2 连接的并发流数（默认 100）；同主机历史上更低的限制还会进一步下调 | 高（多处 `SocketsHttpHandler`） |
| `Dns.ResolveSrv/ResolveMx/ResolveTxt/ResolveCName/ResolvePtr/ResolveNs`（+ Async） | 正式 | 返回 `DnsResult<T>`（记录 + 响应码 + 负缓存 TTL）；`DnsResolver` 实例 API 支持自定义 DNS 服务器；**Linux 上也支持了** | 中 |
| HTTP/2 遇 Windows 认证自动降级到 HTTP/1.1 | 正式 | NTLM/Negotiate 与 HTTP/2 的绑定认证冲突，不再需要手工设 `Version` | 低 |
| `QuicStream.Priority` / `DefaultPriority` | 正式 | RFC 9218，0（最高）–255（最低），默认 127 | 低（除非上 HTTP/3） |
| `MediaTypeNames.Video.*` | 正式 | mp4/mpeg/ogg/quicktime/webm 常量 | 中 |
| HTTP `open_connections` / `active_requests` 指标改为 observable instrument | 正式 | 直接读 `MeterListener` 的要调 `RecordObservableInstruments()` | 低 |
| TLS：`SslStream` 服务端握手边界检查修复、Linux 上证书校验失败改为标准 TLS alert、Unix 上 Negotiate 支持 channel binding 校验 | 正式 | 排障与安全加固 | 低 |
| `TlsContext` / `TlsSession` / `TlsBufferSession` / `TlsSocketSession` / `TlsOperationStatus` | **实验**（SYSLIB5007） | 调用方驱动的非阻塞 TLS 状态机，应用自己管缓冲与 I/O 调度 | 暂不用 |
| `CompositeMLKem` / `Hpke` | **实验**（SYSLIB5006 / SYSLIB5009） | 后量子混合 KEM / HPKE 托管实现 | 暂不用 |

出处：[libraries.md#Networking and transport security](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/libraries#networking-and-transport-security)。

性能侧：socket 地址族/类型/协议缓存去掉全局锁（`dotnet/runtime#124997`），并发建连时不再串行化（[perf 博客#Networking](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-11/#networking)）。

### 3.10 压缩 / 归档

| API / 变更 | 状态 | 说明 |
|---|---|---|
| Zstandard：`ZstandardStream` / `ZstandardEncoder` / `ZstandardDecoder` / `ZstandardDictionary` | 正式（**11 新增**） | Preview 1 以独立包 `System.IO.Compression.Zstandard` 引入，Preview 3 并入 `System.IO.Compression` 命名空间；`ZstandardDictionary.Train` 返回原始字典字节交 `Create(data, quality)` |
| Span 版 Deflate/ZLib/GZip 编解码（`DeflateEncoder`/`ZLibEncoder`/`GZipEncoder` 等） | 正式 | 形状对齐 `BrotliEncoder`/`ZstandardEncoder`，不分配 `Stream` 即可压缩解压；这些无流类型还新增 `Reset()` |
| `ZipArchiveEntry.Open(FileAccess)` / `OpenAsync(FileAccess, ct)`、`CompressionMethod` 属性 | 正式 | 按读写模式打开条目；`ZipCompressionMethod` = Stored/Deflate/Deflate64 |
| ZIP 读取校验 CRC32 | 正式（行为变更） | 损坏/截断的包不再静默通过，抛 `InvalidDataException` |
| ZIP 条目密码支持 | 正式 | `Open(ReadOnlySpan<char>)` 系列 + `ZipEncryptionMethod` |
| `DeflateStream`/`GZipStream` 空载荷也写头尾 | 正式（行为变更） | 之前不写任何字节，现在输出合法压缩流 |
| `TarFile.CreateFromDirectory` 选格式（Pax/Ustar/GNU/V7） | 正式 | 之前固定 Pax |
| `TarReader` 支持 GNU sparse 1.0（PAX） | 正式 | 与主流 `tar` 默认写出的稀疏格式对齐 |

出处：[libraries.md#Compression enhancements](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/libraries#compression-enhancements)、[#Zstandard compression](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/libraries#zstandard-compression)、[Preview 1 发布说明](https://github.com/dotnet/core/blob/main/release-notes/11.0/preview/preview1/libraries.md)、[Preview 3 发布说明](https://github.com/dotnet/core/blob/main/release-notes/11.0/preview/preview3/libraries.md)。

仓库现状：`Services/AiAdAnalyzer.cs:552` 用 `GZipStream` 做**解压**，空载荷行为变更不影响这条路径；Zstandard 目前 0 处使用。

### 3.11 进程 / 诊断

| API / 变更 | 状态 | 说明 |
|---|---|---|
| `Process.Run*` / `RunAsync*`、`RunAndCaptureText*` | 正式 | 一次性启动并拿退出状态/合并 stdout+stderr |
| `Process.ReadAllText/Bytes/Lines*`（含 async） | 正式 | 一次调用读完子进程输出；`ReadAllLines*` 返回 `ProcessOutputLine` 区分 stdout/stderr，不用自己切字符串 |
| `Process.StartAndForget*`、`ProcessStartInfo.StartDetached`、`KillOnParentExit`（Windows） | 正式 | 不等待的启动；脱离父会话/终端；父进程退出时终止子进程 |
| `SafeProcessHandle.Start/ProcessId/Kill/Signal/WaitForExit(Async)` | 正式 | 不经过 `Process` 直接按句柄操作 |
| `ProcessStartInfo.InheritedHandles` / `StandardInput/Output/Error` Handle | 正式 | 精确控制继承哪些句柄；直接给已开的 `SafeFileHandle` |
| `ProcessStartInfo.StartSuspended` + `SafeProcessHandle.Resume`（Windows/macOS） | 正式 | 挂起启动，挂调试器/配 job object 后再放行 |
| `Process.TryGetProcessById(int, out Process?)` | 正式 | 不存在时返回 `false` 而不是抛异常 |
| `Process.Signal(PosixSignal)`、`WaitForExitStatus*` / `TryWaitForExitStatus` | 正式 | 区分正常退出与信号终止 |
| 崩溃报告（in-process） | 正式（Linux/macOS 新增） | `DOTNET_EnableCrashReport=1` / `DOTNET_EnableCrashReportOnly=1`，在崩溃进程内落盘托管栈、模块表、关键运行时状态 |

出处：[libraries.md#Diagnostics and process execution](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/libraries#diagnostics-and-process-execution)、[runtime.md#In-process crash report logging](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/runtime#in-process-crash-report-logging)。

对 Visor 这种"UI 起来一个独立子进程"的架构，`StartAndForget` + `KillOnParentExit` + `ReadAllLinesAsync` 是可直接替换现有手写 `OutputDataReceived` 的三件套（属后续升级任务，本文只登记）。

### 3.12 数值 / 向量

| API | 状态 | 说明 |
|---|---|---|
| `Decimal32` / `Decimal64` / `Decimal128`（IEEE 754-2019 十进制浮点） | 正式 | 支持泛型数学、无穷、NaN；超越函数在 ±1 附近/对数接近 1 时精度大幅修正（`Decimal128.Acos(1-1e-30)` 从约 10²⁴ ULP 误差降到 1 ULP 以内）；**无公开 API 变更但结果会变** |
| `INumberBase<T>.TryParsePartial*` | 正式 | 解析并报告消耗了多少输入，适合 CSV 这类分隔文本，不用先切子串 |
| `Complex<T>` | 正式 | 泛型复数，支持 `float`/`Half`/`BFloat16`/十进制浮点 |
| `BitConverter` 的 `BFloat16` 支持 | 正式 | `GetBytes`、`ToBFloat16`、`*Bits*` 互转 |
| `Random.NextInteger<T>` / `NextBinaryFloat<T>` | 正式 | 任意整型/二进制浮点型 |
| `Matrix4x4.GetDeterminant` SSE 向量化 | 正式（性能） | 约 15% 提升 |
| 跨 lane 向量构造/组合：`CreateGeometricSequence`/`CreateAlternatingSequence`/`CreateHarmonicSequence`、`Zip`/`ZipLower`/`ZipUpper`/`Unzip`/`UnzipEven`/`UnzipOdd`、`Concat*` 家族、`Reverse` | 正式 | `Vector64/128/256/512<T>` 与 `Vector<T>`；图像处理/DSP 少写手写 shuffle |
| `Decimal`↔二进制浮点、`BigInteger`→二进制浮点转换改为"正确舍入" | 正式（**行为变更**） | 用 .NET 11 SDK 重编后结果可能与旧版不同，**没有兼容开关**；`1.23` 这种 double 字面量转 decimal 不再得到 `1.23` |

出处：[libraries.md#Collections, numerics, and low-level I/O](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/libraries#collections-numerics-and-low-level-io)、[十进制/BigInteger 转换行为变更](https://learn.microsoft.com/dotnet/core/compatibility/core-libraries/11/decimal-biginteger-floating-point-conversions)。

### 3.13 DI / 配置 / 校验 / 缓存（当前不适用，登记备查）

| 项 | 状态 | 备注 |
|---|---|---|
| `MemoryCache` 内置 OTel 指标（`dotnet.cache.requests/evictions/entries/estimated_size`） | 正式 | 需 `TrackStatistics=true`；hit/miss 标签是 `dotnet.cache.request.result`（早期构建叫 `...type`） |
| `OptionsBuilder<T>.Validate<TValidator>()` 泛型重载 | 正式 | 校验器从 DI 取，不再传工厂 |
| `ChangeToken.OnChange` 的 `Func<Task>` 重载 | 正式 | 避免 `async void`；重注册在 task 完成后进行。**行为变更**：会重绑已有的 Task 返回回调 |
| `IAsyncStartupValidator` | 正式 | `IStartupValidator` 已标记过时 |
| DataAnnotations 异步校验（`AsyncValidationAttribute` / `IAsyncValidatableObject` / `Validator.ValidateObjectAsync` 等） | 正式 | 校验规则可做 I/O |
| `ValidationAttribute.FormatMessage` | 正式 | 让宿主给模板、特性填占位符，便于本地化 |
| `Activity` 追踪规则化配置（`AddTracing`）、`ActivitySourceFactory`、`ActivitySource` 解封 | 正式 | 不用手接 `ActivityListener` |
| `[LoggerMessage]` 支持泛型方法 | 正式 | 主要为了不装箱；`allows ref struct` 不支持（SYSLIB1011） |
| `ConfigurationIgnoreAttribute`；空数组绑到构造参数不再抛 | 正式 | — |
| `HybridCache.GetOrCreateAsync` 带可变工厂上下文 | 正式 | 可在回调里设过期/本地缓存大小 |
| `PhysicalFilesWatcher` 可靠性修复 | 正式 | 根目录不存在、相对路径、不支持监听的 FS |

出处：[libraries.md#Caching, configuration, and logging](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/libraries#caching-configuration-and-logging)。**本仓库 `src/` 无任何 `Microsoft.Extensions.*` 引用**，本节内容要等引入 DI/Hosting 才有意义。

### 3.14 其他值得一提的

- `MetadataLoadContext.GetLoadContext(Assembly)`、`Type.GetNullableUnderlyingType()`、`ConstructorInfo.GetGenericArguments()` 覆盖、`AssemblyLoadContext.SetAssemblyLocationOverride` —— 正式（[libraries.md#Extensions and developer platform](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/libraries#extensions-and-developer-platform)）。
- `Uri.UriSchemeData`；`StringSyntaxAttribute.CSharp/FSharp/VisualBasic` 常量 —— 正式。
- `UnionAttribute` / `IUnion`（`System.Runtime.CompilerServices`）：**给库作者用的运行时脚手架**，不是给业务代码直接用的 API；C# 编译器与源生成器才是生产者（[libraries.md#Discriminated-union scaffolding](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/libraries#discriminated-union-scaffolding)）。
- `CborReader` 增量读（`isFinalBlock:false` + `NeedsMoreData` + `SlideData`，仅 `Lax` 模式）—— 正式。
- X25519 密钥交换、`CryptographicOperations.FixedTimeEquals(span, byte)`、AES Key Wrap（RFC 3394 / RFC 5649）、Apple 平台上 `AesGcm`/`ChaCha20Poly1305` 空 AAD 快路径、CMS 用 ML-KEM（仅非 Windows）—— 正式（[libraries.md#Cryptography](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/libraries#cryptography)）。
- `SafeFileHandle.Type`、`FileShare`/文件锁在 iOS/tvOS 默认关闭（`DOTNET_SYSTEM_IO_DISABLEFILELOCKING=0` 可恢复）—— 正式，桌面无关。

### 3.15 状态汇总表（实验 / 预览 / 已取消）

| 类别 | 项 | 状态 | 依据 |
|---|---|---|---|
| 实验 | `CompositeMLKem` | 实验，需豁免 SYSLIB5006 | [libraries.md#Experimental composite ML-KEM and HPKE APIs](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/libraries#experimental-composite-ml-kem-and-hpke-apis) |
| 实验 | `Hpke` | 实验，需豁免 SYSLIB5009 | 同上 |
| 实验 | `TlsContext`/`TlsSession`/`TlsBufferSession`/`TlsSocketSession`/`TlsOperationStatus` | 实验，报 SYSLIB5007 | [libraries.md#Experimental caller-driven TLS sessions](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/libraries#experimental-caller-driven-tls-sessions) |
| 预览 | Runtime Async V2 | 预览（opt-in via `<Features>`；规范草案未定稿） | 见 1.2 |
| 预览 | C# 15 memory safety（`unsafe`/`safe` 新模型、指针放宽） | 预览（`LangVersion=preview` + `updated-memory-safety-rules`） | [csharp-15#Memory safety](https://learn.microsoft.com/dotnet/csharp/whats-new/csharp-15#memory-safety) |
| 已取消 | `DOTNET_RuntimeAsync`、`UNSUPPORTED_RuntimeAsync` 环境变量 | 已移除，改用 `<UseRuntimeAsync>false</UseRuntimeAsync>` | [runtime.md#Runtime Async](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/runtime#runtime-async) |
| 已取消 | 用 runtime-async 需 `EnablePreviewFeatures` 的要求 | 已取消（net11.0 不再需要） | 同上 |
| 已弃用 | `Microsoft.Extensions.Options.IStartupValidator` | 过时，改用 `IAsyncStartupValidator` | [libraries.md](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/libraries#asynchronous-validation-with-dataannotations) |
| 已移除（平台相关） | macOS 上的 DSA | 移除 | [compatibility/11](https://learn.microsoft.com/dotnet/core/compatibility/11) |
| 已移除（SDK） | Template engine 的 `netstandard2.0` 目标 | 移除（NuGet 7.0 不再支持 netstandard） | [Preview 4 SDK 发布说明](https://github.com/dotnet/core/blob/main/release-notes/11.0/preview/preview4/sdk.md) |

---

## 4. C# 15：本仓库可以落地的语言特性

前提状态：`src/Directory.Build.props:4` 已经是 `<LangVersion>preview</LangVersion>`，所以 C# 15 **全部正式特性编译上已经可用**，不需要改语言版本；`TreatWarningsAsErrors=true` 意味着新语法带来的任何警告都会变成构建失败，落地时要逐个验证。

| 特性 | 状态 | 本仓库落地建议 | 风险 |
|---|---|---|---|
| **labeled `break`/`continue`** | 正式 | 最安全的一项。`Controls/**` 里嵌套循环 + 布尔 flag 的写法可直接换掉；IDE0410 会提示可替换点 | 无 |
| **collection expression arguments** `[with(capacity: n), .. items]` | 正式 | 把 `new List<T>(cap)` + 循环 Add 改成集合表达式一次写清；也能给 `HashSet<T>` 传 comparer | 无；注意 `with(...)` 必须是第一个元素 |
| **extension indexers** | 正式 | 给 `IReadOnlyList<T>`/`IEnumerable<T>` 之类的适配层加 `this[int]`，替换现有的 `ElementAt` 扩展方法 | 低；必须带命名接收参数 |
| **closed hierarchies**（`closed class`） | 正式 | 播放器状态机（`PlayerViewModel` 的状态枚举/子类）、播放源类型（CDN/PGC/UGC）这类"封闭集合"最合适：`switch` 变成穷尽、不需要 default 分支 | 低；`closed` 类隐式 `abstract`，不能与 `sealed`/`static`/显式 `abstract` 组合；派生不传递（中间层也要标 `closed`） |
| **union types**（`union Pet(Cat, Dog, Bird)`） | 正式 | 播放地址/接口响应这种"多形态返回值"可以用；配合 STJ 的 union 序列化（3.8）走源生成 | 中：① 需 net11 运行时支持（`UnionAttribute` 在 BCL 里）——**net9.0 的测试项目用不了**；② `TreatWarningsAsErrors` 下穷尽性分析若报 warning 会挡构建；③ 与 MVVM Toolkit 源生成器的组合需实测 |
| **非虚静态接口成员**（老运行时也能用） | 正式 | 接口里放纯静态 helper，不必给每个实现加成员；对 netstandard2.0 的 `BiliCopilot.UI.Models` / `UI.ResourceGenerator` 也生效 | 低；实例默认实现与 `static abstract` 仍需运行时支持 |
| **memory safety preview**（指针放宽、`safe` 关键字、`unsafe(expr)`） | **预览** | **不建议**进主干。仓库已有 `AllowUnsafe=True`、大量 WinRT/mpv 互操作，改安全模型等于把审计面全动一遍 | 高：要 `LangVersion=preview` + `updated-memory-safety-rules` 编译器特性，行为与生态工具链都还在演进 |

出处：[What's new in C# 15](https://learn.microsoft.com/dotnet/csharp/whats-new/csharp-15)、语言特性状态页 [dotnet/roslyn Language Feature Status](https://github.com/dotnet/roslyn/blob/main/docs/Language%20Feature%20Status.md)、union 设计讨论 [dotnet/csharplang union proposals](https://github.com/dotnet/csharplang/blob/main/meetings/working-groups/discriminated-unions/union-proposals-overview.md)。

**两个必须先说清的落地约束**：

1. **net9.0 的测试项目吃不到任何 net11 新 API**。`tests/BiliCopilot.Diagnostics.Tests`、`tests/BiliCopilot.CommentPoll.Tests`、`tests/BiliCopilot.Moment.Tests` 都是 `net9.0`。C# 15 语法在它们里面能编译（`LangVersion=preview`），但 `UnionAttribute`、`LeftJoin`、`BitArray.PopCount` 这些运行时类型/方法不在 net9 框架里，会编译失败。要写这类测试，得先把测试 TFM 提到 net11.

   更细的一条约束：`BiliCopilot.Diagnostics.Tests.csproj:16-21` 用 `<Compile Include>` 直接链接了 UI 项目里 6 个诊断源文件（`PlaybackStallProbe`/`PlaybackStallPolicy`/`PlaybackStallDumpBudget`/`PlaybackStallDump`/`PlaybackStallWatch`/`DanmakuRebuildTiming`）。这 6 个文件必须**同时能在 net9.0 下编译**，所以升级它们时不能用 net11-only API，除非同步把测试 TFM 提上去。
2. **`union` 与 XAML/WinUI 工具链**没有任何官方兼容声明。本仓库用 Reactor + 原生 XAML 混合，XAML 侧的 `x:Bind` 不认 union 类型，所以 union 只适合放在 ViewModel/Service 层，不要出现在 XAML 绑定的属性类型上。

---

## 5. 「不是 .NET 11 新增」清单（防误报）

这一节是为了让后续审计/升级任务不要把这些算成"11 带来的收益"。

| 能力 | 实际引入版本 | 证据 | 常见误报点 |
|---|---|---|---|
| Dynamic PGO（含 guarded devirtualization） | **.NET 8 起默认开启** | [.NET 8 runtime what's new](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-8/runtime)（"now enabled by default"） | 把 GDV/去虚拟化的收益记成 11 新增 |
| 分层编译 + Quick JIT | .NET Core 3.0 起默认开启 | [Compilation config](https://learn.microsoft.com/dotnet/core/runtime-config/compilation#tiered-compilation) | 同上 |
| ReadyToRun（crossgen2） | .NET Core 3.0 | [ReadyToRun 部署文档](https://learn.microsoft.com/dotnet/core/deploying/ready-to-run) | 11 只是"改了 R2R 目标 + 允许 runtime-async 内联" |
| NativeAOT | .NET 7 | [Native AOT 部署文档](https://learn.microsoft.com/dotnet/core/deploying/native-aot/) | 11 只是"接口派发走共享 helper" |
| `SearchValues<T>` | **.NET 8** | [SearchValues.Create API 页（moniker: net-8.0 起）](https://learn.microsoft.com/dotnet/api/system.buffers.searchvalues.create?view=net-11.0) | 被 3.5 那一类"搜索优化"带进来 |
| `FrozenDictionary`/`FrozenSet` | **.NET 8** | [.NET 8 runtime what's new](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-8/runtime) | — |
| `System.Text.Json` 源生成 | .NET 7 | [源生成文档](https://learn.microsoft.com/dotnet/standard/serialization/system-text-json/source-generation) | 11 新增的是 union/JSONL/closed 多态等**具体能力** |
| 正则源生成器（`[GeneratedRegex]`） | .NET 7 | [正则源生成](https://learn.microsoft.com/dotnet/standard/base-types/regular-expression-source-generators) | 11 新增的是 `AnyNewLine` + 引擎修复 |
| `LibraryImport` / 源生成 P/Invoke | .NET 7 | [P/Invoke 源生成](https://learn.microsoft.com/dotnet/standard/native-interop/pinvoke-source-generation) | — |
| `ValueTask` / 池化 `ValueTask` | .NET Core 2.1 / 5 | — | 与 runtime-async 混为一谈 |
| `System.Threading.Lock`（`lock` 语句新语义） | **.NET 9**（C# 13） | [.NET 9 / C# 13](https://learn.microsoft.com/dotnet/csharp/whats-new/csharp-13) | 11 只是把 condition variable 直接挂在 lock 上（性能） |
| `params ReadOnlySpan<T>` | .NET 9（C# 13） | 同上 | — |
| `Tensor`/`System.Numerics.Tensors` 包本身 | 早于 11 的独立包 | [libraries.md](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/libraries#systemnumericstensors-package-compatibility) 只写了"包更新会改形状对齐" | 把包更新算成框架新增 |
| `Zstandard` 压缩 | **确实是 11 新增**（别再倒过来误判成既有） | [Preview 1](https://github.com/dotnet/core/blob/main/release-notes/11.0/preview/preview1/libraries.md) 以包引入 → [Preview 3](https://github.com/dotnet/core/blob/main/release-notes/11.0/preview/preview3/libraries.md) 并入框架 | 反向误报 |

---

## 6. 给后续任务的可用清单

按"证据充分 + 与仓库现状对得上"排序，明确标注**本轮不改代码**，只交接给构建审计与源码扫描。

### P0（低风险、可立即验证）

1. **确认 runtime-async 真的生效**：`-t:Rebuild` 后对比程序集里是否还有编译器生成的状态机类型（`<...>d__` 命名），以及 `runtimeconfig.json` / IL 上方法是否带 `async` 标志。**判定依据**：`BiliCopilot.UI.csproj:23-26` 自己写了"Features 不是重编译输入"。
2. **确认测试项目 TFM 缺口**：三个测试项目是 `net9.0`，任何 net11 新 API 的验证都落不了地。这是后续"分批升级"必须先解决的阻塞项（属 t2/t4 范围）。
3. **硬件基线检查**：确认目标用户群机器满足 `x86-64-v2`；`PublishAot=True` 的发布产物尤其要看（AOT 目标跟随基线，见 2.7）。

### P1（中等收益、需要小改）

4. **HTTP 层三件套**：`ShouldEvictConnection`、`InitialHttp2MaxConcurrentStreams`、请求体压缩包装器——仓库里 `SocketsHttpHandler` 有十几处，是收益最集中的落点。
5. **JSON 走新契约**：`GetTypeInfo<T>()` 替手写 cast；需要"逐行输出"的地方（日志/事件导出）用 `SerializeAsyncEnumerable(topLevelValues: true)`；命名策略用 `PascalCase` + `[JsonNamingPolicy]` 替手工 options。
6. **进程管理换新 API**：Visor 启动/退出/收日志可以用 `StartAndForget` + `KillOnParentExit` + `ReadAllLinesAsync(ProcessOutputLine)`，替掉手写事件 + 字符串切分。
7. **C# 15 第一批**：labeled `break`/`continue`、collection expression arguments、extension indexers——纯语法糖，无运行时依赖。

### P2（需要评估、可能改设计）

8. **`union` + `closed` 建模播放状态/地址形态**：要配合 STJ 源生成，且不能出现在 XAML 绑定类型上。
9. **`ReadOnlyMemoryStream`/`StringStream`/`ReadOnlySequenceStream`** 替现有的"先 `ToArray()` 再包 `MemoryStream`"路径。
10. **`StringBuilder.MoveChunks`** 替大文本拼接后清空的模式。
11. **`Process`/`SafeFileHandle` 的管道能力**：Visor 的 IPC 若还在用命名管道原语，可评估 `CreateAnonymousPipe`。

### 不建议做

- 打开 `PublishReadyToRun` 只为吃 crossgen2 内联：会同时把 R2R 目标拉到 `x86-64-v3`（见 2.7），且当前 `PublishAot=True` 的配置需要整体重估。
- 引入 C# 15 memory safety preview（见第 4 节）。
- 为"提速"关掉分层编译/Dynamic PGO。

---

## 7. 参考链接总表

| 用途 | 链接 |
|---|---|
| .NET 11 GA 概览 | https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/ |
| .NET 11 运行时 | https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/runtime |
| .NET 11 库 | https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/libraries |
| .NET 11 SDK | https://learn.microsoft.com/dotnet/core/whats-new/dotnet-11/sdk |
| C# 15 | https://learn.microsoft.com/dotnet/csharp/whats-new/csharp-15 |
| 编译配置（分层编译 / PGO / R2R） | https://learn.microsoft.com/dotnet/core/runtime-config/compilation |
| GC 配置 | https://learn.microsoft.com/dotnet/core/runtime-config/garbage-collector |
| .NET 11 破坏性变更总表 | https://learn.microsoft.com/dotnet/core/compatibility/11 |
| 最低硬件要求变更 | https://learn.microsoft.com/dotnet/core/compatibility/jit/11/minimum-hardware-requirements |
| 十进制/BigInteger 转换变更 | https://learn.microsoft.com/dotnet/core/compatibility/core-libraries/11/decimal-biginteger-floating-point-conversions |
| Runtime async 规范草案（ECMA-335 修订） | https://github.com/dotnet/runtime/blob/main/docs/design/specs/runtime-async.md |
| Runtime async 编译器设计（Roslyn） | https://github.com/dotnet/roslyn/blob/main/docs/compilers/CSharp/Runtime%20Async%20Design.md |
| 分层编译设计文档 | https://github.com/dotnet/runtime/blob/main/docs/design/features/tiered-compilation.md |
| Dynamic PGO 插桩分层 | https://github.com/dotnet/runtime/blob/main/docs/design/features/DynamicPgo-InstrumentedTiers.md |
| 性能总览（Stephen Toub） | https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-11/ |
| 发布公告（STS / 两年支持） | https://devblogs.microsoft.com/dotnet/announcing-dotnet-11/ |
| 11.0.0 发布说明 | https://github.com/dotnet/core/blob/main/release-notes/11.0/11.0.0/11.0.0.md |
| 11.0 Preview 1（Zstandard 引入） | https://github.com/dotnet/core/blob/main/release-notes/11.0/preview/preview1/libraries.md |
| 11.0 Preview 3（Zstandard 并入框架） | https://github.com/dotnet/core/blob/main/release-notes/11.0/preview/preview3/libraries.md |
| SearchValues.Create API（适用版本） | https://learn.microsoft.com/dotnet/api/system.buffers.searchvalues.create?view=net-11.0 |
| STJ union 序列化 | https://learn.microsoft.com/dotnet/standard/serialization/system-text-json/union-types |
| STJ closed 体系多态 | https://learn.microsoft.com/dotnet/standard/serialization/system-text-json/polymorphism#infer-polymorphism-from-a-closed-hierarchy |
| Roslyn 语言特性状态 | https://github.com/dotnet/roslyn/blob/main/docs/Language%20Feature%20Status.md |

---

## 8. 未决项（本文不当作事实的部分）

1. **runtime-async 的"预览/正式"措辞冲突**：GA 文档把它写进"what's new"且不再要求 `EnablePreviewFeatures`，但必须显式开 `<Features>`，且 ECMA-335 修订仍标 draft；第三方（InfoQ）称其 "remains a preview"。本文按工程语义记为"预览（opt-in）"。**若要对外表述，建议统一用"opt-in 功能（非默认）"而不是"预览特性"。**
2. **Dynamic PGO 在 .NET 11 的默认值**：明确的"默认开启"表述只出现在 .NET 8 文档里；.NET 11 的 runtime.md 与 compilation.md 未重复声明。本文按"沿用 .NET 8 的默认开启"处理，属**合理推断**而非直接引文。
3. **`ZstandardStream` 的"预览框架"标记**：API 目录页面标了 "The API is contained in a preview framework"，与 GA 文档把它当正式特性描述不一致；未找到官方解释，故本文只标"正式（11 新增）"并在此备注。
4. **C# 15 `union` 与 MVVM Toolkit / WinUI 工具链的兼容性**：没有任何官方兼容矩阵，需实测，本文只给风险提示。
5. **本仓库 `PublishAot=True` 是否真被走到**：`BiliCopilot.UI.csproj:1499-1500` 同时写 `PublishReadyToRun=False` 与 `PublishAot=True`，注释里又说"AOT 那条路本轮不测"。这条属构建配置审计（t2）的判定范围，本文不越界结论。
