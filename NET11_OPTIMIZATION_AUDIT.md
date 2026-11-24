# .NET 11 优化审计结论（Bili.Copilot / bili-copilot-v2）

> 分支 `exp/net11-optimization`，HEAD `dfdf0bf`，未 push。审计对象 2026-11-24 的 `bili-copilot-v2`；本机 SDK `11.0.100`（另有 9.0.320 / 10.0.403），csc `5.1200.26.53018`（NET11_BUILD_AUDIT.md 文首、§3）。
> 本文只回答一个问题：**现状是什么**。改动记录见 `NET11_MODERNIZATION_REPORT.md`，实测数据见 `NET11_PERFORMANCE_VALIDATION.md`，逐文件清单见 `docs/NET11_SOURCE_SCAN_MANIFEST.md`。
> 每个数字旁边带出处（文档 §、提交 hash 或文件行号），转述的判定与原文一致；原文自相矛盾处已在 §1 注出。

## 0. 结论

1. **runtime-async 生效，但覆盖面只有 3 个编译单元声明、2 个真的有 async 方法**：`/features:runtime-async=on` 只落在 `BiliCopilot.UI` 与 `BiliCopilot.Visor`；`Visor.Models` 由 `f7fa0e3` 补上声明，但该工程当时 0 个 async 方法，属零收益补洞。UI 的 async 方法状态机 **585 → 79**（−86.5%），dll **5,057,536 → 4,489,728 B**（−11.2%）；Visor **3 → 0**（NET11_BUILD_AUDIT.md §3、§4；NET11_P0_FIXES.md 批 3）。
2. **残留的 79 个状态机就是 79 个 `async void`**，另有 7 个 `yield` 迭代器，合计 86 个 `d__` 类型；没有"返回 Task 却回退"的案例（NET11_BUILD_AUDIT.md §4.1）。`async void` 不在 runtime-async 支持的四种返回类型内，属形态限制而非缺陷（NET11_RESEARCH.md §1.3）。
3. **`PublishAot=True` 曾是 UI 工程的常开属性**（原 `BiliCopilot.UI.csproj:1500`），在**普通 build** 阶段就把 `PublishTrimmed` 求值成 `true`，并把 **23 条**裁剪导向开关写进 runtimeconfig；`d781292` 已拆，Release **28 → 5**、Debug **27 → 4**（NET11_P0_FIXES.md 批 2；NET11_BUILD_AUDIT.md §5）。
4. **Release 曾编不过**：`Services/Diagnostics/DiagnosticsReport.cs:14` 的 `<see cref="PlaybackStallDump"/>` 指向包在 `#if DEBUG` 里的类型，CS1574 被 `Directory.Build.props:8` 的 `TreatWarningsAsErrors` 升成错误；`4e3566b` 已修，EXIT=1 → **0 警告 0 错误**（NET11_BUILD_AUDIT.md §6；NET11_P0_FIXES.md 批 1）。
5. **分析器治理空转**：`Directory.Build.props` 的 `EnableNETAnalyzers=true` / `AnalysisMode=AllEnabledByDefault` / `AnalysisLevel=latest` 三项求值结果为 `EnableNETAnalyzers=false`（被 NuGet 包的 `DisableNETAnalyzersForNuGetPackage.props` 压掉），CA 规则一条不跑；命令行强推后拿到 **342 条** CA 诊断（NET11_SOURCE_SCAN_MANIFEST.md §2 第 3 条；提交 `a0d12a6`）。
6. **测试 TFM 缺口已补**：三个测试工程原钉 `net9.0`，`5fe32e5` 升到 `net11.0`，CommentPoll **84/84**、Diagnostics **23/23**；`Diagnostics` 的 18 个用例被 `#if DEBUG` 包住，`-c Release` 只剩 **5/23**（NET11_BUILD_AUDIT.md §8；NET11_P0_FIXES.md 批 4）。
7. **25 条构建配置问题里 3 条有结论**：`4e3566b` 修 Release 红灯、`d781292` 拆 PublishAot、publish 通道降级为"t2 时的历史现象、本轮未复现"；其余 22 条未动（NET11_BUILD_AUDIT.md §12）。
8. **依赖跨代漂移是净风险**：`Microsoft.UI.Reactor 0.1.0-preview.17` 只有 `net10.0-windows10.0.22621` 单一 TFM、nuspec 依赖 WindowsAppSDK 2.2.x，而工程钉 2.6.5；`H.NotifyIcon.WinUI 2.3.2`、`CommunityToolkit.WinUI.* 8.2.250402` 按 1.6.x 编；`Microsoft.Windows.CsWinRT` 钉 2.2.0、实际解析 **2.3.0-prerelease.251015.2**（NET11_BUILD_AUDIT.md §14）。
9. **不建议做的四项**（UI 进程 NativeAOT、激进裁剪、Server GC、R2R）在 §5 逐条给依据，判定都是"维持现状"。

## 1. 审计坐标与口径

| 项 | 值 | 出处 |
|---|---|---|
| 仓库 / 分支 | `bili-copilot-v2` / `exp/net11-optimization` | NET11_BUILD_AUDIT.md 文首 |
| 本轮提交范围 | `2f10c66..dfdf0bf` 共 24 个提交（8 个改代码或工程、16 个文档） | `git log --oneline` |
| 编译单元 | 5 个 src 工程 + 3 个测试工程 = 8 个 | NET11_SOURCE_SCAN_MANIFEST.md §1 |
| SDK / OS / CPU | `11.0.100` / Windows NT 10.0.29712.0 / Ryzen 7 5800H 16 核 31.4 GB | NET11_VERIFY_RAW.md §0 |
| 语义扫描快照 | 2026-11-24 19:40–19:50，期间 4 个只改构建属性与 1 行注释的提交 | NET11_SOURCE_SCAN_MANIFEST.md §1 |

三份原文之间有两处口径差异，本文按下列方式处理：

- **状态机计数**：`86` 是 `d__` 类型数（7 迭代器 + 79 async 状态机，NET11_BUILD_AUDIT.md §4）；`108` 是 `AsyncStateMachineAttribute` 方法数（NET11_VERIFY_RAW.md §2.2）。两者都在原始记录里可查，差 22 个的原因属推断（§2.3 原文标注"推断（未 A/B 验证）"）。本文引用 86 时说明是类型口径。
- **静态 HttpClient 数量**：语义扫描记 `NEW_HTTPCLIENT` 28 条、其中静态工厂 16 条（NET11_HOTSPOTS.md §9）；按字段口径实测是 22 个 `static readonly HttpClient`（NET11_P1_NETWORK_IMAGE.md §A）。两个数不是同一件事。

## 2. 构建配置现状

### 2.1 runtime-async 覆盖面

| 编译单元 | TFM | 声明 `Features` | csc 是否带 `/features:runtime-async=on` | async 形态 |
|---|---|---|---|---|
| BiliCopilot.UI | `net11.0-windows10.0.22621.0` | 是（csproj:27） | 是（XAML 两遍编译各一次） | 86 个 `d__`（79 async + 7 迭代器） |
| BiliCopilot.Visor | `net11.0` | 是（csproj:8） | 是 | 3 → 0 |
| BiliCopilot.Visor.Models | `net11.0` | 是（csproj:16，`f7fa0e3` 补） | 是 | 工程内 0 个 async |
| BiliCopilot.UI.Models | `netstandard2.0` | 否 | 否 | 0；老 corlib 无 `AsyncHelpers`，结构上不可能 |
| BiliCopilot.UI.ResourceGenerator | `netstandard2.0` | 否 | 否 | 0（源生成器宿主约束） |
| 三个测试工程 | `net11.0`（`5fe32e5` 后） | 否 | 否 | 测试代码，无状态机收益 |

出处：NET11_BUILD_AUDIT.md §2.2、§3、§8；NET11_RESEARCH.md §1.9；NET11_P0_FIXES.md 批 3。

三条容易踩空的事实：

- `Features` **不随 `ProjectReference` 传播**，TFM 是 `net11.0` 也不自动带上（NET11_RESEARCH.md §1.9）。
- `~/.dotnet` 文档给的出口 `UseRuntimeAsync=false` **在本机 SDK 11.0.100 无效**：实测仍是 86 个状态机，SDK 的 props/targets 里搜不到这个属性；唯一有效关断是 `-p:Features=` + `-t:Rebuild`（NET11_BUILD_AUDIT.md §7、NET11_RESEARCH.md §1.2 NOTE）。
- `Features` 不是 MSBuild 的重编译输入，改完普通 build 是空操作（csproj:23-26 注释；NET11_BUILD_AUDIT.md §7 实测复现）。

### 2.2 `PublishAot` 的语义（已退出）

| 状态 | Release 开关数 | Debug 开关数 | `PublishTrimmed` 求值 | `SelfContained` |
|---|---|---|---|---|
| `<PublishAot>True</PublishAot>` 在 csproj（审计时） | 28 | 27 | true | true |
| 拆掉（`d781292` 之后，当前 HEAD） | 5 | 4 | 空 | 仍 true |

出处：NET11_P0_FIXES.md 批 2（复算命令用 `-getItem:RuntimeHostConfigurationOption`，前置条件 `<PublishAot>` 必须在 restore 之前在位）；NET11_BUILD_AUDIT.md §5。

- 差异 23 条全是裁剪导向开关，含 `RuntimeFeature.IsDynamicCodeSupported=false`、`JsonSerializer.IsReflectionEnabledByDefault=false`、`EventSource.IsSupported=false`、`Serilog.Capturing.IsStructureValueSupported=false`、`Http3Support=false`（NET11_P0_FIXES.md 批 2）。
- Release 比 Debug 多出的那条是 SDK 对非 Debug 配置默认的 `System.Reflection.Metadata.MetadataUpdater.IsSupported=false`（`Microsoft.NET.Sdk.targets:164`），与 AOT 无关（NET11_P0_FIXES.md 批 2）。
- `Reactor.DevtoolsSupport` **只随 Configuration 走**（Debug=true / Release=false），与 `PublishAot` 无关——原文"从被误关的 false 回到 true"已更正（NET11_P0_FIXES.md 批 2）。
- `SelfContained=true` 与 AOT 无关，来自 WinAppSDK/MSIX 单工程打包链路（NET11_BUILD_AUDIT.md §2.2 实测 `-p:PublishAot=false` 后仍为 true）。
- 语义错配的实质：程序集并没有真被裁剪（`PublishTrimmed` 只在 publish 生效），但运行时被告知"不要做动态代码/反射"（NET11_BUILD_AUDIT.md §5）。
- AOT **从未被执行**：CI 只跑 `msbuild + GenerateAppxPackageOnBuild`，6 个 workflow 里 `pubxml|PublishProfile|dotnet publish|-t:Publish` 命中数 **0**（NET11_BUILD_AUDIT.md §9）。
- 若要重开：`<PublishAot>true</PublishAot>` 必须写进 csproj，不能用命令行 `-p:PublishAot=true`——全局属性会传染给 netstandard2.0 的 `UI.Models`/`ResourceGenerator`，直接 `NETSDK1207` 打断整解（NET11_P0_FIXES.md 批 2）。

### 2.3 分析器与警告治理

| 项 | 现状 | 出处 |
|---|---|---|
| `EnableNETAnalyzers` | props 写 `true`，求值 `false`（NuGet 包 props 压掉） | NET11_SOURCE_SCAN_MANIFEST.md §2 第 3 条 |
| 强推后的 CA 诊断 | 342 条（CA1873 300 条为主） | 同上 |
| `NoWarn` 清空后的量级 | 1982×CS1591 / 884×MVVMTK0045 / 208×IDE0005 / 10×IL2026 | 同上 |
| UI 工程 `NoWarn` | 39 项，其中 28 项 `IL*`、3 项 `REACTOR_*` | NET11_BUILD_AUDIT.md §3 |
| `TrimmerSingleWarn=false` + 28 个 `IL*` 抑制 | "每条都报、然后全部消掉" | NET11_BUILD_AUDIT.md §12-24 |
| `CS1998` 抑制项 | **死项**：.NET 11 编译器不再产生该警告 | NET11_BUILD_AUDIT.md §11-B（探针 0 警告）、NET11_RESEARCH.md §1.8 |

### 2.4 测试工程

| 项 | 现状 | 出处 |
|---|---|---|
| TFM | 三个工程 `net11.0`（`5fe32e5`） | 提交 `5fe32e5` |
| 测试结果 | CommentPoll 84/84、Diagnostics 23/23 | NET11_P0_FIXES.md 批 4 |
| `-c Release` | Diagnostics **5/23**（18 个用例被 `#if DEBUG` 剥掉） | NET11_BUILD_AUDIT.md §8 |
| 测试栈版本 | xunit 2.7.0 / Test.Sdk 17.10.0 / runner 2.5.7，`5fe32e5` 未动 | NET11_BUILD_AUDIT.md §8 |
| sln 覆盖 | 三个测试工程都不在 `src/Bili.Copilot.sln` 里，也不继承 `src/Directory.Build.props` | NET11_BUILD_AUDIT.md §1 |
| 被二次编译的文件 | 8 个（`<Compile Include>` 链接进测试工程），同一文件出现在清单两处 | NET11_SOURCE_SCAN_MANIFEST.md §1、§8 |

### 2.5 CI / SDK 版本链

| 项 | 现状 | 风险 | 出处 |
|---|---|---|---|
| `setup-env/action.yml:61` | `dotnet-version: 9.0.x` | 干净 runner 上 `NETSDK1045` | NET11_BUILD_AUDIT.md §10 |
| `global.json` | 不存在 | SDK 选择 = "最新安装版" | 同上 |
| `nuget.config:9` | 残留 `dotnet9` 源 | 噪声与解析成本 | 同上 |
| `packageSourceMapping` | 无（5 个源全局可见） | 靠 `NoWarn;NU1507` 擦屁股 | 同上 |
| CI 构建 Visor | 不构建；UI 只 `ProjectReference` `Visor.Models` | Visor.exe 是签进仓库的二进制 | 同上 |
| `dist/` 里的 Visor | 3,308,032 B（11-03），比仓库里的 3,399,680 B（11-24）旧一代 | 用 dist 出包会带旧 visor | 同上 |

### 2.6 隐式与死配置

| 项 | 现象 | 出处 |
|---|---|---|
| `Configurations` | 只有 `Debug;Release`，但 csproj:444-452 有 `'Publish\|x64'` 条件块；`-c Publish` 能进，`DefineConstants` 变 `PUBLISH` 且丢 `LOCAL_DEV` | NET11_BUILD_AUDIT.md §2.1 |
| `LOCAL_DEV` / `ARCH_*` | 全仓库 `.cs/.xaml` 零引用；`LOCAL_DEV` 在 Debug 与 Release 都定义 | NET11_BUILD_AUDIT.md §12-10 |
| `rd.xml` | 只对 NativeAOT 生效；ILLink 不认 `RdXmlFile`，对 UI 工程完全无效 | NET11_BUILD_AUDIT.md §12-9 |
| `OutDir`/`IntDir` 重定向 | 未用 `BaseOutputPath`/`BaseIntermediateOutputPath`，留有两套中间产物残留 | NET11_BUILD_AUDIT.md §12-22 |
| sln 平台映射 | Visor 映射 `Any CPU`，`-p:Platform=x64` 时落到 `_build/AnyCPU/…` | NET11_BUILD_AUDIT.md §12-23 |
| 未用包钉版 | 136 条 `PackageVersion` / 70 个真实 `PackageReference`，其中约 59 条无人引用 | NET11_BUILD_AUDIT.md §12-21 |
| `Nullable` | 在 `Directory.Build.props:5` 与 `:14` 各声明一次 | NET11_BUILD_AUDIT.md §12-18 |
| `EditorConfigFilePath` | 非标准属性，`.editorconfig` 本就会被自动读取 | NET11_BUILD_AUDIT.md §12-19 |
| `Features` 前导分号 | 产生一个空 feature 槽 | NET11_BUILD_AUDIT.md §12-20 |

## 3. 依赖跨代漂移风险

153 个直接+传递包，没有一个声明 `net11` 资产，全部走"高版本 TFM 向下兼容"回落（NET11_BUILD_AUDIT.md §14）。被支持的机制能保证"能跑"，但 runtime-async、裁剪、AOT、CsWinRT 投影这些交叉项，包作者都没测过——验证责任转移到本仓库。

| 包 | 钉版 | 实际解析 | 包内 TFM / 依赖 | 风险判定 | 出处 |
|---|---|---|---|---|---|
| Microsoft.UI.Reactor | 0.1.0-preview.17 | 同 | `net10.0-windows10.0.22621` 单一 TFM；nuspec 依赖 WindowsAppSDK.WinUI 2.2.1 / Runtime 2.2.0 | 工程钉 2.6.5，属**未验证组合** | §14 |
| H.NotifyIcon.WinUI | 2.3.2 | 同 | `net8.0/net9.0-windows10.0.17763`；nuspec 依赖 WindowsAppSDK **1.6.250108002** | 跨大版本（1.6 → 2.6.5） | §14 |
| CommunityToolkit.WinUI.Controls.* | 8.2.250402 | 同 | 最高 `net9.0-windows10.0.19041` | 同样按 1.6.x 编 | §14 |
| Microsoft.Windows.CsWinRT | 2.2.0 | **2.3.0-prerelease.251015.2** | `net6.0`,`net8.0` + analyzers/build | 钉版失效，实际是预发布，WinRT 投影行为随它变 | §12-8、§14 |
| Microsoft.WindowsAppSDK | 2.6.5 | 同 | 拆包，WinUI 包 lib 是 `net6.0-windows10.0.17763.0` | 无 net11 资产，靠回落 | §14 |
| Microsoft.Graphics.Win2D | 1.4.0 | 同 | `net6.0-windows10.0.19041.0` | 要求 min 19041，本工程刚好卡边 | §14 |
| ComputeSharp.D2D1.WinUI | 3.2.0 | 同 | `net8.0-windows10.0.22621` | 着色器源生成器按 net8 编，AOT/裁剪下 COM 互操作需实测 | §14 |
| Microsoft.Windows.SDK.BuildTools.MSIX | 未钉（Reactor 传递） | 1.7.260930100 | build 资产 | 未钉版、由 Reactor 带进来 | §14 |
| Serilog / Serilog.Extensions.Logging | 4.3.1-dev-02385 / 9.0.3-dev-02320 | 同 | — | 开发版包，干扰归因 | §14 |

## 4. 分级问题清单

两级清单分开看：构建配置（NET11_BUILD_AUDIT.md §12，25 条）与源码热点（NET11_HOTSPOTS.md，21 组 / 735 条命中）。两套分级口径不同，不合并计数。

### 4.1 构建配置（25 条）

| 级别 | # | 项 | 判定依据 | 当前状态 |
|---|---|---|---|---|
| P0 | 1 | Release 命中 CS1574 → 构建失败 | `DiagnosticsReport.cs:14` + `Directory.Build.props:8`；§6 实测 EXIT=1 | **已修**（`4e3566b`，EXIT=0） |
| P0 | 2 | `PublishAot=True` 常开 | 普通 build 写 28 条裁剪开关；§5 | **已修**（`d781292`，28/27 → 5/4） |
| P0 | 3 | publish 通道损坏（CS0246 `StringNames`） | §9 更正框：带 restore 的 publish 退出码 0、619 个文件、无 CS0246 | **降级**（t2 历史现象，本轮未复现） |
| P0 | 4 | CI 只装 .NET 9 | `setup-env/action.yml:61`；§10 | 未动 |
| P1 | 5 | `NoWarn;CS1998` 是死抑制 | §11-B 探针 0 警告；Roslyn 已移除 | 未动 |
| P1 | 6 | `UseRuntimeAsync` 不存在 | §7：`-p:UseRuntimeAsync=false` 仍是 86 个状态机 | 未动 |
| P1 | 7 | runtime-async 覆盖不全 | §0-1；`UI.Models` 是 netstandard2.0 | 部分：`Visor.Models` 已补（`f7fa0e3`），`UI.Models` 评估后不抬（CS8765） |
| P1 | 8 | CsWinRT 2.2.0 是死钉 | 实际 2.3.0-prerelease.251015.2 | 未动 |
| P1 | 9 | `rd.xml` 只对 NativeAOT 生效 | §12-9 | 未动 |
| P1 | 10 | `LOCAL_DEV` / `ARCH_*` 死符号 | 全仓零引用；Debug/Release 都定义 | 未动 |
| P1 | 11 | `Configurations` 不含 `Publish` 但有条件块 | §2.1 实测 `-c Publish` 可进 | 未动 |
| P1 | 12 | 三个测试工程在 sln 外 | §1；不继承 props | 未动（TFM 已升） |
| P1 | 13 | 用例被 `#if DEBUG` 包住 | §8：Release 5/23 | 未动 |
| P1 | 14 | CI 不构建 Visor，但 UI 依赖其二进制 | §10 | 未动 |
| P1 | 15 | `dist/` 里的 Visor 落后一代 | 3,308,032 B vs 3,399,680 B | 未动 |
| P1 | 16 | 无 `global.json` | §10 | 未动 |
| P1 | 17 | `nuget.config` 残留 `dotnet9` + 无 `packageSourceMapping` | §10 | 未动 |
| P2 | 18–25 | `Nullable` 重复 / `EditorConfigFilePath` / `Features` 前导分号 / 66 条未用钉版 / `OutDir` 重定向 / 平台映射 / `TrimmerSingleWarn=false` / 分析器配置极值 | §12 P2 表逐条 | 未动 |

> 25 条 = P0 4 + P1 13 + P2 8。已修 2、降级 1、其余 22 条未动。

### 4.2 源码热点（21 组 / 735 条）

| 级别 | 组数 | 命中 | 构成 | 本轮处置 | 出处 |
|---|---|---|---|---|---|
| P0 | 2 | 4 | `JSON_REFLECTION` 真反射 4 | P0-1 位置回调链已改（`69785b5`）；P0-2 只解决构建侧，代码侧 4 处未动 | NET11_HOTSPOTS.md §0、P0-1/P0-2 |
| P1 | 7 | 107 | `ASYNC_VOID` 79 + `NEW_HTTPCLIENT` 12 + `SYNC_OVER_ASYNC` 8 + `BLOCKING_SLEEP` 4 + `STRING_CONCAT_LOOP` 2 + `DISCARDED_TASK` 1 + `IMAGE_ALLOC` 1 | 12 处 HttpClient 改掉 1 处；弹幕接收路径改完（`b41a978`）；窗口切换改完（`08c0c0a`）；79 个 `async void` 一律未动 | 同上 §P1 |
| P2 | 8 | 273 | `MISSING_CT` 90 + `LINQ_IN_LOOP` 59 + `HTTP_BUFFERED` 53 + `ASYNC_LAMBDA_VOID` 29 + `TASK_RUN` 20 + `IMAGE_ALLOC` 15 + `CLOSURE_IN_LOOP` 4 + `JSONDOC_NO_USING` 1 + `ASYNC_NO_AWAIT` 2 | 未动 | 同上 §P2 |
| P3 | 4 | 325 | `CONFIGUREAWAIT` 199 + `LINQ_MATERIALIZE` 117 + `LOOP_DELAY` 9 | 未动（多数是正确用法） | 同上 §P3 |
| 不计入 | — | 26 | 静态工厂 16 + JSON 误报 7 + `IMAGE_IO` 3 | — | 同上 §9 |
| 合计 | 21 | 735 | 189 个文件有命中 | — | NET11_SOURCE_SCAN_MANIFEST.md §1、§5 |

两条与 `.NET 11` 的关系需要说清，避免把旧问题记成 11 的收益：

- 真正吃到 11 的只有"506 个 Task/ValueTask async 方法的状态机消除"；`async void` 79 个永远吃不到（NET11_HOTSPOTS.md §与 .NET 11 的真实关系）。
- Dynamic PGO、分层编译、`ValueTask`、`SearchValues<T>`、`FrozenDictionary` **不是 11 新增**（.NET 8 起），R2R 本期未开（NET11_RESEARCH.md §5、§2.7）。
- CS1998 判据失效，`async` 无 `await` 只能静态扫描：`src/` 406 个 `async Task` 方法里 8 个无 await（NET11_BUILD_AUDIT.md §11-B）。

## 5. 不建议做的项

| 项 | 判定 | 依据 | 若要重估的前提 |
|---|---|---|---|
| UI 主进程开 NativeAOT | 不做 | WinUI/WinAppSDK 的 AOT 支持长期受限（[microsoft-ui-xaml#8082](https://github.com/microsoft/microsoft-ui-xaml/discussions/8082)），UI 工程 `NoWarn` 里的 `IL2104`/`IL3053` 正是 WinRT.Runtime 的裁剪/AOT 警告；AOT 从未被执行，publish 通道的 AOT 分支从未验证（NET11_BUILD_AUDIT.md §5、§9、§11-C） | 先修好 publish 并跑通一次 x64 AOT 出包、再用无 XAML 的进程做对照。Visor 已按 NativeAOT 出包（3.4 MB 原生 exe），是唯一已验证的落点（§11-D） |
| 激进裁剪（`PublishTrimmed=true` 全量 + `TrimmerSingleWarn=false`） | 不做 | 153 个包无 net11 资产、大量反射/JSON/COM 路径；`TrimmerSingleWarn=false` 与 28 个 `IL*` 抑制等价于没有裁剪治理（NET11_BUILD_AUDIT.md §12-24、§14） | 先把 4 处反射 JSON 改源生成（NET11_HOTSPOTS.md P0-2），再逐条清 `IL*`，然后才谈裁剪 |
| Server GC | 不做 | 现为工作站 GC（属性未设，用运行时默认）；Server GC 会拉高内存与暂停，多核并行 GC 对 UI 线程无益（NET11_BUILD_AUDIT.md §13） | 只有在出现"GC 吞吐成为瓶颈"的实测证据时才评估 |
| `PublishReadyToRun=true` | 不做 | R2R 会把目标拉到 `x86-64-v3`（AVX/AVX2/BMI/FMA），老 CPU 退化；当前 `PublishReadyToRun=False` 是保守正确选择（NET11_BUILD_AUDIT.md §11-C；NET11_RESEARCH.md §2.7、§6） | 接受 v3 基线、并重估 APPX 体积与 `IL2xxx` 警告 |
| 关掉分层编译 / Dynamic PGO"提速" | 不做 | 两者都是默认开的正确配置（NET11_BUILD_AUDIT.md §13；NET11_RESEARCH.md §6） | 无 |
| 为 .NET 11 改 79 个 `async void` | 不做 | 事件签名就是 `void`，改不动形态；改的价值在稳定性（异常逃逸），不在 11 的收益（NET11_HOTSPOTS.md P1-1） | 按"抽 `async Task` 私有方法 + handler 只留 try/catch"单独排期 |
| 换 `IHttpClientFactory` | 本轮不做 | 要动 DI 容器与 22 个静态工厂，收益是 DNS 轮换与统一遥测，与"最小改动"相悖（NET11_P1_NETWORK_IMAGE.md §明确没做的事） | 先完成剩余 11 处 `new HttpClient()` 的复用化 |
| C# 15 `union` 放在 XAML 绑定类型上 | 不做 | 没有官方兼容矩阵；`x:Bind` 不认 union 类型（NET11_RESEARCH.md §4 注 2、§6） | 只允许落在 ViewModel/Service 层 |
| `TieredCompilation` / `TieredPGO` 手工配 | 不做 | 未设即用运行时默认（都开）；t1 列的 JIT 收益是"默认吃到"（NET11_BUILD_AUDIT.md §13） | 无 |

另有 6 处已被认定为正确实现、后续任务不应"顺手优化"：`SmallImageCache`（LRU 192 + 内存压力联动 + 按解码尺寸去重）、13 个 `static readonly HttpClient` 工厂、`GlobalSerializeContext` 的 7 个 `[JsonSerializable]`、`PlayerViewModel.Methods.cs:754-770` 的 `DispatcherQueue` 兜底、`obj/` 下 494 个生成文件、`#if DEBUG` 的 6 个诊断探针（NET11_HOTSPOTS.md §不该动的地方）。

## 6. 未验证项

| 项 | 缺什么 | 为什么缺 | 补它需要的条件 |
|---|---|---|---|
| ARM64 / x86 产物 | 两个架构的构建与计数 | 只在 x64 上跑；sln 把 Visor 映射成 AnyCPU（NET11_BUILD_AUDIT.md 附录 C） | 对应架构的 MSBuild 工具集 + 一次 `-t:Rebuild` |
| MSIX 打包链路 | `GenerateAppxPackageOnBuild` 在 net11 + WinAppSDK 2.6.5 上的出包证据 | 需要签名证书（`BiliCopilot.UI_TemporaryKey.pfx` / Store 证书）（附录 C） | 临时证书 + 一次打包 |
| AOT publish 可行性 | UI 工程的 AOT 出包结果 | 本轮 publish 未开 `PublishAot`（NET11_P0_FIXES.md 批 2 更正框） | 先把 `<PublishAot>true</PublishAot>` 写进 csproj（不能走命令行），再跑 publish 并做运行时冒烟 |
| 28 条裁剪开关的功能影响 | 逐条"哪些良性、哪些会咬人" | 只做了有/无对照（附录 C） | 用 AOT=True 产物对拍关键路径：`JsonSerializer` 无 source-gen、`System.Management`、EventSource 诊断、Reactor devtools |
| `Microsoft.UI.Reactor` 在 WindowsAppSDK 2.6.5 上的行为 | 专项验证 | 只确认了版本依赖的跨代关系（§14） | 一份 Reactor 用到 devtools/源映射的功能清单 + 逐项冒烟 |
| `UI.Models` 抬 TFM 的回归面 | 抬到 `net11.0` 后的全量回归 | 立报 `PinItem.cs:51` CS8765，要改源码（NET11_P0_FIXES.md 批 3） | 独立任务：改 `Equals(object?)` 签名 + 回归 |
| 用户侧 x86-64-v2 满足率 | 真实用户 CPU 分布 | 本机 Zen3 远超基线，测不出来（NET11_BUILD_AUDIT.md §11-D） | 遥测或发布说明口径（§11-D 已给可原样使用的对外说明） |
