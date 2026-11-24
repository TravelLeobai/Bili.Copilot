# .NET 11 构建配置审计与 runtime-async 生效验证

> 分支 `exp/net11-optimization`（未 push）· 仓库 `bili-copilot-v2`
> 审计时间 2026-11-24 · 本机 SDK `11.0.100`（另有 9.0.320 / 10.0.403）· csc `5.1200.26.53018`
> 本文只做审计，不改任何工程文件或源码。所有数字均由实测得到，命令与证据文件见附录 A / B。

---

## 0. 结论摘要

1. **runtime-async 真的生效了，但只覆盖 2 个程序集**：`BiliCopilot.UI` 与 `BiliCopilot.Visor` 的 csc 命令行里确有 `/features:runtime-async=on`；`Visor.Models`、`UI.Models`、`ResourceGenerator`、三个测试工程都没有。UI 程序集里 async 方法状态机从 **585 → 79**（−86.5%），dll 体积 −11.2%；Visor 从 **3 → 0**。
2. **剩下 79 个状态机 = 79 个 `async void` 事件处理器，另有 7 个 `yield` 迭代器**——与 t1 给出的"回退形态"完全对上，没有出现"Task 返回却回退"的异常案例。
3. **`PublishAot=True` 是 UI 工程的常开属性（`:1500`），它不只影响 publish**：实测它在**普通 build** 里就推出 `PublishTrimmed=true`、`SelfContained=true`，并往 `runtimeconfig.json` 写进 **28 个裁剪导向的特性开关**（含 `RuntimeFeature.IsDynamicCodeSupported=false`、`JsonSerializer.IsReflectionEnabledByDefault=false`、`EventSource.IsSupported=false`、`Reactor.DevtoolsSupport=false`）。关掉 PublishAot 后只剩 4 个。这是本轮最需要处理的一项。
4. **Release 现在根本编不过**：`DiagnosticsReport.cs:14` 的 `<see cref="PlaybackStallDump"/>` 指向一个整体包在 `#if DEBUG` 里的类型，Release 下报 CS1574，被 `TreatWarningsAsErrors=true` 提升为错误。也就是说 **CI 的 Release 矩阵目前必然失败**。
5. **`PublishAot` / `PublishReadyToRun` 从未被任何流程走到**：CI 只跑 `msbuild <csproj> /p:GenerateAppxPackageOnBuild=true`（build + 打包，不是 publish），工作流里没有任何 `pubxml` / `dotnet publish` / `-t:Publish`。t2 当时真去 `dotnet publish` 会在 C# 编译阶段就失败（CS0246 `StringNames`），`PublishAot` 换成 `false` 同样失败——这条本轮**未复现**（见 §9 更正框）。
6. **文档里说的 `UseRuntimeAsync=false` 在本机 SDK 无效**。实测 `-p:UseRuntimeAsync=false` 构建后仍是 86 个状态机（与开着完全一致）；只有 `-p:Features=` 能真正关断。SDK 的 targets/props 里搜不到 `UseRuntimeAsync`（见 §7）。
7. **`CS1998` 在 .NET 11 编译器里已经不存在**，UI 工程 `NoWarn` 里的 `CS1998` 是死抑制项。用一个 net11 探针工程验证：3 个"async 无 await"方法，`/nowarn` 清空、`warn:11`，**0 条警告**。仓库里真正"async Task 却无 await"的方法实测只有 **8 个**（§11-B）。
8. **三个测试工程升 net11 可行**：临时重定向后全部编过，`Diagnostics.Tests` **23/23** 通过、`CommentPoll.Tests` **84/84** 通过（net11.0 运行时）。但 `Diagnostics.Tests` 的大部分用例被 `#if DEBUG` 包着，`-c Release` 跑只剩 **5/23**。
9. **包版本存在明显的跨大版本漂移**：`Microsoft.UI.Reactor 0.1.0-preview.17` 是按 `WindowsAppSDK 2.2.x` 编的（还带 `net10.0-windows10.0.22621` 单一 TFM），`H.NotifyIcon.WinUI 2.3.2`、`CommunityToolkit.WinUI.*` 是按 **1.6.x** 编的，工程却钉 **2.6.5**；`Microsoft.Windows.CsWinRT 2.2.0` 这个钉版是死的，实际解析到 **2.3.0-prerelease.251015.2**。
10. **CI 与 net11 已经脱节**：`setup-env` 只装 `dotnet-version: 9.0.x`，nuget.config 还留着 `dotnet9` 源，仓库里**没有 global.json**。本机能编是因为本机装了 11.0.100 并被"最新 SDK"规则选中。

---

## 1. 审计范围

| 类别 | 文件 | 备注 |
|---|---|---|
| 解决方案 | `src/Bili.Copilot.sln` | 只含 5 个 src 工程，**不含任何测试工程** |
| 工程 | `BiliCopilot.UI.csproj`（1510 行） | WinExe / WinUI3 / MSIX |
| | `BiliCopilot.Visor.csproj` | 独立 exe，Native AOT |
| | `BiliCopilot.Visor.Models.csproj` | net11.0 类库 |
| | `BiliCopilot.UI.Models.csproj` | **netstandard2.0** 类库 |
| | `BiliCopilot.UI.ResourceGenerator.csproj` | netstandard2.0 源生成器（Roslyn analyzer） |
| | `tests/`：Diagnostics / CommentPoll / Moment | 全部 **net9.0**，全不在 sln 里 |
| 属性 | `src/Directory.Build.props` | 仅 src 生效；tests 不在其覆盖范围 |
| 包管理 | `src/Directory.Packages.props` | 中央包管理，仅 src 生效 |
| | `nuget.config` | 5 个源、无 packageSourceMapping |
| 缺失 | `global.json` | 全仓库不存在 → SDK 版本不受控 |
| 缺失 | `Directory.Build.targets` | 不存在；也没有自定义 MSBuild Target |
| 发布 | `Properties/PublishProfiles/win-{x86,x64,arm64}.pubxml` | FileSystem 发布，SelfContained=true |
| | `scripts/Publish-Visor.ps1` | 唯一真正跑 `dotnet publish` 的地方 |
| CI | `.github/workflows/*.yml`（6 个）、`.github/actions/{setup-env,build-app}` | 见 §10 |
| 其他 | `rd.xml`、`app.manifest`、`Package.appxmanifest` | 见 §12 |

---

## 2. 实测一：关键属性求值（`dotnet msbuild -getProperty`）

命令形态：

```powershell
dotnet msbuild <proj> -p:Platform=x64 -p:Configuration=<cfg> -p:SolutionDir=<repo>\src\ -getProperty:<name>
```

### 2.1 `BiliCopilot.UI`（三种配置）

| 属性 | Debug | Release | Publish |
|---|---|---|---|
| TargetFramework | `net11.0-windows10.0.22621.0` | 同 | 同 |
| TargetPlatformMinVersion | `10.0.19041.0` | 同 | 同 |
| Features | `;runtime-async=on` | 同 | 同 |
| UseRuntimeAsync | （空，未定义） | 空 | 空 |
| PublishAot | `True` | `True` | `True` |
| PublishTrimmed | `true`（由 AOT 推出） | `true` | `true` |
| SelfContained | `true` | `true` | `true` |
| Optimize | `false` | `true` | `True` |
| RuntimeIdentifier | `win-x64`（由 Platform 推出） | 同 | 同 |
| WindowsPackageType | `MSIX` | 同 | 同 |
| TieredCompilation / TieredPGO | （空 = 用运行时默认，即两者都开） | 同 | 同 |
| ServerGarbageCollection | （空 = 工作站 GC） | 同 | 同 |
| InvariantGlobalization | （空 = 用 ICU，zh-CN 正常） | 同 | 同 |
| LangVersion | `preview` | 同 | 同 |
| DebugType | `portable` / Deterministic `true` | 同 | 同 |
| Configurations | `Debug;Release`（**不含 Publish**） | 同 | 同 |
| DefineConstants | `TRACE;LOCAL_DEV;ARCH_X64;…;DEBUG` | `…;RELEASE` | `TRACE;ARCH_X64;…;PUBLISH`（**丢了 LOCAL_DEV**） |

### 2.2 其余工程

| 工程 | TFM | Features | PublishAot | PublishTrimmed | SelfContained | IsAotCompatible | IsTrimmable | LangVersion |
|---|---|---|---|---|---|---|---|---|
| Visor | `net11.0` | `;runtime-async=on` | `true` | `true` | `false`（脚本另行覆盖） | `true` | `true` | preview |
| Visor.Models | `net11.0` | 空 | 空 | 空 | 空 | 空 | 空 | preview |
| UI.Models | `netstandard2.0` | 空 | 空 | 空 | 空 | 空 | 空 | latest |
| Diagnostics.Tests | `net9.0` | 空 | 空 | 空 | 空 | 空 | 空 | 13.0 |

要点：

- `SelfContained=true` 在 **PublishAot=false 时依然为 true**（实测 `-p:PublishAot=false` → SelfContained 仍 `true`，PublishTrimmed 变空）。因此"自带 runtime"这件事不是 AOT 带来的，是 WinAppSDK/MSIX 单工程打包链路带来的；`dist/BiliCopilot-Reactor-x64/` 里成排的 `System.*.dll` 就是它的产物。
- `Configurations` 里没有 `Publish`，但 csproj `:444-452` 有 `'Publish|x64'` 的 `Optimize` 条件块。命令行传 `-c Publish` 时这些块**会**命中（实测 Optimize=True，且 DefineConstants 变成 `PUBLISH` 而不是 `RELEASE`/`LOCAL_DEV`）——能跑，但属于"IDE 里看不见、CLI 里能进"的隐式配置。

---

## 3. 实测二：每个程序集真实的 csc 命令行

来源：`dotnet build src/Bili.Copilot.sln -c Debug -p:Platform=x64 -v:detailed` 的日志（附录 B-1），抓 `bincore\csc.exe` 行。

| 程序集 | 含 `runtime-async=on` | 同一次编译里的另一条 `/features:` |
|---|---|---|
| `BiliCopilot.UI.dll`（XAML 第一遍 intermediatexaml） | **有**（独立的 `/features:runtime-async=on`） | `InterceptorsNamespaces=;Microsoft.UI.Reactor.Generated;;Microsoft.AspNetCore.Http.Generated;…` |
| `BiliCopilot.UI.dll`（第二遍，最终产物） | **有** | 同上 |
| `BiliCopilot.Visor.dll` | **有** | `InterceptorsNamespaces=;Microsoft.AspNetCore.Http.Generated;…` |
| `BiliCopilot.Visor.Models.dll`（net11.0） | 无 | `InterceptorsNamespaces=;Microsoft.Extensions.Validation.Generated` |
| `BiliCopilot.UI.Models.dll`（netstandard2.0） | 无 | 无 |
| `BiliCopilot.UI.ResourceGenerator.dll`（netstandard2.0） | 无 | 无 |
| 三个测试工程（net9.0） | 无 | 无 |

两条观察：

1. csc 收到的是**两条** `/features:` 参数：一条是 SDK/生成器机制塞的 `InterceptorsNamespaces=…`，另一条才是工程里写的 `runtime-async`。所以"命令行里有 runtime-async"这件事只能靠精确匹配 `runtime-async=on` 判断，`/features:` 出现一次并不等于生效。
2. `Features` 的值是 `;runtime-async=on`（**前导分号**，因为 `$(Features)` 当时为空）。csc 容忍空项，仅在 `InterceptorsNamespaces` 那条里留下一个空槽（`…Reactor.Generated;;Microsoft.AspNetCore…`）。属于卫生问题，不影响功能。

其余命令行事实：编译用 `D:\dotnet\sdk\11.0.100\Roslyn\bincore\csc.exe`（不是 dotnet 驱动）；UI 工程 `/warn:11 /langversion:15.0`；`/unsafe+`（UI、Visor）、`/unsafe-`（Visor.Models）；`/nowarn:` 里带着 39 项 UI 抑制清单（其中 28 项是 `IL*`、3 项是 `REACTOR_*`）。

---

## 4. 实测三：产物里的状态机计数（本文核心证据）

方法论：用 `System.Reflection.Metadata` 读每个 dll 的 **TypeDef 表**，统计名字以 `>d__N` 结尾的编译器生成类型（去重、精确，不受 `#Strings` 堆去重影响）；再按"是否存在 `<>2__current` 字段"区分**迭代器**与**async 方法**。

两组对照构建（同一台机器、同一 SDK、同一提交）：

- **ON**：仓库原样（`Features=;runtime-async=on`）
- **OFF**：`-p:Features=`（全局属性强制清空，工程里的赋值被忽略）

| 程序集 | 状态机 ON | 状态机 OFF | 变化 |
|---|---|---|---|
| `BiliCopilot.UI.dll`（Debug x64） | **86** | **592** | −506（−85.5%） |
| ├ 其中 `yield` 迭代器 | 7 | 7 | 0 |
| └ 其中 async 方法 | **79** | **585** | **−506（−86.5%）** |
| TypeDef 总数 | 2490 | 3008 | −518 |
| 文件大小 | 4,489,728 B | 5,057,536 B | **−11.2%（−554 KB）** |
| `BiliCopilot.Visor.dll` | **0** | **3** | −3（全部） |
| `BiliCopilot.Visor.Models.dll` | 0 | 0 | 工程内无 async |
| `BiliCopilot.UI.Models.dll` | 0 | 0 | 工程内无 async |

Release（`-c Release`）复测：`BiliCopilot.UI.dll` 仍 **86**（TypeDef 2466），`Visor.dll` 仍 **0** —— 两个配置一致，说明生效与优化级别无关。

### 4.1 剩下的 79 个到底是什么

- UI 源码里 `async void` 声明数（正则 `\basync\s+void\s+\w+\s*\(`，UI 工程 696 个 .cs 文件）＝ **79**，与"剩余 async 方法状态机 79"**一一对上**。
- 7 个迭代器是 `<CandidateRoots>d__5`、`<SplitEmotes>d__20`、`<SplitSentences>d__20`、`<Redraw>d__20`、`<get_ContentChildren>d__7`、`<get_HorizontalLines>d__11`、`<get_VerticalLines>d__9` 这类 `yield return` 方法（源码 `yield return` 共 15 处，其中 7 处落在编译进本程序集的类型里）。
- **没有**"返回 `Task` 却回退到状态机"的案例——585−79＝506 个 Task/ValueTask async 方法全部转换成功。

结论：runtime-async 覆盖边界与 t1 的表格一致；本项目里"剩下的"不是一个待修 bug，而是 `async void` 事件处理器这种**天然不能转换**的形态。若要让这 79 个也受益，只能把它们改成 `async Task` 并把 `+=` 订阅改用带 await 的调用点——那是代码改造（t3/t4 范畴），不是配置能解决的。

---

## 5. 实测四：`PublishAot=True` 泄漏到普通 build（P0）

同一份代码，只改 `-p:PublishAot`：

| runtimeconfig.json 键 | `PublishAot=True`（仓库默认） | `PublishAot=false` |
|---|---|---|
| 顶层 `includedFrameworks` | Microsoft.NETCore.App 11.0.0 | 同（自包含与 AOT 无关） |
| `configProperties` 条目数 | **28**（Debug 27） | **4** |

> **本轮复算（t8 复核 F1 要求）**：不去读产物，直接枚举喂给 `runtimeconfig.json` 的 MSBuild 项——
> `dotnet msbuild Desktop/BiliCopilot.UI/BiliCopilot.UI.csproj -p:Platform=x64 -p:Configuration=<cfg> -p:SolutionDir=<repo>\src\ -getItem:RuntimeHostConfigurationOption`。
> `<PublishAot>True</PublishAot>` 在 csproj 里时：Release **28** / Debug **27**；拆掉后：Release **5** / Debug **4**。
> 与上表一致（上表的 `PublishAot=false` 一列写 4，是 Debug 口径；Release 口径是 5，多出的那条是
> SDK 对非 Debug 配置默认的 `MetadataUpdaterSupport=false`）。复算明细与"与 t8 数字不一致"的留档见
> `docs/NET11_P0_FIXES.md` 批 2。

默认构建多出来的开关（节选，完整见附录 B-4）：

```
System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported : false
System.Runtime.InteropServices.BuiltInComInterop.IsSupported           : false
System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault           : false
System.Diagnostics.Tracing.EventSource.IsSupported                     : false
System.ComponentModel.TypeDescriptor.IsComObjectDescriptorSupported    : false
System.ComponentModel.Design.IDesignerHost.IsSupported                 : false
System.Data.DataSet.XmlSerializationIsSupported                        : false
System.Linq.Expressions.CanEmitObjectArrayDelegate                     : false
System.Reflection.Metadata.MetadataUpdater.IsSupported                 : false
System.Resources.ResourceManager.AllowCustomResourceTypes              : false
System.Resources.UseSystemResourceKeys                                 : false
System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization  : false
System.StartupHookProvider.IsSupported                                 : false
System.Text.Encoding.EnableUnsafeUTF7Encoding                          : false
Serilog.Capturing.IsStructureValueSupported                           : false
Reactor.DevtoolsSupport                                               : false
Microsoft.Extensions.DependencyInjection.VerifyOpenGenericServiceTrimmability : true
System.Linq.Enumerable.IsSizeOptimized                                : true
```

为什么这是 P0：

- 这些开关是**运行时契约**，不是编译期优化。它们会在 **Debug/Release 的普通 build 产物**里生效，而 CI 打包用的正是 build 产物，所以**装到用户机器上的包带着"无动态代码 / JSON 反射关闭 / EventSource 关闭 / Reactor devtools 关闭"的语义**。
- 其中 `Reactor.DevtoolsSupport=false` 这一条在**上一版被误归因给 AOT**：它只随 Configuration 走
  （Debug=true / Release=false），与 `PublishAot` 无关——复算时 AOT=True 的 Debug 清单里它就是 `true`。
  本节表格是 Release 口径，所以看到的是 `false`。
- 语义与"没开启裁剪"自相矛盾：程序集并没有真的被裁剪（`PublishTrimmed` 只在 publish 生效），但运行时被告知"不要做动态代码/反射"。这类错配是"平时没事、遇到 `JsonSerializer.Serialize<T>` 无 source-gen、`Expression.Compile`、`EventSource` 诊断、`BinaryFormatter` 兜底路径时猝死"的经典成因。
- 官方口径也印证了这条链：`PublishAot` 蕴含 `PublishTrimmed` 且"不能关"（dotnet/runtime#109452），而 WinUI/WinAppSDK 的 AOT 支持长期受限（[microsoft-ui-xaml#8082](https://github.com/microsoft/microsoft-ui-xaml/discussions/8082)），这正是 UI 工程 `NoWarn` 里塞了 `IL2104/IL3053` 的原因——那两个是 `WinRT.Runtime` 的裁剪/AOT 警告。

审计建议（不本轮改）：UI 工程应把 `PublishAot` 从常开属性改成"仅在某条真的会用到的发布命令里传参"，默认 `false`；`PublishReadyToRun=False` 反而是对的（见 §9）。若确实想要 AOT 收益，只应落在 Visor 这种无 XAML 的独立进程上。

---

## 6. 实测五：Release 编不过（P0，与 net11 无关）

```
dotnet build src\Bili.Copilot.sln -c Release -p:Platform=x64
→ 1 个警告 / 11 个错误，EXIT=1
```

首个错误：

```
src\Desktop\BiliCopilot.UI\Services\Diagnostics\DiagnosticsReport.cs(14,43): error CS1574:
  XML 注释中有未能解析的 cref 特性"PlaybackStallDump"
```

原因链：

- `Services/Diagnostics/PlaybackStallDump.cs:3` 起整体包在 `#if DEBUG` 里（`PlaybackStallPolicy/Budget/Probe/Watch` 同样，共 5 个文件）。
- `DiagnosticsReport.cs` **没有** DEBUG 包裹，`:14` 的文档注释仍 `<see cref="PlaybackStallDump"/>`。
- 于是 Release 下类型不存在 → CS1574 → `Directory.Build.props:8` 的 `TreatWarningsAsErrors=true` 把它升级成错误。
- 编译一旦失败，XAML 第二遍就缺 `-LocalAssembly`，接着喷 `WMC1509` + 一串 `WMC0001 Unknown type 'XxxConverter'` + `WMC9999`。**后面这一大片是次生灾害，真凶只有 CS1574。**

验证：加 `-p:TreatWarningsAsErrors=false` 后 Release **完全成功**（0 错误，2 警告，同为 CS1574）。

影响：CI 的 `build-app` 用 `/p:Configuration=Release`，`TreatWarningsAsErrors` 没有覆盖 → **Release 矩阵必然红**。这与 net11 迁移无关，是当前分支的既存状态；但它挡在"回归与性能验证"前面，t5/t6 之前必须先决定怎么处理（改注释 or 给 doc 警告豁免 or 只在这一个文件上加 `#if DEBUG`）。

---

## 7. 实测六：关断 runtime-async 的手段只有一个

| 手段 | 实测结果 | 判定 |
|---|---|---|
| `-p:Features=` | 592 个状态机（回到老形态） | **有效** |
| `-p:UseRuntimeAsync=false` | 86 个状态机，TypeDef 2490（与开启完全一致） | **无效** |

并且 `D:\dotnet\sdk\11.0.100` 下的 `*.props` / `*.targets` 里**搜不到** `UseRuntimeAsync` 或 `runtime-async` 字样——这个开关不由 SDK 实现。t1 文档里"用 `UseRuntimeAsync=false` 退出"这条**在本机 SDK 11.0.100 上不成立**；能用的就是 csproj 注释（`:19-26`）里已经写明的 `-p:Features=`。

> 备注：`Features` 不是 MSBuild 的重编译输入。改它之后普通 build 是空操作，必须 `-t:Rebuild`（或换 IntDir），否则会看到"改了没反应"。这条 csproj 注释里已经警示过，本轮实测也复现了（第一次 OFF 构建靠换 `BuildOutDir` 才真正重编）。

---

## 8. 实测七：测试工程 net9.0 → net11.0 可行性

方法：`dotnet restore -p:TargetFramework=net11.0 --force` 后 `dotnet build -p:TargetFramework=net11.0 --no-restore`（不改任何工程文件）。

| 工程 | net11.0 编译 | net11.0 跑测试 | 备注 |
|---|---|---|---|
| `BiliCopilot.Diagnostics.Tests` | ✅ 0 错 0 警 | ✅ **23/23 通过** | 链接 6 个 UI 诊断源文件 |
| `BiliCopilot.CommentPoll.Tests` | ✅ 0 错 1 警 | ✅ **84/84 通过** | 警告 `NU1510`：`Microsoft.Extensions.Logging.Abstractions` 在 net11 上已内置，显式引用多余 |
| `BiliCopilot.Moment.Tests` | ✅ 0 错 0 警 | —（不是测试工程） | `OutputType=Exe`、无 `IsTestProject`、无测试框架，是手工跑的小程序 |

阻碍项（都不致命）：

1. **三个工程都在 sln 外**，因此既不参与 `msbuild src/Bili.Copilot.sln`，也不继承 `src/Directory.Build.props`（没有 `TreatWarningsAsErrors`、`LangVersion` 只有 13.0）。升级时若要一并纳入治理，得先解决"props 作用域"问题。
2. **测试工程的包是逐工程写死 `Version=` 的**（因为不在 `Directory.Packages.props` 覆盖范围内），`xunit 2.7.0 / Microsoft.NET.Test.Sdk 17.10.0 / xunit.runner.visualstudio 2.5.7` 都是 net9 时代的钉子；升 net11 后建议一并升（本轮实测这三个版本在 net11 上能跑，不是硬阻塞）。
3. **6 个被链接的诊断源文件被"二次编译"**：一次进 net9 测试宿主，一次进 net11-windows 应用。这就是"这 6 个文件不能用 net11-only API"的机械原因——只要测试工程还是 net9，编译器就按 net9 的引用集编它们。
4. `#if DEBUG` 包裹导致 `-c Release` 时大部分用例**凭空消失**：`PlaybackStallDiagnosticsTests.cs` 与 `TestDoubles.cs` 带 `#if DEBUG`（`DanmakuRebuildTimingTests.cs` 不带）。实测同一条命令：`-c Debug` **23/23**，`-c Release` **只有 5/23 通过**（18 个播放停滞用例整体不存在）。CI 里若想跑真测试，必须固定 Debug。

---

## 9. 实测八：publish 通道是坏的，AOT 从未被执行

三条事实：

1. **CI 不走 publish**：`build-app/action.yml:28-49` 只有 `msbuild <project> /p:Configuration /p:Platform /p:SolutionDir /p:UapAppxPackageBuildMode /p:GenerateAppxPackageOnBuild=true /p:AppxPackageDir`；6 个 workflow 里 `pubxml|PublishProfile|dotnet publish|-t:Publish` 命中数为 **0**。`PublishProfile` 属性（csproj `:10`）只对 VS 的"发布"菜单有意义。
2. **真去 publish 会失败**（且与 AOT 无关）：

   > **更正（t9，来源：t8 复核 F3）**：这条在**当前 HEAD 上未复现**。带 restore 的
   > `dotnet publish … -c Release -p:Platform=x64 -p:SolutionDir=<repo>\src\ -o <产出目录>` 退出码 0、
   > 产出 619 个文件、无 CS0246 / `StringNames` 错误。下面这段按「t2 时的历史现象，工作区状态相关」
   > 保留；且那次 publish 没有开 `PublishAot`，**AOT publish 是否可行仍未验证**。

   ```
   dotnet publish src\Desktop\BiliCopilot.UI\BiliCopilot.UI.csproj -c Release -p:Platform=x64 -p:SolutionDir=<repo>\src\
   → error CS0246: 未能找到类型或命名空间名"StringNames"（LocaleExtension.cs / ResourceToolkit.cs / NavigationViewModel.cs）
   → 随后 WMC1509 + WMC0001 连锁（次生）
   ```

   把 `-p:PublishAot=false` 传进去，**同样的三个 CS0246**。也就是说 `PublishAot=True` 从未真正跑到 ILCompiler 那一步，那 28 个特性开关是 SDK 在 build 阶段就写进去的（§5）。
3. **不传 `SolutionDir` 的直连构建也是坏的**：`Directory.Build.props:17-19` 用 `$(SolutionDir)` 拼 `BuildOutDir`，`UI.Models.csproj:12` 用 `$(SolutionDir)Desktop\…\Resources.resw` 取资源。SolutionDir 为空时，前者把输出丢到仓库根的 `_build/`，后者取不到 resw → 源生成器不产出 `StringNames` → CS0246。**所有直连单个工程的命令都必须带 `-p:SolutionDir=<repo>\src\`**（CI 带了，所以 CI 这条路没踩）。

结论（对应队长问题 C）：`PublishReadyToRun=False` 与 `PublishAot=True` 目前都只是"声明"，不会被任何流程执行；风险不是"配错了会崩"，而是**配置撒谎**——它让后来者以为 AOT/R2R 已经在用，实际上打包产物是 build 输出，还额外背着一整套裁剪语义开关。

---

## 10. CI / SDK 版本链

| 项 | 现状 | 风险 |
|---|---|---|
| `setup-env/action.yml:61` | `dotnet-version: 9.0.x` | net11 工程靠 self-hosted runner 上"碰巧装了 11"才能编；干净 runner 上会 `NETSDK1045` |
| `global.json` | **不存在** | SDK 选择 = "最新安装版"，本机恰好 11.0.100；一旦 runner 升级 SDK 行为会漂移 |
| `nuget.config:9` | `dotnet9` 源（dnceng/public/dotnet9） | 过时源；net11 的包走 nuget.org，这条只剩噪声与解析成本 |
| `nuget.config` 无 `packageSourceMapping` | 5 个源全局可见 | `Directory.Build.props:6` 的 `NoWarn;NU1507` 就是给它擦屁股的 |
| CI 不构建 `BiliCopilot.Visor` | UI 只 `ProjectReference` 了 `Visor.Models` | Visor.exe 是**签进仓库的二进制**（`Assets/visor/{x64,arm64}/BiliCopilot.Visor.exe`，3.40 MB / 3.51 MB，2026-11-24 10:29） |
| `dist/BiliCopilot-Reactor-x64/Assets/visor/x64/BiliCopilot.Visor.exe` | 3,308,032 B，2026-11-03 | 比仓库里的**旧**一代；谁拿 dist 出包就会带旧 visor |

---

## 11. 队长四问的逐条回答

### A. 三个测试工程钉 net9.0；升 net11 是否可行？升级后那 6 个文件能否用 net11-only API？

**可行，且已实测**（§8）：三个工程用 `-p:TargetFramework=net11.0` 全部编译通过，`Diagnostics.Tests 23/23`、`CommentPoll.Tests 84/84` 在 `Microsoft.NETCore.App 11.0.0` 上跑绿。

具体阻碍只有四条：

1. 测试工程在 sln 外、在 `src/Directory.Build.props` 作用范围外 —— 升了也拿不到统一的分析器/警告策略（要单独给 `tests/Directory.Build.props`，或把 tests 挪进 sln 并调整 props 位置）。
2. 包版本是逐工程硬写的，`Microsoft.NET.Test.Sdk 17.10.0 / xunit 2.7.0 / xunit.runner.visualstudio 2.5.7` 属 net9 时代钉子；net11 能跑但建议连测试栈一起升。`Microsoft.Extensions.Logging.Abstractions` 在 net11 上会触发 `NU1510`（已内置、显式引用多余），可直接删。
3. `Moment.Tests` 不是测试工程（`OutputType=Exe`，无测试框架、无 `IsTestProject`），升级它只会得到"一个 net11 控制台小程序"，不要指望它进测试报告。
4. `#if DEBUG` 让 Release 测试变成空跑（§8 第 4 点）——升 TFM 不解决这个，反而更容易踩（因为大家会想"既然升了 net11，顺手 `-c Release` 跑一遍"）。

**升级后这 6 个文件是否就能用 net11-only API：能，但只在测试工程也升到 net11 之后。** 这 6 个文件（`PlaybackStallProbe/Policy/DumpBudget/Dump/Watch`、`DanmakuRebuildTiming`）在测试工程里是 `<Compile Include>` 链接进来的，编译器按**测试工程的 TFM** 编它；测试工程是 net9，它们就只能用 net9 API。全部升到 net11 后，两侧 TFM 对齐，就可以放心用 net11 API（包括 `SearchValues` 之类的 .NET 8+ API 本来就能用，真正"只有 net11 才有"的东西也在这一步解锁）。

一个反向约束别忘了：这 6 个文件必须保持**不依赖 WinUI / WinAppSDK**（当前实测只有 `System.*` + `Microsoft.Win32.SafeHandles`），否则一旦挪进 net11-windows 才能用 WinUI 类型，测试宿主就得跟着上 `net11.0-windows10.0.22621.0` + WinAppSDK，测试会从"毫秒级纯 BCL"变成"需要 WinUI 运行时"。

### B. CS1998 清点

**先给结论：`NoWarn` 里的 `CS1998` 已经是死项。** 探针工程（net11.0 类库，3 个 `async` 无 `await` 方法：`Task`、`Task<int>`、`async void`），`/nowarn` 清空、`/warn:11`，编译 **0 警告 0 错误**。这与 Roslyn 侧移除该诊断的记录一致（[dotnet/roslyn#82692](https://github.com/dotnet/roslyn/issues/82692) 引 #80144）。

因此"官方说的同步快路径"在这套工具链里**没有任何编译期信号**可以统计。改用静态扫描给出事实清单（`src/` 下 745 个 .cs，注释/字符串先剥离，`async Task|Task<T>|ValueTask…` 方法体是否出现 `await`）：

- 扫描到的 `async Task/ValueTask` 方法：**406 个**
- 其中**方法体内无 `await`**：**8 个**

| 位置 | 方法 |
|---|---|
| `Services/Live/BiliWbiSigner.cs:136` | `async Task<string> GetMixinKeyAsync(string cookie, ILogger? logger)` |
| `Services/Live/LiveDanmakuClient.cs:96` | `async Task<bool> SendDanmakuAsync(string roomId, string text)` |
| `Services/Live/LiveRoomApi.cs:372` | `async Task<string?> GetJsonAsync(string url, string cookie, ILogger? logger)` |
| `ViewModels/Components/SubscribedSeasonSectionDetailViewModel/SubscribedSeasonSectionDetailViewModel.cs:61` | `async Task TryFirstLoadAsync()` |
| `ViewModels/Core/AppViewModel/AppViewModel.cs:94` | `async Task LaunchAsync()` |
| `ViewModels/Core/AppViewModel/AppViewModel.cs:176` | `async Task ShowUpdateAsync()` |
| `ViewModels/Items/ArticleItemViewModel/ArticleItemViewModel.cs:65` | `async Task OpenInBroswerAsync()` |
| `ViewModels/Items/MomentItemViewModel/MomentItemViewModel.cs:268` | `async Task OpenInBroswerAsync()` |

怎么看这 8 个（供 t3/t4 排序用）：

- 它们**不是**"官方说的同步快路径受益项"。官方那条讲的是"async 方法在无挂起时同步完成时开销更低"，前提是**方法真的可能 await**。这 8 个是**根本没有 await 的 async 包装**——在 runtime-async 下它们会被编成"同步返回已完成 Task"，省掉的只是状态机；更彻底的做法是直接去掉 `async` 返回 `Task.CompletedTask`/`Task.FromResult`。8 个都动过手，收益也就 8 个方法的调用点，**优先级低**。
- 数量这么少也说明：`NoWarn;CS1998` 当年并不是为了掩盖一大片问题，而是被当成"清警告"的顺手操作。
- 另有 79 个 `async void` 事件处理器（分布 55 个文件，`PlayerViewModel.Methods.cs` 7 个、`SearchHomePage.xaml.cs` 3 个、`SearchHomePage`/`StartupPage` 2 个…）——它们与 CS1998 无关，但同样"永不转换成 runtime-async"（§4.1），是"延迟挂起不可优化"的最大单一群体。

### C. 发布路径核实

- `PublishAot=True`（UI，`:1500`）、`PublishReadyToRun=False`（`:1499`）**都不会被现有发布流程执行**：CI 是 `msbuild + GenerateAppxPackageOnBuild`（build + MSIX 打包），没有 publish 步骤；`pubxml` 也没被任何 workflow 引用。
- 但它们**不是无害的**：`PublishAot=True` 在 build 阶段就推出 `PublishTrimmed/SelfContained` 并写 28 个特性开关（§5）。所以"当前 R2R=False"这句现状描述要补一句：**R2R 确实没开，但 AOT 的副作用已经开了**。
- `PublishReadyToRun=False` 本身是**正确**的选择：R2R + WinUI3 + 大量泛型/反射的组合收益不确定，且会增加 APPX 体积、让 `IL2104/IL3053` 之类警告变复杂。t1 提到的"R2R 默认比较器专用化最多 20×"要吃到，前提是开启 R2R；本轮不建议为它破例。
- 真正的风险有三条，按严重度：① Release 编不过（§6）→ CI 必红；② `PublishAot` 的语义开关进了发布物（§5）；③ ~~publish 通道损坏（§9），任何人想走"官方 publish"都会被 CS0246 拦住~~ → 本轮带 restore 的 publish 已能跑通（§9 更正），这条降级为"t2 时的历史现象，未复现"。

### D. x86-64-v2 基线

- 实测确认发布物是**自包含**（`SelfContained=true`，`runtimeconfig.json` 里 `includedFrameworks: Microsoft.NETCore.App 11.0.0`，`dist/` 下有整排 `System.*.dll`）。这意味着**JIT 的指令集基线由本项目自带的 11.0.0 runtime 决定，与用户机器上装没装 .NET 11 无关**。
- 本机 CPU（AMD Ryzen 7 5800H，Zen3）远超基线，所以本地验证**不可能**暴露这问题——必须靠口径而不是靠测。
- 本项目**没有**开启 R2R（`PublishReadyToRun=False`），所以 t1 说的"R2R 目标 x86-64-v3"这一层**不适用**；只剩 JIT/AOT 的 x86-64-v2 要求。Visor 是 Native AOT 产物（3.4 MB 原生 exe），它按 AOT 基线编（同样 v2 兜底），但 Visor 只有滚轮增强这一条功能，最坏情况是"增强失效"而非主程序起不来。
- 实际用户面影响：`TargetPlatformMinVersion=10.0.19041.0`（Win10 2004）保证了 SSE4.2/POPCNT 这一代硬件（2008 年后），**x86-64-v2 在"能装 Win10 2004 的机器"上基本无风险**；理论断层只存在于 2008 年前的 x86-64 CPU 或某些虚拟机（未透传 SSE4.2 的老 hypervisor 配置），以及极旧的 Windows Server 虚拟化环境。
- **对外说明口径（建议原样使用）**：

  > 本版本使用 .NET 11 自包含运行时，要求 64 位 CPU 支持 SSE4.2 与 POPCNT（即 x86-64-v2 基线）。该系统要求自 .NET 11 起生效，覆盖 Windows 10 2004 及以上版本的绝大多数设备；若在极旧的 x86-64 设备或未开启相应指令集透传的虚拟机上出现启动失败，请回退到上一版本。

  同时在 release note 里补一句"ARM64 需要支持 LSE 原子的 Windows on Arm 设备"，避免 Arm64 用户踩到 t1 记的 +LSE 基线变更。

---

## 12. 重复 / 过时 / 冲突 / 无效参数清单

### P0（会导致构建/发布失败或改变运行时语义）

| # | 项 | 位置 | 现象 |
|---|---|---|---|
| 1 | Release 命中 CS1574 → 构建失败 | `Services/Diagnostics/DiagnosticsReport.cs:14` + `Directory.Build.props:8` | §6 |
| 2 | `PublishAot=True` 常开 | `BiliCopilot.UI.csproj:1500` | §5：普通 build 也写 28 个裁剪特性开关 |
| 3 | ~~publish 通道损坏（CS0246 StringNames）~~ 降级为历史现象（本轮带 restore 的 publish 退出码 0、619 个文件） | `UI.Models.csproj:12` + 直连 build | §9 + 更正框 |
| 4 | CI 只装 .NET 9 | `.github/actions/setup-env/action.yml:61` | 干净 runner 上 `NETSDK1045` |

### P1（配置撒谎 / 覆盖不全 / 可维护性）

| # | 项 | 位置 | 说明 |
|---|---|---|---|
| 5 | `NoWarn;CS1998` 是死抑制 | `BiliCopilot.UI.csproj:16` | .NET 11 编译器不再产生 CS1998（§11-B），可删 |
| 6 | `UseRuntimeAsync` 不存在 | 全局 | t1 记录的退出开关在本机 SDK 无效（§7）；正确手段是 `-p:Features=` |
| 7 | runtime-async 覆盖不全 | `Visor.Models.csproj` 无 `Features`；`UI.Models` 是 netstandard2.0 | 现在这两个工程里没有 async 方法所以看不出损失；一旦有人往 Models 里写 async，就永远拿不到运行时 async（且 netstandard2.0 结构上不可能） |
| 8 | `Directory.Packages.props:41` 的 `CsWinRT 2.2.0` 是死钉 | 实际解析 **2.3.0-prerelease.251015.2** | 钉版与实际不符，且实际是**预发布版**；建议要么显式引用并钉住，要么删掉这条误导性钉版 |
| 9 | `rd.xml` 只对 NativeAOT 生效 | `BiliCopilot.UI.csproj:30` + `rd.xml` | 内容 `<Assembly Name="BiliCopilot.UI" Dynamic="Required All">` 是 UWP/.NET Native 时代写法；SDK 的 props/targets 里没有消费 `RdXmlFile`，ILLink 也不认（实测：ILCompiler 的 targets 里才有 `@(RdXmlFile->'--rdxml:%(FullPath)')`）。对 UI 工程**完全无效**，却会让人误以为"裁剪时反射被保护了" |
| 10 | `LOCAL_DEV` 与 `ARCH_X64`/`ARCH_ARM64`/`ARCH_X86` 全是死符号 | csproj `:429-443` | 全仓库 `.cs/.xaml` 里**零引用**（grep 实测）；且 `LOCAL_DEV` 在 Debug **和** Release **都**定义（`:429-434` 两个分支写的一样），Publish 配置下反而不定义 |
| 11 | `Configurations` 不含 `Publish`，但存在 `Publish\|x64` 条件块 | csproj `:444-452` vs 求值结果 | CLI 能进、IDE 看不到；且 Publish 配置下 `LOCAL_DEV` 丢失、定义变成 `PUBLISH`——三套 `DefineConstants` 互不一致 |
| 12 | 三个测试工程在 sln 外 | `src/Bili.Copilot.sln` | 既不参与解决方案构建，也拿不到 `src/Directory.Build.props` 的警告/分析器策略 |
| 13 | 测试用例被 `#if DEBUG` 包住 | `tests/BiliCopilot.Diagnostics.Tests/*.cs` | Release 下只剩 5/23 个用例（18 个消失） |
| 14 | CI 不构建 Visor，但 UI 依赖其二进制 | `Assets/visor/**` | 换 TFM/AOT 设置后必须手工跑 `scripts/Publish-Visor.ps1`，否则应用跑的是旧 exe（脚本注释里已警示） |
| 15 | `dist/` 里的 Visor 落后一代 | `dist/BiliCopilot-Reactor-x64/Assets/visor/x64/` | 3,308,032 B（11-03） vs 仓库 3,399,680 B（11-24） |
| 16 | 无 `global.json` | 仓库根 | SDK 版本不受控；CI 与本地可能用不同 SDK |
| 17 | `nuget.config` 残留 `dotnet9` 源 + 无 `packageSourceMapping` | `nuget.config:9` | 直接导致要 suppress `NU1507`（`Directory.Build.props:6`） |

### P2（卫生问题，不影响正确性）

| # | 项 | 位置 | 说明 |
|---|---|---|---|
| 18 | `Nullable` 重复声明两次 | `Directory.Build.props:5` 与 `:14` | 后者覆盖前者，无实际差异 |
| 19 | `EditorConfigFilePath` 不是标准属性 | `Directory.Build.props:16,23` | `.editorconfig` 本来就会被 Roslyn 自动当 analyzerconfig 读；再当 `AdditionalFiles` 加一次是重复投递 |
| 20 | `Features` 前导分号 | `UI.csproj:27`、`Visor.csproj:8` | 产生空 feature 槽（§3） |
| 21 | 66 条包钉版无人引用 | `Directory.Packages.props` | 136 条 `PackageVersion` / 70 个真实 `PackageReference`（其中 7 条 analyzer 的 `PackageReference` 写在中央文件里，扣掉后约 **59 条无人引用**）。含 `System.Private.Uri 4.3.2`、`System.ValueTuple 4.5.0`、`System.Threading.Tasks.Extensions 4.5.4`、`Microsoft.Bcl.HashCode/AsyncInterfaces/Numerics/TimeProvider`、`Polly`、`Flurl.Http`、`Grpc.*`、`QRCoder`、`NLog.*` 等一批 net11 上已被内置或已废弃的东西。注意：其中一部分可能是**刻意的传递依赖钉版**，删前要逐条确认 |
| 22 | `OutDir/IntDir` 直接重定向而非 `BaseOutputPath/BaseIntermediateOutputPath` | `Directory.Build.props:17-19` | 会造成 `_build/` 与 `obj/` 两套中间产物语义分裂；历史上已经留下 `src/_build/Platform=x64/`（把 `Platform=x64` 当成了目录名）、`src/BiliCopilot.Visor/obj/x64/Release/net9.0/` 等残留 |
| 23 | 子工程输出平台不一致 | sln 把 Visor 映射到 `Any CPU` | 解决方案以 `-p:Platform=x64` 构建时，Visor/Visor.Models 落到 `_build/AnyCPU/…`，UI 落到 `_build/x64/…`，UI 再从 AnyCPU 目录 copy-local（日志里 MSB3027 的路径可见）。能work，但 `dotnet clean` 与并行构建会踩锁 |
| 24 | `TrimmerSingleWarn=false` | `UI.csproj:14` | 与 `NoWarn` 里 28 个 `IL*` 组合起来是"每条都报、然后全部消掉"，等价于没有 trimming 治理 |
| 25 | `AnalysisMode=AllEnabledByDefault` + `TreatWarningsAsErrors` + 39 项 NoWarn | `Directory.Build.props:8,11,12` + `UI.csproj:16` | 分析器开得最猛、抑制也最多；本轮发现的实际价值是它帮忙抓到了 CS1574（虽然是它自己制造的 Release 红灯） |

---

## 13. JIT / PGO / GC / R2R 配置是否合理

| 项 | 工程里的值 | 运行时生效值 | 判定 |
|---|---|---|---|
| TieredCompilation | 未设 | 默认开（Core 3.0 起） | ✅ 合理，不要动 |
| TieredPGO | 未设 | 默认开（.NET 8 起，**非 11 新增**） | ✅ 合理 |
| ServerGarbageCollection | 未设 | 工作站 GC | ✅ 对 WinUI 桌面应用正确（Server GC 会拉高内存与暂停，且多核并行 GC 对 UI 线程无益） |
| ConcurrentGarbageCollection | 未设 | 默认开 | ✅ |
| InvariantGlobalization | 未设 | ICU 生效 | ✅ 必须如此（中文资源/排序） |
| PublishReadyToRun | `False` | 无 | ✅ 保守正确（见 §11-C） |
| PublishAot（UI） | `True` | build 阶段即注入裁剪特性开关 | ❌ 见 §5 |
| PublishTrimmed（UI） | 由 AOT 推出 `true` | publish 才真正裁剪；build 只进 analyzer | ❌ 同上 |
| `System.Threading.ThreadPool.UseWindowsThreadPool` | 由 WinAppSDK/CsWinRT 注入 `true` | 启用 Windows 线程池 | ⚠️ 性能相关事实，t5/t6 做基准时要知道线程池实现已经不是 .NET 默认那个 |
| JIT 指令集基线 | 不可配 | .NET 11 默认 x86-64-v2 | 见 §11-D |

结论：**JIT/PGO/GC 这一层配置是干净的（全靠默认值），不需要为了 .NET 11 改任何东西**。t1 里列的一大票 JIT 收益（内联预算、GVM/DIM 去虚拟化、BCE、async 进 tier1）都是"默认吃到"，不需要工程配合。真正需要处理的是 `PublishAot` 这一条。

---

## 14. 第三方包矩阵与 net11 兼容风险

解析结果取自 `src/Desktop/BiliCopilot.UI/obj/project.assets.json`（153 个直接+传递包），TFM 取自各包 `lib/` 目录实测。

| 包 | 钉版 | 实际解析 | 包内 TFM | 风险 |
|---|---|---|---|---|
| Microsoft.WindowsAppSDK | 2.6.5 | 2.6.5（Base/Foundation/InteractiveExperiences/WinUI/Runtime/AI/DWrite/Widgets 同版） | 拆包，`WindowsAppSDK.WinUI` 的 lib 是 `net6.0-windows10.0.17763.0` | ⚠️ 无 net11 资产，靠 netcoreapp 兼容回落；MSIX 单工程打包链路对 `PublishAot` 极其敏感 |
| Microsoft.UI.Reactor | 0.1.0-preview.17 | 同 | **`net10.0-windows10.0.22621` 单一 TFM** | ⚠️ 只为 net10 编；其 nuspec 依赖 `WindowsAppSDK.WinUI 2.2.1` / `Runtime 2.2.0`，被工程的 2.6.5 抬上去 → **Reactor 在 2.6.5 上属于未验证组合**。另：其 `Reactor.DevtoolsSupport` 开关被 UI 的 PublishAot 关掉了 |
| Microsoft.Graphics.Win2D | 1.4.0 | 同 | `net6.0-windows10.0.19041.0` | ⚠️ 要求 `TargetPlatformMinVersion >= 19041`（本工程 19041 ✅，刚好卡边） |
| ComputeSharp.D2D1.WinUI | 3.2.0 | 同 | `net8.0-windows10.0.22621` | ⚠️ D2D1 着色器源生成器按 net8 编；AOT/裁剪下其生成的 COM 互操作需要实测 |
| Microsoft.Windows.CsWinRT | 钉 2.2.0 | **2.3.0-prerelease.251015.2** | `net6.0`,`net8.0` + analyzers/build | ❗ 钉版失效且实际是**预发布**；WinRT 投影行为随它变 |
| H.NotifyIcon.WinUI | 2.3.2 | 同 | `net8.0/net9.0-windows10.0.17763` | ⚠️ nuspec 依赖 **WindowsAppSDK 1.6.250108002**，工程跑 2.6.5（跨大版本） |
| CommunityToolkit.WinUI.Controls.Primitives（及 Segmented/Sizers） | 8.2.250402 | 同 | 最高 `net9.0-windows10.0.19041` | ⚠️ 同样按 **1.6.x** 编，跨大版本 |
| Microsoft.Windows.SDK.BuildTools | 10.0.26100.6901 | 同 | n/a（build 资产） | ✅ 与 `22621` 目标兼容 |
| Microsoft.Windows.SDK.BuildTools.MSIX | 未钉（Reactor 传递） | 1.7.260930100 | n/a | ⚠️ 未钉版、由 Reactor 带进来 |
| Microsoft.Web.WebView2 | 1.0.3719.77 | 同 | 原生 + net 资产 | ✅ |
| System.Drawing.Common | 未钉（Reactor 传递） | 10.0.14 | — | ⚠️ Windows-only，AOT/裁剪下有已知坑 |
| 其余（Danmaku.*、Richasy.*、Serilog dev 包…） | 混合 preview / `-dev-` 版本 | — | — | ⚠️ `Serilog 4.3.1-dev-02385`、`Serilog.Extensions.Logging 9.0.3-dev-02320` 是开发版包；`Richasy.*` 全是 preview。这些不是 net11 引入的问题，但会干扰"出问题到底怪谁"的定位 |

要点：**这些包没有一个声明 `net11` 资产**，全部走"高版本 TFM 向下兼容"回落。对 net11 而言这是被支持的机制（net11 可消费 net10/net9/net8/net6 资产），所以"能不能跑"没问题；问题在**验证责任转移到我们这边**：运行时 async、裁剪、AOT、CsWinRT 投影这些交叉项，包作者都没测过。

---

## 15. Debug 与 Release 的差异是否合理

| 维度 | Debug | Release | 判定 |
|---|---|---|---|
| Optimize | false | true | ✅ |
| DefineConstants | `TRACE;LOCAL_DEV;ARCH_X64;…;DEBUG` | `…;RELEASE`（**同样带 LOCAL_DEV**） | ❌ LOCAL_DEV 两处都定义且全仓库零引用（§12-10） |
| DebugType | portable | portable | ✅ （WinUI/MSIX 通常保留 pdb 便于诊断；若在意体积可在 Release 用 `embedded`/`none`） |
| PublishAot / PublishTrimmed / SelfContained | True / true / true | 同 | ❌ 与配置无关的常开项（§5） |
| runtime-async | 生效（86 状态机） | 生效（86 状态机） | ✅ 一致 |
| 编译结果 | 成功 | **失败（CS1574）** | ❌ §6 |
| 测试 | 真跑（23/23） | 只跑 5/23（18 个用例被 `#if DEBUG` 剥掉） | ❌ §8 |

一句话：Debug/Release 的**优化级与符号设置是合理的**；不合理的是"Debug 能编、Release 编不过"以及"Release 的测试是空跑"这两条——它们会让任何人误判"这条分支是健康的"。

---

## 16. 建议（本轮不改代码，供后续任务排期）

按投入产出排序：

1. **修 Release 红灯**（1 行级）：`DiagnosticsReport.cs:14` 的 cref 改成纯文本，或给 `PlaybackStall*` 的引用加 `#if DEBUG`。这是 CI 转绿的前置条件。
2. **拆掉 UI 工程的 `PublishAot=True`**：默认 `false`，需要 AOT 的只有 Visor。副作用是 `runtimeconfig.json` 从 28 个开关回到 4 个，`IL2xxx` 的 `NoWarn` 清单也能缩掉一大截。评估这一步时至少要跑一次冒烟：`JsonSerializer` 无 source-gen 的路径、`System.Management`、Reactor devtools。
3. **给 `Visor.Models` 补 `Features=runtime-async=on`**（和 `IsAotCompatible`），把 `UI.Models` 从 `netstandard2.0` 抬到 `net11.0`——这两条现在没有收益，但挡着以后往 Models 写 async。
4. **加 `global.json` 钉 SDK（11.0.100 + rollForward 策略）**，同时把 `setup-env` 的 `dotnet-version` 改成 11.0.x。这是"构建可复现"的最低成本一项。
5. **测试工程升 net11 并纳入 sln**：升完那 6 个诊断文件就能用 net11-only API；顺手清 `#if DEBUG` 造成的 Release 空跑。
6. **publish 通道要么修好、要么删掉**：`pubxml` 三个 + `PublishAot` 现在都是"看起来能发、实际发不了"。如果确定只走 MSIX build，就把它们收进文档别留成诱饵。
7. **清理死配置**：`rd.xml`、`LOCAL_DEV`/`ARCH_*`、`NoWarn;CS1998`、`CsWinRT 2.2.0` 钉版、66 条未用钉版、`dotnet9` 源。都是低风险纯删除，但会显著降低下一位审计者的误判率。
8. **`dist/` 与 `Assets/visor` 的版本对齐**：把 visor exe 重新发布并同步到 dist，或在 dist 里加一个"由 Publish-Visor.ps1 生成"的说明。

---

## 附录 A：复现命令（全部在 `bili-copilot-v2` 下执行，`$env:PATH` 前置 `D:\dotnet`）

```powershell
# 1) 属性求值
dotnet msbuild src\Desktop\BiliCopilot.UI\BiliCopilot.UI.csproj -p:Platform=x64 -p:Configuration=Debug `
  -p:SolutionDir=$PWD\src\ -getProperty:TargetFramework -getProperty:Features -getProperty:PublishAot `
  -getProperty:PublishTrimmed -getProperty:SelfContained -getProperty:TieredPGO

# 2) 抓 csc 命令行（看 /features:runtime-async=on 落在哪些程序集）
dotnet build src\Bili.Copilot.sln -c Debug -p:Platform=x64 -v:detailed --no-restore > build.log

# 3) 状态机计数（ON）
dotnet build src\Bili.Copilot.sln -c Debug -p:Platform=x64 -v:minimal --no-restore

# 4) 状态机计数（OFF，必须换输出目录才会真正重编）
dotnet build src\Bili.Copilot.sln -c Debug -p:Platform=x64 "-p:Features=" `
  "-p:BuildOutDir=<temp>\off\x64\Debug\" -v:minimal --no-restore

# 5) Release（会因 CS1574 失败；加 TreatWarningsAsErrors=false 才能过）
dotnet build src\Bili.Copilot.sln -c Release -p:Platform=x64 --no-restore

# 6) runtimeconfig 对照
dotnet build src\Bili.Copilot.sln -c Debug -p:Platform=x64 -p:PublishAot=false `
  "-p:BuildOutDir=<temp>\noaot\x64\Debug\" --no-restore
#   然后读 <out>\BiliCopilot.UI\bin\BiliCopilot.UI.runtimeconfig.json

# 7) UseRuntimeAsync 无效验证
dotnet build src\Bili.Copilot.sln -c Debug -p:Platform=x64 -p:UseRuntimeAsync=false `
  "-p:BuildOutDir=<temp>\usert\x64\Debug\" --no-restore

# 8) 测试工程升 net11（不改工程文件）
dotnet restore tests\BiliCopilot.Diagnostics.Tests\BiliCopilot.Diagnostics.Tests.csproj -p:TargetFramework=net11.0 --force
dotnet build   tests\BiliCopilot.Diagnostics.Tests\BiliCopilot.Diagnostics.Tests.csproj -p:TargetFramework=net11.0 --no-restore
dotnet test    tests\BiliCopilot.Diagnostics.Tests\BiliCopilot.Diagnostics.Tests.csproj -p:TargetFramework=net11.0 --no-build

# 9) publish 通道验证（两种都会失败，证明与 AOT 无关）
dotnet publish src\Desktop\BiliCopilot.UI\BiliCopilot.UI.csproj -c Release -p:Platform=x64 -p:SolutionDir=$PWD\src\
dotnet publish src\Desktop\BiliCopilot.UI\BiliCopilot.UI.csproj -c Release -p:Platform=x64 -p:SolutionDir=$PWD\src\ -p:PublishAot=false
```

状态机计数的脚本逻辑（临时脚本，不落仓库）：

```powershell
Add-Type -AssemblyName System.Reflection.Metadata
$fs = [System.IO.File]::OpenRead($dll); $pe = [System.Reflection.PortableExecutable.PEReader]::new($fs)
$md = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
foreach ($h in $md.TypeDefinitions) {
  $n = $md.GetString($md.GetTypeDefinition($h).Name)
  if ($n -match '>d__\d+$') { $sm++ }        # 状态机（含迭代器）
}
# 迭代器判定：该类型有名为 '<>2__current' 的字段
```

## 附录 B：证据文件（均在仓库外的临时目录 `_tmp/`，不入库）

| 编号 | 文件 | 内容 |
|---|---|---|
| B-1 | `_tmp/net11build-debug.log` | Debug x64 全量 `-v:detailed` 日志（含 5 个程序集的 csc 命令行与 `/features:`） |
| B-2 | `_tmp/release-build.log` / `release-build2.log` | Release 失败（CS1574 + WMC 连锁） / 加 `TreatWarningsAsErrors=false` 后成功 |
| B-3 | `_tmp/cs1998probe.log` | net11 探针：`async` 无 `await` → 0 条 CS1998 |
| B-4 | 仓库内 `src/_build/x64/Release/BiliCopilot.UI/bin/BiliCopilot.UI.runtimeconfig.json` | 28 个特性开关（AOT 默认） |
| B-5 | `_tmp/noaot-x64/Debug/BiliCopilot.UI/bin/BiliCopilot.UI.runtimeconfig.json` | 4 个开关（PublishAot=false） |
| B-6 | `_tmp/async-no-await.txt` | 8 个"async Task 无 await"的 文件:行 + 签名 |
| B-7 | `_tmp/publish-aot.log` / `publish-aot2.log` / `publish-noaot.log` | 三次 publish 均 CS0246 |
| B-8 | `_tmp/offbuild/`、`_tmp/usert-x64/`、`_tmp/noaot-x64/`、`_tmp/dbg2/` | 各对照构建的产物（状态机计数来源） |
| B-9 | `_tmp/count-sm2.ps1`、`classify-sm.ps1`、`list-sm.ps1`、`scan-async-no-await.ps1` | 本轮的四个分析脚本 |

## 附录 C：本轮未覆盖 / 未下结论的部分

- **ARM64 与 x86 未实测**（只在 x64 上跑；sln 把 Visor 映射成 AnyCPU，跨架构产物一致性没验证）。
- **MSIX 打包链路未跑**（需要签名证书 `BiliCopilot.UI_TemporaryKey.pfx` / Store 证书）；`GenerateAppxPackageOnBuild` 在 net11 + WinAppSDK 2.6.5 上能否出包，本轮没有证据。
- **AOT 真实可行性未验证**（publish 坏在更早一步）。只知道 Visor 的 AOT 出包是成功的（仓库里有 3.4 MB 原生 exe）。
- **runtimconfig 那 28 个开关对功能的具体影响未逐个验证**（只做了"有/无"对照）；哪些是良性（例如 `EventSource.IsSupported=false` 在无诊断需求时无所谓）、哪些会咬人（`JsonSerializer.IsReflectionEnabledByDefault`、`IsDynamicCodeSupported`），建议 t5/t6 在跑回归前先用 `/p:PublishAot=false` 的产物对拍一遍关键路径。
- **`Microsoft.UI.Reactor` 在 WindowsAppSDK 2.6.5 上的行为**未做专项验证；只确认了版本依赖的跨代关系。
