# .NET 11 P0 拦路石修复（承接 NET11_BUILD_AUDIT.md）

> 分支 `exp/net11-optimization`（未 push）· 仓库 `bili-copilot-v2` · 2026-11-24
> SDK `11.0.100` · 只改工程配置与一行 XML 文档注释，不动业务逻辑、不动 79 个 `async void`

## 批 1 · Release 红灯（P0）

`src/Desktop/BiliCopilot.UI/Services/Diagnostics/DiagnosticsReport.cs:14`

```diff
-    /// <summary>停滞 dump 的目录（与 <see cref="PlaybackStallDump"/> 写入位置一致）.</summary>
+    /// <summary>停滞 dump 的目录（与 <c>PlaybackStallDump.cs</c> 的写入位置一致；该类只在 Debug 构建里参与编译）.</summary>
```

`PlaybackStallDump` 整体在 `#if DEBUG` 里（同目录 5 个文件都是），Release 下 cref 解析不到 → CS1574 → 被 `Directory.Build.props:8` 的 `TreatWarningsAsErrors` 升成错误，随后 WMC1509/WMC0001/WMC9999 全是次生灾害。**没有把类型挪出 `#if DEBUG`**——那会改变 Release 行为。全仓 `cref` 扫过，指向 DEBUG-only 类型的只有这一处。

| 命令 | 结果 |
|---|---|
| `dotnet build …BiliCopilot.UI.csproj -c Release -p:Platform=x64` | 修前 EXIT=1（1 error），修后 **EXIT=0，0 警告 0 错误** |

## 批 2 · `PublishAot` 语义（P0）

`src/Desktop/BiliCopilot.UI/BiliCopilot.UI.csproj:1500` 的 `<PublishAot>True</PublishAot>` 已删除（原位置留注释说明为什么不再常开）。

**改前实测**（Release 产物 runtimeconfig）：`configProperties` **28** 条，含 `RuntimeFeature.IsDynamicCodeSupported=false`、`JsonSerializer.IsReflectionEnabledByDefault=false`、`EventSource.IsSupported=false`、`Reactor.DevtoolsSupport=false`……（Debug 27 条）。

**改后实测**：

| 产物 | 开关数 | 剩下的都是什么 |
|---|---|---|
| Debug | **4** | `MVVMTOOLKIT_ENABLE_*`、`EnableUnsafeBinaryFormatterSerialization`、`CSWINRT_USE_WINDOWS_UI_XAML_PROJECTIONS`、`Reactor.DevtoolsSupport=true` |
| Release | **5** | 上面 4 条 + `System.Reflection.Metadata.MetadataUpdater.IsSupported=false` |

Release 多的那条**与 AOT 无关**：SDK `Microsoft.NET.Sdk.targets:164` 对非 Debug 配置默认 `MetadataUpdaterSupport=false`（关热重载）。裁剪导向的 23 条已全部消失，`Reactor.DevtoolsSupport` 也从被误关的 `false` 回到 `true`。

其他语义不变：`SelfContained` 仍是 `true`（这是 WinAppSDK/MSIX 单工程打包带来的，t2 §2.2 已实测与 AOT 无关），产物仍是自包含、`includedFrameworks` 照旧；`PublishReadyToRun=False` 保留。

**取舍**（也是提交说明里那条）：UI 工程的 publish 通道本来就坏（`dotnet publish` 在 C# 编译阶段就 CS0246 `StringNames`，把 `PublishAot` 设成 false 同样失败），CI 只走 `msbuild + GenerateAppxPackageOnBuild`，从没有 `publish` 步骤——也就是说这个 AOT 从未被执行，只留下了 28 条运行时语义开关。真要给 UI 进程上 AOT：先修 publish 通道，再在那条命令上显式传 `-p:PublishAot=true`。Visor 的 Native AOT 不受影响，仍在它自己的工程里。

## 批 3 · runtime-async 覆盖（P1）

- `BiliCopilot.Visor.Models.csproj` 加 `<Features>$(Features);runtime-async=on</Features>`。csc 命令行复核（`-t:Rebuild -v:detailed`）：`/langversion:preview /features:runtime-async=on /embed:obj\x64\Debug\net11.0\BiliCopilot.Visor.Models.*`，工程求值 `Features=;runtime-async=on`。当前该工程 0 个 async 方法，所以是零收益的"补洞"，防的是以后有人往 Models 写 async 时悄悄退回状态机。`IsAotCompatible` 没跟着加——那只会把 ILLink 警告经本仓库的 `TreatWarningsAsErrors` 变成红灯，与本次目标无关。
- `BiliCopilot.UI.Models`（netstandard2.0）**评估后不抬**：改成 `net11.0` 编译立刻报 `PinItem.cs(51,26): error CS8765`（`override bool Equals(object obj)` 与 net11 里带可空标注的 `Equals(object?)` 不匹配）——修它要动 `PinItem.cs`，超出本任务 inScope 清单，而本项目当前一个 async 都没有，收益为零。留作独立任务：抬 TFM + 改签名 + 回归，一次做完。`ResourceGenerator` 保持 netstandard2.0（源生成器宿主约束）。
- 状态机计数未退化：Debug `BiliCopilot.UI.dll` **86（7 个迭代器 + 79 个 async）**，与 t2 完全一致；`BiliCopilot.Visor.dll` **0**。

## 批 4 · 测试工程 TFM（P1）

三个测试工程 `net9.0` → `net11.0`。`CommentPoll` 删掉 `Microsoft.Extensions.Logging.Abstractions`（net11 共享框架已内置，只剩 NU1510 警告）；xunit / Test.Sdk 版本一个没动（仍走工程内钉版）。

| 命令 | 结果 |
|---|---|
| `dotnet test tests/BiliCopilot.CommentPoll.Tests/…` | **84/84 通过**，0 警告 |
| `dotnet test tests/BiliCopilot.Diagnostics.Tests/…` | **23/23 通过**（默认 Debug） |
| `dotnet build tests/BiliCopilot.Moment.Tests/…` | 0 错 0 警（该工程是控制台小程序，不是测试工程） |

**已知限制（没改测试逻辑）**：`Diagnostics.Tests` 里 18 个用例被 `#if DEBUG` 包着，`-c Release` 跑只剩 **5/23**。CI/本地跑这套测试必须固定 Debug。

## 验证汇总

| 命令 | 结果 |
|---|---|
| `dotnet build BiliCopilot.UI.csproj -c Release -p:Platform=x64` | EXIT=0，0 警告 0 错误（无 CS1574 / WMC0001） |
| `dotnet build BiliCopilot.UI.csproj -c Debug -t:Rebuild -p:Platform=x64` | EXIT=0，0 警告 0 错误 |
| `dotnet build BiliCopilot.Visor.Models.csproj -c Debug -t:Rebuild` | EXIT=0，csc 带 `/features:runtime-async=on` |
| `dotnet build BiliCopilot.Visor.csproj -c Debug` | EXIT=0，`Visor.dll` 状态机 0 |
| 两套 `dotnet test` | 84/84、23/23 |

备注：本机默认 Debug 输出目录一度被两个无窗口的旧实例锁着（`BiliCopilot.UI` PID 13604 起于 18:38、`BiliCopilot.Visor` PID 42424 起于 18:21），`-t:Rebuild` 的 copy 阶段报 MSB3021/MSB3027（`BiliCopilot.UI.Models.dll` 被占用）。关掉这两个实例后同一条命令 0 错 0 警通过；此前另用隔离输出目录（`-p:BuildOutDir=<temp>\…`）做过一次对照构建，两处结论一致（86 状态机、4 条开关）。

## 本轮没做的事

- 79 个 `async void` 一个没动（天然不能转 runtime-async，见 t2 §4.1）。
- 未 push，未改 sln、未改 `Directory.*.props`、未动 CI。
- `UI.Models` 抬 TFM、`rd.xml` / 死符号 / `CsWinRT` 死钉 / 66 条未用钉版等 P1/P2 项不在本任务范围（见 t2 §12）。
