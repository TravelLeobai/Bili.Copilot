# .NET 11 源码扫描清单（统计口径与扫描方法）

> 分支 `exp/net11-optimization`，报告基线 HEAD `77ed06a`（四份报告在 `7a81ab6` 新增，`64dd96f` / `03b8df4` 两次补记），未 push。扫描日期 2026-11-24，语义数据取于 19:40–19:50（NET11_SOURCE_SCAN_MANIFEST.md §1）；此后新增的提交只动文档，不参与编译。
> **逐文件清单不在这里，见 `docs/NET11_SOURCE_SCAN_MANIFEST.md`**（539 行，含 1268 条逐文件记录、246 个 XAML 清点、8 条已知局限）。
> 本文只回答一个问题：**清单在哪、按什么口径统计**。两份文件重名，正文只保留口径与方法，清单正文以 `docs/` 版为准；若两处数字冲突，按 `docs/` 版。
> 热点分级与调用链见 `docs/NET11_HOTSPOTS.md`；构建配置见 `NET11_OPTIMIZATION_AUDIT.md`。

## 0. 结论

1. 参与编译的 `.cs` 共 **1268** 个，来自 **8 个编译单元**（5 个 src 工程 + 3 个测试工程），取的是 MSBuild 真实 csc 命令行里的源文件列表，不是目录遍历（NET11_SOURCE_SCAN_MANIFEST.md §1）。
2. 其中仓库内手写源码 **769** 个（85,166 行）、仓库内生成代码 **494** 个（单列不修改）、包内/外部源码 **5** 个（不属于本仓库）。
3. 20 类语义检查共 **735 条**命中，落在 **189** 个手写文件上；命中在生成代码里的为 **0**（生成代码只看不改）。
4. XAML 标记文件 **246** 个，只清点不做语义分析（原因见 §4）。
5. 剔除 **24** 条测试工程 `MISSING_CT` 误报（测试方法本来就不需要取消令牌），清单与统计均已剔除。
6. 扫描方法是"用真实 csc 参数重建 Roslyn `CSharpCompilation`"，不是正则：正则无法判断"这是 Task 上的 `.Result`"还是"这是 `ToList`"，也无法区分生成类型与手写类型。

## 1. 统计口径

| 口径 | 数量 | 说明 |
|---|---|---|
| 参与编译的 `.cs`（8 个编译单元） | 1268 | 从 `-v:detailed` 日志抓 csc 参数，已剔除混入的 `.dll` |
| 其中仓库内手写源码 | 769 | 排除 `obj/` 下 XAML/源生成产物；含 8 个被测试工程与 UI 双重编译的链接文件（同一文件出现两次，是刻意的） |
| 其中仓库内生成代码 | 494 | `*.g.cs` / `*.g.i.cs` / `XamlTypeInfo.g.cs` / AssemblyInfo 等 |
| 其中包内/外部 `.cs` | 5 | 编译单元里由 NuGet 包带入的源码（如 Microsoft.NET.Test.Sdk 的 `Program.cs`） |
| 手写源码行数 | 85,166 | Roslyn 语法树行数 |
| 有命中的文件 | 189 | 命中 = 至少一条语义检查 |
| 语义命中总数 | 735 | 20 类检查，见 §2 |
| 其中落在生成代码里的命中 | 0 | 全部排除 |
| XAML 标记文件 | 246 | 单列清点，未做语义分析 |
| 调用点索引 | 6,088 | 项目内调用 + 关注的外部 API，用于热点文档的调用链 |
| 已剔除误报 | 24 | 测试工程的 `MISSING_CT` |

## 2. 按工程汇总

| 编译单元 | 手写文件 | 行数 | 有命中文件 | 命中数 | 需改 | 需评估 |
|---|---|---|---|---|---|---|
| BiliCopilot.UI | 696 | 78,415 | 181 | 698 | 82 | 78 |
| BiliCopilot.UI.Models | 42 | 1,563 | 0 | 0 | 0 | 0 |
| BiliCopilot.UI.ResourceGenerator | 1 | 93 | 0 | 0 | 0 | 0 |
| BiliCopilot.Visor | 5 | 615 | 2 | 9 | 1 | 0 |
| BiliCopilot.Visor.Models | 1 | 85 | 0 | 0 | 0 | 0 |
| BiliCopilot.CommentPoll.Tests | 12 | 2,279 | 4 | 25 | 1 | 2 |
| BiliCopilot.Diagnostics.Tests | 9 | 1,911 | 2 | 3 | 1 | 1 |
| BiliCopilot.Moment.Tests | 3 | 205 | 0 | 0 | 0 | 0 |
| **合计** | **769** | **85,166** | **189** | **735** | — | — |

出处：NET11_SOURCE_SCAN_MANIFEST.md §4。三个测试工程在扫描时为 `net9.0`，`5fe32e5` 之后都是 `net11.0`——"同一文件被 UI 与测试工程各编一次、两边 TFM 不同"的旧约束**当前已不存在**，链接文件可以用 net11 专有 API（同文 §8 第 2 条）。

## 3. 20 类检查的命中分布

| 检查 | 命中 | 涉及文件 | 判定级别 |
|---|---|---|---|
| `CONFIGUREAWAIT` | 199 | 37 | P3 |
| `LINQ_MATERIALIZE` | 117 | 63 | P3 |
| `MISSING_CT` | 90 | 50 | P2 |
| `ASYNC_VOID` | 79 | 55 | P1 |
| `LINQ_IN_LOOP` | 59 | 37 | P2 |
| `HTTP_BUFFERED` | 53 | 24 | P2 |
| `ASYNC_LAMBDA_VOID` | 29 | 24 | P2 |
| `NEW_HTTPCLIENT` | 28 | 22 | P1（其中静态工厂 16 条不计） |
| `TASK_RUN` | 20 | 10 | P2 |
| `IMAGE_ALLOC` | 16 | 11 | P2 |
| `JSON_REFLECTION` | 11 | 6 | P1（其中 7 条误报，真反射 4 条） |
| `LOOP_DELAY` | 9 | 9 | P3 |
| `SYNC_OVER_ASYNC` | 8 | 3 | P0 |
| `CLOSURE_IN_LOOP` | 4 | 4 | P2 |
| `BLOCKING_SLEEP` | 4 | 4 | P1 |
| `IMAGE_IO` | 3 | 3 | P2 |
| `ASYNC_NO_AWAIT` | 2 | 2 | P2 |
| `STRING_CONCAT_LOOP` | 2 | 1 | P1 |
| `JSONDOC_NO_USING` | 1 | 1 | P2 |
| `DISCARDED_TASK` | 1 | 1 | P1 |

出处：NET11_SOURCE_SCAN_MANIFEST.md §5。级别只是"该检查项最严重能到的位置"，逐条判定与调用链在 `docs/NET11_HOTSPOTS.md`；两者合计口径（P0 4 / P1 107 / P2 273 / P3 325 / 不计 26 = 735）以热点文档 §0 为准。

`JSON_REFLECTION` 的 11 条里有 7 条误报，原因是重建编译单元没有跑 STJ 源生成器，`GlobalSerializeContext.Default` 在语义模型里成了错误类型；逐点核对后真反射是 4 条（`Services/AppStateStore.cs:107`、`:136`、`Services/LiveEmoteStore.cs:142`、`:165`）。这类"源生成器缺位造成假阳性"对 STJ / MVVM Toolkit / Reactor / CsWin32 / ComputeSharp 都成立，最终判定必须逐点读代码，语义扫描只用于缩小范围（同文 §5 注、NET11_HOTSPOTS.md §9）。

## 4. 扫描方法

1. **重建真实编译单元**：从 `dotnet build -v:detailed` 日志里抓每个程序集真实的 csc 命令行（`/reference:` 338 条、`/define:`、`/langversion:`、源文件清单），用 Roslyn 按同一份参数重建 `CSharpCompilation`。语义符号与编译器看到的一致，不靠文本猜测。
2. **20 类语义检查**：每条命中都要求 `SemanticModel` 能解析出符号/类型（"这是 Task 上的 `.Result`"、"这是 `System.Linq.Enumerable.ToList`"、"这个 lambda 的 ConvertedType 委托返回 void"），无法解析的一律不报。
3. **编译器与分析器诊断**：另跑两轮真实构建——(a) `NoWarn` 清空（只留 XAML 必需的 `WMC1510`），拿到 1982×CS1591 / 884×MVVMTK0045 / 208×IDE0005 / 10×IL2026；(b) 命令行 `-p:EnableNETAnalyzers=true` 押过 NuGet 包里的 `DisableNETAnalyzersForNuGetPackage.props`，拿到 342 条 CA 诊断（CA1873 300 条为主）。这一步同时暴露了分析器治理空转：props 里三项求值结果是 `EnableNETAnalyzers=false`，CA 规则一条不跑。
4. **调用索引**：同一遍扫描记录 6,088 条调用点（`Project/Callee/File/Line/Member/Kind`），供热点文档回答"谁调用了它"。

XAML 只清点不语义分析的两个原因：仓库长期抑制 `WMC1510`（XAML 两遍编译必需）；XAML 编译器的类型解析发生在 C# 编译之前的中间程序集上，单独对 `.xaml` 做静态分析拿不到 `x:Bind` 的真实类型信息。**code-behind 侧（`.xaml.cs`）已全部纳入 §6 的语义扫描**（同文 §7 末段）。

复现材料在仓库外 `_tmp/scan/`：`extract-specs.ps1`（日志 → 编译规格）、`RoslynScan.csproj` / `Program.cs`（分析器本体，直连 SDK 自带 `Microsoft.CodeAnalysis*.dll`）、报告数据 `out2/{findings,callsites,filestats,diag}.jsonl`（同文 §2 末段）。

## 5. 已知局限

1. **只在 x64 / Debug 一份编译单元上扫描**，`/define:` 是 `TRACE;LOCAL_DEV;ARCH_X64;...;DEBUG`；Release 下 `#if DEBUG` 的 6 个诊断源文件不会被扫到，ARM64 分支同理。
2. **测试工程 TFM 在扫描期间变化**（`net9.0` → `net11.0`），旧的双 TFM 约束已消失（§2 末段）。
3. **nullable 上下文按 Disable 重建**，只为让语义解析不受 `#nullable` 影响；本文不查可空性，判定不受影响。
4. **检查项是"形"不是"罪"**：`CONFIGUREAWAIT` 199 处多数正确（库层/后台层用 `ConfigureAwait(false)` 正是该做的），`LINQ_MATERIALIZE` 117 处多数无害。
5. **`MISSING_CT` 只查"public/internal 异步方法无 `CancellationToken` 形参且有 await"**，不判断该调用是否真的可取消；保留的 90 条需人工分级。
6. **不覆盖**：IL/反射调用（`MethodInfo.Invoke`）、`dynamic`、COM 互操作边界内的性能特征、`.xaml` 标记本身的热路径（如 `ItemsRepeater` 模板复杂度）。
7. **双宿主编译**：三个测试工程用 `<Compile Include>` 链接了 UI 的 8 个源文件，同一文件在清单里出现两次（分别挂在测试工程与 UI 下），是刻意的，不是重复计数。

出处：NET11_SOURCE_SCAN_MANIFEST.md §8。
