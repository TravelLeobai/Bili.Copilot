# NET11_VERIFY_RAW — t11 独立验证原始记录

> 本文件只放命令、原始输出与分档结论（实测 / 推断 / 未验证），不做综合评价。评价由 Lead 汇总。
> 采集人：verifier（t11），采集窗口 2026-11-24 20:51–21:10（Asia/Shanghai）。

## 0. 被测对象与环境

| 项 | 值 | 取得方式 |
|---|---|---|
| 仓库 | `E:\Documents\deepseek-harness\default-workspace\bili-copilot-v2` | — |
| 分支 / HEAD | `exp/net11-optimization` / `555fabfc231f4d1762fba9063cbd162075b53af2`（短 `555fabf`） | `git rev-parse HEAD` |
| 工作区状态 | `M src/Desktop/BiliCopilot.UI/BiliCopilot.UI.csproj`（**注释块内文字改动，无代码语义**；见 §1.4） | `git status --porcelain` |
| SDK | `11.0.100`（同机另有 9.0.320 / 10.0.403） | `D:\dotnet\dotnet.exe --version` |
| OS | Windows NT 10.0.29712.0 | `[Environment]::OSVersion` |
| CPU / 逻辑核 / RAM | AMD Ryzen 7 5800H / 16 / 31.4 GB | `Get-CimInstance Win32_Processor` 等 |
| 构建输出（隔离） | `E:\build-temp-dsh\out-verifier\` | `-p:BuildOutDir=` |
| 当时已注册包 | `Richasy.BiliCopilot.Dev_1.0.4.0_x64__g7ew4fxhv1f46` → `src\_build\x64\Debug\BiliCopilot.UI\bin`（dll 4,494,848 B，20:41:27，sha256 前 16 位 `A34DA6C8D0C1C965`） | `Get-AppxPackage` |

**没有重注册包**（按 Lead 指示：上次重注册把登录态弄丢）。因此 §4 冷启动测的是 **20:41 那次构建的产物**，不是我本次构建的产物；两者元数据计数一致（见 §2），文件大小差 512 B（见 §2 表注）。

### 0.1 并发构建的约束（实测，不是推测）

- `src\Directory.Build.props:17-19` 只把 `OutDir` / `IntDir` 迁到 `$(BuildOutDir)`，**`IntermediateOutputPath` 仍在仓库内**：`src\Desktop\BiliCopilot.UI\obj\x64\<Config>\net11.0-windows10.0.22621.0\win-x64\`。XAML 中间产物（`intermediatexaml\BiliCopilot.UI.dll`）落在该目录（`Microsoft.UI.Xaml.Markup.Compiler.interop.targets:182,213`：`XamlGeneratedOutputPath` 默认取 `$(IntermediateOutputPath)`）。
- 结论：**同一项目同一配置的两个 Rebuild 不能并发**，`-p:BuildOutDir` 隔离救不了 obj。本次通过「互斥锁文件 + 静默检查」串行化（脚本见 §10）。
- 隔离布局的副作用（实测）：`OutDir = $(BuildOutDir)\$(MSBuildProjectName)\bin` 不含配置名，**Debug 与 Release 共用同一 bin**，后一次构建会覆盖前一次产物。本记录中每个配置的数据都在该配置构建完成后立刻采集。

## 1. 干净构建

### 1.1 命令（原文）

```powershell
$env:TMP="E:\build-temp-dsh"; $env:TEMP="E:\build-temp-dsh"
cd E:\Documents\deepseek-harness\default-workspace\bili-copilot-v2\src
D:\dotnet\dotnet.exe build Desktop\BiliCopilot.UI\BiliCopilot.UI.csproj -c Debug -t:Rebuild -p:Platform=x64 `
  "-p:SolutionDir=E:\Documents\deepseek-harness\default-workspace\bili-copilot-v2\src\" `
  -p:BuildOutDir=E:/build-temp-dsh/out-verifier/
# Release 同一条，仅 -c Release
```

### 1.2 结果

| 配置 | 退出码 | 警告 | 错误 | 墙钟 | 产物 `BiliCopilot.UI.dll` | sha256 前 16 位 |
|---|---|---|---|---|---|---|
| Debug | 0 | 0 | 0 | 152.4 s（`已用时间 00:02:31.03`） | 4,495,360 B（≈4,390.0 KiB），mtime 20:58:13 | `66CD816BF120FFA0` |
| Release | 0 | 0 | 0 | 148.3 s（`已用时间 00:02:27.74`） | 3,893,248 B（≈3,802.0 KiB），mtime 21:02:11 | `82B46B95D1278D46` |

原始输出尾部（Debug）：

```
  BiliCopilot.UI -> E:\build-temp-dsh\out-verifier\BiliCopilot.UI\bin\BiliCopilot.UI.dll

已成功生成。
    0 个警告
    0 个错误

已用时间 00:02:31.03
EXITCODE=0 ELAPSED=152.4s
```

原始输出尾部（Release）：

```
  BiliCopilot.UI -> E:\build-temp-dsh\out-verifier\BiliCopilot.UI\bin\BiliCopilot.UI.dll

已成功生成。
    0 个警告
    0 个错误

已用时间 00:02:27.74
EXITCODE=0 ELAPSED=148.3s
```

产物清单（Release 覆盖后）：`out-verifier\BiliCopilot.UI\bin` 共 **619** 个文件，含 `BiliCopilot.UI.exe`（99,328 B）与 `AppxManifest.xml`（`Identity ... Version="1.0.4.0"`）。其余三个工程各 4 个文件（dll/pdb/xml/deps.json）。

### 1.3 第一次 Debug 尝试失败（原始记录，供解释为何有两次）

第一次（20:53:08 起）与另一条线的同项目 Rebuild（同分钟启动）撞在同一个 XAML 中间产物上：

```
CSC : error CS2012: 无法打开"...\src\Desktop\BiliCopilot.UI\obj\x64\Debug\net11.0-windows10.0.22621.0\win-x64\intermediatexaml\BiliCopilot.UI.dll"以进行写入
  - The process cannot access the file ... because it is being used by another process.;文件可能被 'Microsoft.UI.Xaml.Markup.Compiler' (31660) 锁定
E:\...\App.xaml(64,14): XamlCompiler error WMC0001: Unknown type 'UriToBitmapConverter' in XML namespace 'using:BiliCopilot.UI.Converters'
... （同型 WMC0001 共 9 条：LevelImageConverter / CountConverter / GridLengthConverter / SortTypeTextConverter /
     DurationConverter / ColorTextConverter / ColorToBrushConverter / PlayerPositionConverter）
Microsoft.UI.Xaml.Markup.Compiler.interop.targets(864,9): Xaml Internal Error error WMC9999: 未将对象引用设置到对象的实例。
... XamlCompiler warning WMC1509: No LocalAssembly parameter given during MarkupCompilePass2 ...
    1 个警告，11 个错误，EXITCODE=1
```

判定（实测 + 推断分列）：**实测**——同一时刻存在另一个 `dotnet build Desktop/BiliCopilot.UI/... -c Debug -t:Rebuild`（PID 37744，20:53:08）与 `Microsoft.UI.Xaml.Markup.Compiler`（PID 31660）；CS2012 直接点名该 dll 被锁。**推断**——WMC0001/WMC9999/WMC1509 是 pass2 拿不到刚生成的中间程序集导致的连锁症状，不是源码错误；依据是同一 HEAD 在无并发时（§1.2）0 错 0 警。

### 1.4 工作区那处未提交改动的性质

`git diff -- src/Desktop/BiliCopilot.UI/BiliCopilot.UI.csproj` 的全部内容都在 `<!-- -->` 注释块内（改写 PublishAot 取舍说明的措辞），无 XML 元素/属性变更，故不影响任何产物。

## 2. 状态机计数（读程序集元数据，不用文本 grep）

### 2.1 命令

```powershell
cd E:\build-temp-dsh\verifier
D:\dotnet\dotnet.exe run count-states3.cs -- "E:\build-temp-dsh\out-verifier\BiliCopilot.UI\bin\BiliCopilot.UI.dll"
```

脚本用 `System.Reflection.Metadata` 打开 PE/CLR 元数据，按方法上的自定义特性分类（脚本全文见 §10.1）：

- `AsyncStateMachineAttribute` → async 方法
- `AsyncIteratorStateMachineAttribute` → async 迭代器
- `IteratorStateMachineAttribute` → 同步迭代器
- 另计名字含 `d__` 的嵌套类型（编译器生成的状态机类型）作交叉核对

### 2.2 结果

| 产物 | 大小 | async | asyncIterator | iterator | `d__` 嵌套类型 | 三类合计 |
|---|---|---|---|---|---|---|
| Debug（20:58:13） | 4,495,360 B | **108** | 0 | **7** | **86** | 115 |
| Release（21:02:11） | 3,893,248 B | **108** | 0 | **7** | **86** | 115 |
| 已注册的 20:41 构建（`src\_build\x64\Debug\bin`） | 4,494,848 B | **108** | 0 | **7** | **86** | 115 |

原始输出（Debug）：

```
== BiliCopilot.UI.dll  (4495360 bytes, mtime 2026-11-24 20:58:13)
   async               : 108      (在 d__ 类型内: 0)
   asyncIterator       : 0      (在 d__ 类型内: 0)
   iterator            : 7      (在 d__ 类型内: 0)
   d__ nested types    : 86
   三类合计             : 115   (d__ 86)
   局部函数/闭包里的 async 前 5 例: <>c__DisplayClass3_0::<SelectGroup>b__0, <>c__DisplayClass5_0::<SelectSection>b__0, <>c__DisplayClass4_0::<RenderQRCode>b__0, <>c__DisplayClass2_0::<Refresh>b__0, <>c__DisplayClass3_0::<ChangeChildPartition>b__0
```

原始输出（Release，仅前 6 行）：

```
== BiliCopilot.UI.dll  (3893248 bytes, mtime 2026-11-24 21:02:11)
   async               : 108
   asyncIterator       : 0
   iterator            : 7
   d__ nested types    : 86
```

### 2.3 口径说明与未解差异

- **实测**：`AsyncStateMachineAttribute` 108 个方法、`IteratorStateMachineAttribute` 7 个方法、`d__` 类型 86 个；三者跨 Debug/Release/已注册构建完全一致，没有配置间差异。
- **推断（未 A/B 验证）**：108 ≠ 86 差的 22 个，可能是 runtime-async 下部分 async 方法不再生成状态机类型（改为普通方法 + AsyncHelpers 形态），特性仍打在原方法上。没有做「同源码 runtime-async=off 重编译」的对照，所以这条只是推断。
- 逐个数的旧口径（`d__` = 86，即 7 迭代器 + 79 async）与 t4 报告里的「86（7 + 79）」一致；但那个口径把 async 方法数记为 79 是「状态机类型数 - 迭代器数」的推算，不是直接数方法。本记录直接数方法得 108，两个数字都在上表可查，请 Lead 按需选用并说明口径。
- `MethodImplAttributes` 里出现过 `0x2000`（519 个方法，样例 `App::SyncQualityUnlockIfNeededAsync` 等）与 `0x0003`（72）、`0x0080`（44）、`0x0100`（21）、`0x0008`（1）；未解码含义，仅记录原始值。

## 3. 单测（两套独立复跑）

### 3.1 命令与结果

```powershell
$env:TMP="E:\build-temp-dsh"; $env:TEMP="E:\build-temp-dsh"
cd E:\Documents\deepseek-harness\default-workspace\bili-copilot-v2
D:\dotnet\dotnet.exe test tests\BiliCopilot.CommentPoll.Tests\BiliCopilot.CommentPoll.Tests.csproj -p:BuildOutDir=E:/build-temp-dsh/out-verifier/
D:\dotnet\dotnet.exe test tests\BiliCopilot.Diagnostics.Tests\BiliCopilot.Diagnostics.Tests.csproj -p:BuildOutDir=E:/build-temp-dsh/out-verifier/
```

| 工程 | TFM | 通过 | 失败 | 跳过 | 总计 | 耗时 | 退出码 |
|---|---|---|---|---|---|---|---|
| BiliCopilot.CommentPoll.Tests | net11.0 | 84 | 0 | 0 | 84 | 82 ms | 0 |
| BiliCopilot.Diagnostics.Tests | net11.0 | 23 | 0 | 0 | 23 | 672 ms | 0 |

原始输出行：

```
已通过! - 失败:     0，通过:    84，已跳过:     0，总计:    84，持续时间: 82 ms - BiliCopilot.CommentPoll.Tests.dll (net11.0)
已通过! - 失败:     0，通过:    23，已跳过:     0，总计:    23，持续时间: 672 ms - BiliCopilot.Diagnostics.Tests.dll (net11.0)
```

### 3.2 两条口径备注（都是实测）

- **`-p:BuildOutDir` 对测试工程不生效**：`tests\` 在仓库根、不在 `src\` 下，没有继承 `src\Directory.Build.props`，`BuildOutDir` 无处消费，产物实际落在 `tests\<proj>\bin\Debug\net11.0\`。这里照原样记录，不掩饰。
- 第一次把相对路径写成 `tests\...` 但从 `src\` 目录执行，得到 `MSBUILD : error MSB1009: 项目文件不存在`；测试工程在仓库根而非 `src\` 下。上面的命令已修正。
- 本次两套都是**增量**编译后运行（程序集已存在，日志显示 `-> ...Tests.dll` 后直接进入测试运行）；`CommentPoll` 用 `-t:Rebuild` 没做，属于口径差异，记录在案。

## 4. 冷启动（3 次，未登录态）

### 4.1 命令

```powershell
pwsh -NoProfile -File E:\build-temp-dsh\verifier\coldstart.ps1 -Runs 3
# 脚本逻辑：杀进程 → Start-Process "shell:AppsFolder\Richasy.BiliCopilot.Dev_g7ew4fxhv1f46!App"
#          → 每 50ms 轮询 BiliCopilot.UI 进程与其 MainWindowHandle → 记「启动到出窗口」毫秒数
#          → 出窗口后 4 s 采 Responding / WorkingSet / UIA 元素
```

计时起点是 `Start-Process` 调用返回前的时间戳，终点是第一次看到 `MainWindowHandle != 0`。

### 4.2 结果

| 次 | 出窗口 (ms) | 进程出现 (ms) | Responding | WorkingSet | UIA 元素数 |
|---|---|---|---|---|---|
| 1 | 3,932 | 1,329 | True | 222.2 MB | 19 |
| 2 | 2,611 | 108 | True | 226.0 MB | 19 |
| 3 | 2,535 | 111 | True | 224.8 MB | 19 |

**中位数 = 2,611 ms**（三次排序 2,535 / 2,611 / 3,932）。第一次含冷文件缓存，进程本身出现就花了 1,329 ms，后两次 108/111 ms。

> 脚本 `coldstart.ps1` 自己写出的 `medianMs` 字段是 3932，**这个字段算错了**：PowerShell 的 `[int](3/2)` 用银行家舍入得 2 而不是 1，取到了最大值。以原始三次值手算的中位数 2,611 ms 为准。

### 4.3 主窗口与登录状态（UIA 实测）

三次冷启动后 UIA 枚举结果一致：**19 个可命名元素，`ControlType.Edit` 数量 0，导航项（搜索/动态/直播/设置/热门/番剧/影视/排行榜）命中 0 个**。元素名清单：

```
版本 : 1.0.4.0 | 哔哩助理 | 请使用移动客户端扫描下方的二维码登录 | 刷新 | 探索视频应用桌面交互的新可能 | 网页登录 | 阅读文档 | AppWindow Custom Title Bar | Close | Maximize | Minimize | Non Client Input Sink Window
```

结论（实测）：**主窗口正常出现、进程 Responding=True、但停在扫码登录页**，没有搜索框，因此无法进入播放路径。这一条与 §5–§7 的卡点同源。

### 4.4 进程数（跑前 / 跑后）

| 时点 | BiliCopilot.UI | BiliCopilot.Visor |
|---|---|---|
| 跑前（21:04:26） | 1 | 1 |
| 跑后（最后一次冷启动后） | 1 | 1 |

## 5. 播放 ≥60 秒 + Responding —— **未验证（登录态缺失）**

- 卡点证据 1（日志，实测）：
  ```
  2026-11-24 20:36:25.444 [INF] 访问令牌：已就绪 access_token=**** uid=177800289 剩余有效期≈4291.5h
  2026-11-24 20:37:06.315 [INF] 访问令牌：当前没有 access_key（未登录或登录态未带令牌）
  2026-11-24 20:45:07.390 [INF] 访问令牌：当前没有 access_key（未登录或登录态未带令牌）
  2026-11-24 20:49:26.294 [INF] 访问令牌：当前没有 access_key（未登录或登录态未带令牌）
  2026-11-24 20:51:17.677 [INF] 访问令牌：当前没有 access_key（未登录或登录态未带令牌）
  ```
- 卡点证据 2（UIA，实测）：§4.3，主窗口只有登录页 19 个元素，`Edit` 控件 0 个，无搜索框 → 无打开视频的入口。
- 未做的动作：没有尝试图形界面之外的注入、没有改动应用数据、没有伪造令牌。**本条留空，不填「应该能播」。**
- 恢复条件：应用里完成一次扫码登录（日志出现 `访问令牌：已就绪`），随后按 §10.3 的流程可直接补测。

## 6. 播放器开关 30 轮 —— **未验证（登录态缺失）**

- 实际执行：探测性 2 轮（不是 30 轮），命令 `pwsh -File toggle-player.ps1 -Rounds 2`，结果两轮均：
  ```json
  { "round": 1, "keyword": "原神", "opened": false, "closed": false, "error": "search-unavailable" }
  { "round": 2, "keyword": "崩坏", "opened": false, "closed": false, "error": "search-unavailable" }
  ```
  脚本在动手前先打日志：`[21:07:41] 到不了搜索页，无法开始（可能停在登录页）`。
- 进程数（实测）：跑前 UI=1 / Visor=1，跑后 UI=1 / Visor=1。**在这个「什么都没打开」的状态下，进程数没有变化；但这不能当成「30 轮无泄漏」的证据**，30 轮循环从未发生。
- 因此：失败次数、泄漏、create/dispose 配对**无数据**。

## 7. dotnet-counters —— 播放场景**未验证**；工具可用性有实测结论

### 7.1 要求的命令（原样执行）

```powershell
dotnet-counters collect -p <pid> --counters System.Runtime[gc-heap-size,alloc-rate,gen-0-gc-count,gen-1-gc-count,gen-2-gc-count,time-in-gc,working-set] -o <path> --duration 00:00:30
```

### 7.2 对应用进程（PID 40956，BiliCopilot.UI，登录页）的结果

| 尝试 | 参数 | 输出文件 | 行数 |
|---|---|---|---|
| A | `--counters "System.Runtime[gc-heap-size,alloc-rate,gen-0-gc-count,gen-1-gc-count,gen-2-gc-count,time-in-gc,working-set]" --duration 00:00:30` | `counters-idle-210535.csv`（61 B） | **1（只有表头）** |
| B | `--counters "System.Runtime" --duration 00:00:10` | `counters-test2.csv` | **1（只有表头）** |
| C | `--counters "System.Runtime[dotnet.process.memory.working_set,dotnet.gc.collections]" --duration 00:00:06 --refresh-interval 1` | `counters-app-metricname.csv` | **1（只有表头）** |

A 的原始输出与文件全文：

```
Starting a counter session. Press Q to quit.
File saved to E:\build-temp-dsh\verifier\results\counters-idle-210535.csv
EXIT=0
--- 文件全文（61 字节）---
Timestamp,Provider,Counter Name,Counter Type,Mean/Increment
```

### 7.3 对照：同一台机、同一工具、另一个 .NET 11 进程

```powershell
dotnet-counters collect -p 42344 --counters "System.Runtime" -o counters-msbuild.csv --duration 00:00:06
```

```
Starting a counter session. Press Q to quit.
File saved to ...\counters-msbuild.csv
rows=163
Timestamp,Provider,Counter Name,Counter Type,Mean/Increment
11/24/2026 21:07:09,System.Runtime,dotnet.process.memory.working_set (By),Metric,300769280
11/24/2026 21:07:09,System.Runtime,dotnet.timer.count ({timer}),Metric,5
11/24/2026 21:07:09,System.Runtime,dotnet.gc.last_collection.heap.size (By)[gc.heap.generation=gen1],Metric,1612304
```

补充探测（实测）：

- `dotnet-trace ps` 能列出目标进程：`40956  BiliCopilot.UI  ...\src\_build\x64\Debug\BiliCopilot.UI\bin\BiliCopilot.UI.exe`。
- `dotnet-trace collect -p 40956 --profile gc-collect --duration 00:00:03` 报告 `Trace completed.`，输出文件仅 **1,926 B**（基本等于空 trace）。

**分档**：
- **实测**：工具链本身在本机可用（对 MSBuild 采到 163 行）；工具版本 `dotnet-counters 10.0.750501`（`dotnet tool list --global`）；对该应用进程三次采集均为 0 样本，CSV 只有表头，退出码 0；`dotnet-trace` 能 attach 但 trace 体积近乎空。
- **未验证**：为什么该进程一条计数都没有——可能是 MSIX 松散注册进程的诊断通道差异、也可能是工具 10.x 与运行时 11.0.0 的计数器通道差异。**没有定论，未做进一步隔离实验。**
- **未验证**：播放场景下的 gen0/1/2、分配速率、工作集——登录态缺失，播放从未发生（§5）。
- 附带观察：`dotnet-counters monitor` 在无控制台的作业里会崩（`System.IO.ConsolePal.GetBufferInfo ... 句柄无效`），不是应用问题；`collect` 子命令不受影响。

## 8. PERF UiLag 原始行（来源：应用自身日志，非本次采集）

**数据来源声明**：下面这些行来自应用日志文件本身，由**更早的会话**（t6 的播放实测）写出，不是我这次跑出来的——登录态缺失，我无法产生新的播放窗口。文件路径：

```
%LOCALAPPDATA%\Packages\Richasy.BiliCopilot.Dev_g7ew4fxhv1f46\LocalState\Logger\log-2026-11-24.txt
```

取数命令：

```powershell
$log="$env:LOCALAPPDATA\Packages\Richasy.BiliCopilot.Dev_g7ew4fxhv1f46\LocalState\Logger\log-2026-11-24.txt"
Get-Content $log | Where-Object { $_ -match "^2026-11-24 20:(09|10|11|12|13):" -and $_ -match "PERF UiLag" }
```

### 8.1 改造后会话（起播 20:10:16，日志里 34 行全量）

```
2026-11-24 20:10:06.101 [INF] PERF UiLag: n=195 p50=0.16ms p95=785.12ms max=1240.87ms
2026-11-24 20:10:06.102 [INF] PERF UiLag buckets: <16.7=160 <33.3=3 <50=0 <100=3 <200=5 >=200=24 p50=0.16ms p95=785.12ms max=1240.87ms
2026-11-24 20:10:16.097 [INF] PERF UiLag: n=198 p50=0.15ms p95=1.58ms max=86.66ms
2026-11-24 20:10:16.097 [INF] PERF UiLag buckets: <16.7=195 <33.3=1 <50=1 <100=1 <200=0 >=200=0 p50=0.15ms p95=1.58ms max=86.66ms
2026-11-24 20:10:26.098 [INF] PERF UiLag: n=194 p50=0.17ms p95=3.85ms max=39.38ms
2026-11-24 20:10:36.101 [INF] PERF UiLag: n=193 p50=0.15ms p95=1.85ms max=5.05ms
2026-11-24 20:10:46.108 [INF] PERF UiLag: n=195 p50=0.17ms p95=1.70ms max=59.82ms
2026-11-24 20:11:06.112 [INF] PERF UiLag: n=392 p50=0.17ms p95=1.84ms max=8.14ms
2026-11-24 20:11:16.113 [INF] PERF UiLag: n=198 p50=0.18ms p95=1.81ms max=15.51ms
2026-11-24 20:11:26.113 [INF] PERF UiLag: n=194 p50=0.14ms p95=1.54ms max=6.48ms
2026-11-24 20:11:36.114 [INF] PERF UiLag: n=198 p50=0.12ms p95=1.10ms max=2.05ms
2026-11-24 20:11:46.115 [INF] PERF UiLag: n=193 p50=0.12ms p95=1.10ms max=2.33ms
2026-11-24 20:11:56.115 [INF] PERF UiLag: n=195 p50=0.14ms p95=1.51ms max=3.38ms
2026-11-24 20:12:06.115 [INF] PERF UiLag: n=194 p50=0.14ms p95=2.16ms max=21.08ms
2026-11-24 20:12:26.116 [INF] PERF UiLag: n=390 p50=0.15ms p95=1.65ms max=10.11ms
2026-11-24 20:12:36.117 [INF] PERF UiLag: n=198 p50=0.14ms p95=1.39ms max=1.98ms
2026-11-24 20:12:46.122 [INF] PERF UiLag: n=191 p50=0.16ms p95=1.77ms max=6.70ms
2026-11-24 20:12:56.123 [INF] PERF UiLag: n=197 p50=0.19ms p95=2.34ms max=6.06ms
2026-11-24 20:13:06.123 [INF] PERF UiLag: n=190 p50=0.16ms p95=1.67ms max=5.45ms
```

（为控制篇幅，20:10:26–20:13:06 之间每个窗口的 `buckets` 行没有全贴；`PERF UiLag:` 行是全量。）

汇总（实测，按上面的原始行算）：

| 指标 | 值 |
|---|---|
| 起播前的窗口（20:10:06，含建窗/挂载） | p50 0.16 ms / p95 785.12 ms / max 1240.87 ms |
| 起播后窗口（20:10:16–20:13:06，16 行）p50 范围 | 0.12 – 0.19 ms |
| 同 16 行 p95 范围 | 1.10 – 3.85 ms |
| 同 16 行 max 范围 | 1.98 – 86.66 ms |
| 起播时刻（日志） | `20:09:58.401 初始化：创建播放器` → `20:10:15.924 初始化：播放器就绪` |

注：`20:11:06`（n=392）与 `20:12:26`（n=390）的采样数翻倍，是相邻两个窗口合并上报，不影响 p50/p95 口径说明。

### 8.2 改造前会话（18:50–18:57，同一日志文件里的相邻会话）

```
2026-11-24 18:51:32.243 [INF] PERF UiLag: n=389 p50=0.36ms p95=4.93ms max=238.68ms
2026-11-24 18:51:42.243 [INF] PERF UiLag: n=197 p50=0.25ms p95=2.09ms max=5.96ms
2026-11-24 18:51:52.248 [INF] PERF UiLag: n=197 p50=0.27ms p95=8.88ms max=155.25ms
2026-11-24 18:52:02.251 [INF] PERF UiLag: n=198 p50=1.33ms p95=519.79ms max=974.04ms
2026-11-24 18:52:12.255 [INF] PERF UiLag: n=188 p50=1.06ms p95=9.07ms max=94.05ms
2026-11-24 18:52:22.842 [INF] PERF UiLag: n=196 p50=2.07ms p95=6.96ms max=11.37ms
2026-11-24 18:52:32.847 [INF] PERF UiLag: n=197 p50=1.67ms p95=6.93ms max=16.39ms
2026-11-24 18:52:42.850 [INF] PERF UiLag: n=187 p50=0.65ms p95=4.44ms max=617.61ms
2026-11-24 18:52:52.855 [INF] PERF UiLag: n=189 p50=0.68ms p95=5.28ms max=30.77ms
2026-11-24 18:53:12.856 [INF] PERF UiLag: n=389 p50=0.64ms p95=3.02ms max=12.80ms
2026-11-24 18:53:22.856 [INF] PERF UiLag: n=186 p50=0.56ms p95=2.67ms max=20.84ms
2026-11-24 18:53:32.859 [INF] PERF UiLag: n=189 p50=0.35ms p95=2.05ms max=17.85ms
```

（更早的 18:50:02–18:51:12 八行 p50 0.13–0.24 ms、p95 0.60–2.79 ms，属播放前的空闲页；18:56 之后是退出播放器后的窗口，均未计入。）

汇总（实测）：

| 指标 | 值 |
|---|---|
| 起播窗口（18:52:02，含 `mpv` 拉起与首帧） | p50 1.33 ms / p95 519.79 ms / max 974.04 ms |
| 播放期窗口（18:52:12–18:53:32，共 8 行）p50 范围 | 0.35 – 2.07 ms |
| 同 8 行 p95 范围 | 2.05 – 9.07 ms |

**这不是受控 A/B**（实测边界，必须连着看）：

- 两次会话之间代码变了（t6 的 `69785b5` 提交时间 20:12:48、`08c0c0a` 20:12:56）；20:09 那次会话用的是改动后的工作树，18:5x 那次是改动前（两次会话与代码版本的对应关系取自 t6 的交付说明，我未独立核对当次构建）。
- 两次会话的播放内容不同（20:09 是普通视频 `BV1iFyfzWEMk`；18:5x 的播放对象从我拿到的日志无法确认）、窗口时长不同、机器状态（后台构建）不同。
- 会话里还有其它进程在跑（同期有 dotnet 构建），我没有做「关掉一切后单向变量」的对照。
- 因此：**两次的 p50/p95 只能并列展示，不能当作改造收益的定量结论**。真正的受控复算需要登录态恢复后，在安静机器上各跑一次。

## 9. 结论分档

### 实测（本次亲手跑出）

1. Debug `-t:Rebuild`：退出码 0、0 警告 0 错误、152.4 s、`BiliCopilot.UI.dll` 4,495,360 B。
2. Release `-t:Rebuild`：退出码 0、0 警告 0 错误、148.3 s、`BiliCopilot.UI.dll` 3,893,248 B。
3. 状态机计数：Debug/Release/已注册构建三者一致 —— async 方法 108、迭代器方法 7、`d__` 状态机类型 86（脚本读元数据，非 grep）。
4. 单测：CommentPoll 84/84、Diagnostics 23/23，均 net11.0，退出码 0。
5. 冷启动 3 次出窗口 3,932 / 2,611 / 2,535 ms（中位数 2,611 ms），三次都 Responding=True，主窗口标题「哔哩助理」。
6. 当前构建启动后停在扫码登录页：UIA 19 个元素、`Edit` 控件 0 个、无导航项。
7. `dotnet-counters` 对该应用进程采 30 s / 10 s / 6 s 均为 0 样本（只有 CSV 表头）；对 MSBuild（.NET 11）采 6 s 得 163 行 —— 工具本身可用。
8. `dotnet-trace` 能 attach 该进程，3 s `gc-collect` 只产出 1,926 B。
9. 环境约束确认：`IntermediateOutputPath` 未随 `BuildOutDir` 迁移，同项目同配置并发 Rebuild 会撞锁（CS2012 + 一串 WMC0001）。
10. 隔离布局下 Debug/Release 共用同一个 `bin` 目录（后构建覆盖前构建）。

### 推断（有依据但未做对照实验）

1. WMC0001/WMC9999/WMC1509 那 11 个错误是并发撞锁的连锁症状，不是源码缺陷（依据：同一 HEAD 无并发时 0 错 0 警）。
2. async 方法数 108 与状态机类型数 86 差 22，可能是 runtime-async 下部分 async 方法不再生成状态机类型（未做 runtime-async=off 的 A/B）。

### 未验证（缺条件，未编数据）

1. 打开视频播放 ≥60 s + 播放期 Responding —— 登录态缺失（§5）。
2. 播放器开关 30 轮（失败次数、进程泄漏、create/dispose 配对）—— 登录态缺失（§6）。
3. `dotnet-counters` 播放场景的 gen0/1/2、alloc-rate、time-in-gc、working-set —— 登录态缺失（§7）。
4. 为什么该应用进程采不到任何计数（工具/运行时 11 的通道问题？MSIX 进程差异？）—— 未定论（§7）。
5. 改前/改后 UiLag 的受控对比 —— 现有数据是两次非受控会话（§8）。

## 10. 复现材料

### 10.1 状态机计数脚本（`E:\build-temp-dsh\verifier\count-states3.cs`，file-based app，`dotnet run count-states3.cs -- <dll>`）

```csharp
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

static string? TypeName(MetadataReader md, EntityHandle h) => h.Kind switch
{
    HandleKind.TypeDefinition => md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)h).Name),
    HandleKind.TypeReference => md.GetString(md.GetTypeReference((TypeReferenceHandle)h).Name),
    _ => null
};
static string? AttrName(MetadataReader md, CustomAttribute ca) => ca.Constructor.Kind switch
{
    HandleKind.MemberReference => TypeName(md, md.GetMemberReference((MemberReferenceHandle)ca.Constructor).Parent),
    HandleKind.MethodDefinition => TypeName(md, md.GetMethodDefinition((MethodDefinitionHandle)ca.Constructor).GetDeclaringType()),
    _ => null
};

foreach (var path in args)
{
    using var fs = File.OpenRead(path);
    using var pe = new PEReader(fs);
    if (!pe.HasMetadata) { Console.WriteLine($"{Path.GetFileName(path)}: no metadata"); continue; }
    var md = pe.GetMetadataReader();

    int async = 0, asyncIter = 0, iter = 0;
    int asyncInD = 0, asyncIterInD = 0, iterInD = 0;
    var dTypes = new List<string>();

    foreach (var th in md.TypeDefinitions)
    {
        var td = md.GetTypeDefinition(th);
        var tname = md.GetString(td.Name);
        bool inD = tname.Contains("d__");
        if (inD) dTypes.Add(tname);
        foreach (var mh in td.GetMethods())
        {
            var m = md.GetMethodDefinition(mh);
            foreach (var cah in m.GetCustomAttributes())
            {
                var ca = md.GetCustomAttribute(cah);
                switch (AttrName(md, ca))
                {
                    case "AsyncStateMachineAttribute":         if (inD) asyncInD++; else async++; break;
                    case "AsyncIteratorStateMachineAttribute": if (inD) asyncIterInD++; else asyncIter++; break;
                    case "IteratorStateMachineAttribute":      if (inD) iterInD++; else iter++; break;
                }
            }
        }
    }
    Console.WriteLine($"== {Path.GetFileName(path)}");
    Console.WriteLine($"   async {async} (in d__: {asyncInD}) / asyncIterator {asyncIter} (in d__: {asyncIterInD}) / iterator {iter} (in d__: {iterInD})");
    Console.WriteLine($"   d__ nested types: {dTypes.Count}");
}
```

### 10.2 构建串行化的锁（Lead 给的方案，本次实际使用）

```powershell
$lock='E:\build-temp-dsh\ui-build.lock'
while(Test-Path $lock){ Start-Sleep -Seconds 15 }
New-Item -ItemType File -Path $lock -Force | Out-Null
try { <build 命令> } finally { Remove-Item $lock -Force -ErrorAction SilentlyContinue }
```

本次在锁之外还加了「静默检查」：等到没有 `dotnet.exe`（命令行含 ` build ` / ` test `）与 `Microsoft.UI.Xaml.Markup.Compiler.exe` 才落锁。

### 10.3 登录态恢复后可直接补跑的两条

```powershell
# 1) 播放场景 + GC/工作集（先拿到播放中的 PID）
dotnet-counters collect -p <pid> --counters System.Runtime[gc-heap-size,alloc-rate,gen-0-gc-count,gen-1-gc-count,gen-2-gc-count,time-in-gc,working-set] -o E:\build-temp-dsh\verifier\results\counters-play.csv --duration 00:00:30

# 2) 播放器开关 30 轮（脚本已备，未改注册）
pwsh -NoProfile -File E:\build-temp-dsh\verifier\toggle-player.ps1 -Rounds 30
```

### 10.4 采集现场文件（本机）

```
E:\build-temp-dsh\logs\debug-rebuild.log
E:\build-temp-dsh\logs\release-rebuild.log
E:\build-temp-dsh\logs\test-commentpoll.log
E:\build-temp-dsh\logs\test-diagnostics.log
E:\build-temp-dsh\logs\coldstart.log
E:\build-temp-dsh\logs\toggle-attempt.log
E:\build-temp-dsh\verifier\results\coldstart-*.json
E:\build-temp-dsh\verifier\results\counters-idle-210535.csv
E:\build-temp-dsh\verifier\results\counters-msbuild.csv
E:\build-temp-dsh\verifier\results\counters-app-metricname.csv
```


---

# 附：第二轮实测（t14，2026-11-24 21:37–22:10）

> 第一轮（t11）失败的三项在第二轮重跑：登录态已恢复、部署 bin 换成 HEAD 构建、dotnet-counters 能采到数。
> 本节仍只放命令、原始输出与分档结论。

## A0. 本轮对象与环境

| 项 | 值 |
|---|---|
| HEAD | `dafb451`（`docs(net11): 补齐 t13 的运行时复测记录…`），工作区 `git status` **干净** |
| SDK | `11.0.100` |
| 隔离构建输出 | `E:\build-temp-dsh\out-verifier\` |
| **已注册的包** | `Richasy.BiliCopilot.Dev_1.0.4.0_x64__g7ew4fxhv1f46` → `src\_build\x64\Debug\BiliCopilot.UI\bin`；dll 4,494,848 B，mtime 21:47:57，sha256 前 16 位 `669F6BA7…` |
| **该 bin 的 runtimeconfig 开关数** | **4 条**（`MVVMTOOLKIT_ENABLE_INOTIFYPROPERTYCHANGING_SUPPORT`、`System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization`、`CSWINRT_USE_WINDOWS_UI_XAML_PROJECTIONS`、`Reactor.DevtoolsSupport=true`）；**没有** `System.Diagnostics.Tracing.EventSource.IsSupported` |
| 登录态 | 21:30:55 / 21:33:31 / 21:33:39 / 21:33:46 / 21:37:30 / 21:51:13 / 21:58:03 均 `访问令牌：已就绪 access_token=**** uid=177800289` |

对照第一轮：20:41 那份部署 bin 的 runtimeconfig 带 27 条裁剪开关（含 `EventSource.IsSupported=false`），对它的 counters 采集是 0 样本。本轮换成 4 条开关的 HEAD 构建后，同一条命令采到 **210 行** —— 两轮差异只在这一处，与 §7 的观测一致。

## A1. 干净构建（Debug / Release 各一次）

```powershell
cd E:\Documents\deepseek-harness\default-workspace\bili-copilot-v2\src
D:\dotnet\dotnet.exe build Desktop\BiliCopilot.UI\BiliCopilot.UI.csproj -c Debug -t:Rebuild -p:Platform=x64 `
  "-p:SolutionDir=E:\Documents\deepseek-harness\default-workspace\bili-copilot-v2\src\" -p:BuildOutDir=E:/build-temp-dsh/out-verifier/
# Release 同一条，仅 -c Release
```

| 配置 | 退出码 | 警告 | 错误 | 墙钟 | `BiliCopilot.UI.dll` | runtimeconfig 开关数 |
|---|---|---|---|---|---|---|
| Debug | 0 | 0 | 0 | 167.5 s（`已用时间 00:02:46.77`） | 4,495,360 B，mtime 21:41:08 | 4（`Reactor.DevtoolsSupport=true`） |
| Release | 0 | 0 | 0 | 170.0 s（`已用时间 00:02:49.08`） | 3,893,248 B，mtime 21:47:52，sha16 `DB9DA0131D04850B` | 5（多 `MetadataUpdater.IsSupported`，`DevtoolsSupport=false`） |

并发情况（实测）：21:38:47 另一条线在跑 `dotnet build … -c Release`（PID 35444），与我的 Debug 构建同时进行；因配置不同，obj 分属 `obj\x64\Debug` 与 `obj\x64\Release`，本次没有撞车。我的 Release 构建等了它 ~2 分 45 秒（锁 + 静默检查）才落锁开跑，`已用时间` 只统计构建本身。

## A2. 状态机计数（读元数据）

```powershell
cd E:\build-temp-dsh\verifier
D:\dotnet\dotnet.exe run count-states3.cs -- "<dll 路径>"
```

| 产物 | 大小 | async | asyncIterator | iterator | `d__` 类型 |
|---|---|---|---|---|---|
| Debug（21:41:08） | 4,495,360 B | **108** | 0 | **7** | **86** |
| Release（21:47:52） | 3,893,248 B | **108** | 0 | **7** | **86** |
| 已注册 bin（21:47:57） | 4,494,848 B | **108** | 0 | **7** | **86** |

与第一轮（同样是 108 / 7 / 86）一致，没有任何一个配置在两次构建之间发生状态机数量变化。

## A3. 单测（独立复跑）

```powershell
cd E:\Documents\deepseek-harness\default-workspace\bili-copilot-v2
D:\dotnet\dotnet.exe test tests\BiliCopilot.CommentPoll.Tests\BiliCopilot.CommentPoll.Tests.csproj -p:BuildOutDir=E:/build-temp-dsh/out-verifier/
D:\dotnet\dotnet.exe test tests\BiliCopilot.Diagnostics.Tests\BiliCopilot.Diagnostics.Tests.csproj -p:BuildOutDir=E:/build-temp-dsh/out-verifier/
```

```
已通过! - 失败:     0，通过:    84，已跳过:     0，总计:    84，持续时间: 128 ms - BiliCopilot.CommentPoll.Tests.dll (net11.0)
已通过! - 失败:     0，通过:    23，已跳过:     0，总计:    23，持续时间: 1 s - BiliCopilot.Diagnostics.Tests.dll (net11.0)
```

（`-p:BuildOutDir` 对测试工程仍不生效 —— `tests\` 不在 `src\` 下、没有继承 `src\Directory.Build.props`，产物落 `tests\<proj>\bin\Debug\net11.0\`，见 §3.2。）

## A4. 冷启动 3 次（已登录态）

```powershell
pwsh -NoProfile -File E:\build-temp-dsh\verifier\coldstart.ps1 -Runs 3
```

| 次 | 出窗口 (ms) | 进程出现 (ms) | Responding | WorkingSet | UIA 元素 | 登录页 |
|---|---|---|---|---|---|---|
| 1 | 2,538 | 169 | True | 417.2 MB | 304 | 否 |
| 2 | 2,479 | 115 | True | 509.6 MB | 304 | 否 |
| 3 | 2,309 | 116 | True | 458.6 MB | 304 | 否 |

**中位数 = 2,479 ms**（排序 2,309 / 2,479 / 2,538）。三次主窗口标题都是「哔哩助理」，UIA 命中导航项 `搜索|动态|直播|设置`，主窗口元素数 304（第一轮登录页时是 19）。

## A5. 打开视频与播放 —— **播放未成立（mpv 初始化超时）**

### A5.1 入口方式（实测记录，供后人复用）

- 该版本 UI 树里**没有** `ControlType.Edit`，也没有任何支持 `ValuePattern` 的元素（全树 303 个元素的类型分布：Text 141 / Button 111 / ListItem 18 / Pane 11 / Group 3 / ScrollBar 2 / Custom 2 / List 2 / Window 1）→ 搜索框这条路走不通。
- 首页卡片 `ListItem(Name='ItemContainer')` 只有 `SelectionItemPattern`（`InvokePattern` 不存在），`Select()` **只选中、不播放**；实测无效。
- 可用配方：把窗口摆到 (40,20) 1500×950、`SetForegroundWindow` 后，点「页面上面积最大的元素」的中部（= 首条动态的大卡片缩略图）。点击后 `player-lifecycle` 日志出现 `create-player:start ← MomentItemViewModel.ActivateAsync`，播放窗口（类名 `Microsoft.UI.Windowing.Window`）在 **730–780 ms** 内出现。

### A5.2 三次尝试，全部 mpv 初始化超时

| 尝试 | 启动到出窗口 | 播放窗口出现 | 播放器初始化 | 结果 |
|---|---|---|---|---|
| 1 | 1,675 ms | 781 ms | `timeout` 20,726 ms | 放弃这轮播放 |
| 2 | 2,288 ms | 734 ms | `timeout` 20,762 ms | 放弃这轮播放 |
| 3 | 2,285 ms | 730 ms | `timeout` 20,741 ms | 放弃这轮播放 |
| 4（30 轮循环第 1 轮） | — | — | `timeout`（22:00:18 告警） | 放弃这轮播放 |
| 5（间隙 12 s 后再试） | — | 22:10:15 起 init | `timeout`（22:10:37.975 告警） | 放弃这轮播放 |

原始日志（第三次，22:00 前后）：

```
2026-11-24 22:00:18.436 [WRN] mpv 播放器初始化超过 20 秒没返回，放弃这轮播放
2026-11-24 22:00:26.629 [INF] PERF Load.MpvInit: n=1 p50=20012.34ms p95=20012.34ms max=20012.34ms
2026-11-24 22:00:26.629 [INF] PERF COUNT Load.MpvInit.Timeout=1
```

`player-lifecycle-20261124.log`：

```
21:56:40.791 | create-mpv-client:start | PlayerVM#1 | tid=2
21:56:41.077 | create-mpv-client:end   | PlayerVM#1 | tid=2 | elapsed=284ms
21:56:41.803 | create-player:start    | PlayerVM#1 | tid=2
21:57:01.821 | create-player:timeout  | PlayerVM#1 | tid=2 | abandoned, no fallback, no dispose
```

对照：21:40:29 那次（engineer2 的会话）是 `create-player:end … elapsed=11555ms`（成功）。同一台机、同一份已注册构建，21:56 之后连续 4 次全部超时；**播放是否成立在两次实验中不一致，原因未查明**（本节标「未验证」）。

同一时间段里 mpv 其实已经出画（`[MPV] Event received: AudioReconfig / VideoReconfig`），但 ViewModel 侧的播放初始化没有完成，日志里同时刷出成片的 NRE（见 A7）。

## A6. 播放器开关 30 轮（打开/关闭）

```powershell
pwsh -NoProfile -File E:\build-temp-dsh\verifier\r2-toggle-final.ps1 -Rounds 30 -CountersSeconds 30
```

每轮：杀进程 → 冷启动计时 → 点大卡片 → 等播放窗口出现（openMs）→ 关播放窗口（WM_CLOSE 等消失，closeMs）→ 记录「关闭后进程数」→ 杀进程记录残留。

| 指标 | 值 |
|---|---|
| 轮数 | 30 |
| 打开成功 | **29 / 30** |
| 关闭成功 | **29 / 30** |
| 失败 | **1**（第 26 轮：`Exception calling "FromHandle" … "Unrecognized error."` —— UIA 读元素时抛错，非应用崩溃） |
| 启动中位数 | 2,291 ms |
| 打开中位数 | 1,177 ms（样本 741–2,062 ms） |
| 关闭中位数 | 232 ms（29 个样本，最小 220、最大 551） |
| 每轮 Responding | 29 次采样全 True（第 26 轮无采样） |
| 关闭播放窗口后的进程数 | 每一轮都是 UI=1 / Visor=1（= 应用还在，播放窗口已关） |
| 杀进程后的残留 | 每一轮 0 |
| 跑前 → 跑后进程 | UI 1→0、Visor 1→0 |

**泄漏判定（实测）**：30 轮里每次关窗后都只剩 1 个 UI + 1 个 Visor（没有播放窗口/播放器残留成第二个进程），每轮结束杀进程后 0 残留，最终 0/0 —— 就进程维度看不到泄漏。**但**：`create-player:timeout` 的日志写明 `abandoned, no fallback, no dispose`，即这条路径上播放器 VM 没有走 dispose；进程数看不出来，不代表对象没留下。

## A7. dotnet-counters（30 s）与 UiLag / 日志告警计数

### A7.1 命令

```powershell
dotnet-counters collect -p <BiliCopilot.UI pid> --counters System.Runtime[gc-heap-size,alloc-rate,gen-0-gc-count,gen-1-gc-count,gen-2-gc-count,time-in-gc,working-set] -o counters-play-r2-20261124-215944.csv --duration 00:00:30
```

采集窗口：22:00:00–22:00:27（第 1 轮开关循环里，播放窗口已打开）。**注意**：这段里 mpv 初始化在 22:00:18 超时，所以这是「播放窗口打开、播放未成立」的采样，不是成功的播放场景。

### A7.2 原始数值（210 行 = 7 项 × 30 次采样）

| 计数器 | 最小 | 最大 | 平均 |
|---|---|---|---|
| Gen 2 GC Count (Count/sec) | 0 | 0 | 0 |
| Gen 1 GC Count (Count/sec) | 0 | 0 | 0 |
| Gen 0 GC Count (Count/sec) | 0 | 1 | 0 |
| Allocation Rate (B/sec) | 24,552 | 4,085,432 | 331,493 |
| GC Heap Size (MB) | 26.85 | 31.45 | 29.2 |
| % Time in GC | 0 | 0 | 0 |
| Working Set (MB) | 424.6 | 470.8 | 436.4 |

### A7.3 UiLag（同一窗口）

```
2026-11-24 22:00:06.632 [INF] PERF UiLag: n=198 p50=0.11ms p95=419.71ms max=874.81ms
2026-11-24 22:00:26.629 [INF] PERF UiLag: n=396 p50=0.09ms p95=0.20ms max=22.24ms
```

三次尝试期间的 UiLag（播放窗口开着、mpv 未就绪）：

```
21:56:50.241  p50=0.09ms p95=248.85ms max=701.74ms
21:57:47.646  p50=0.11ms p95=302.61ms max=756.11ms
21:58:21.690  p50=0.09ms p95=168.61ms max=622.38ms
21:58:55.634  p50=0.10ms p95=234.46ms max=689.01ms
21:59:15.630  p50=0.09ms p95=0.17ms  max=1.45ms (n=395)
```

**口径提醒**：UiLag 只在播放窗口存在的时候输出；`p50` 好看（0.09–0.11 ms）**不等于**没有异常，同窗口的 WRN 见下。

### A7.4 同一窗口的日志告警（21:59:44–22:09，共 1,010 行）

| 项 | 数量 |
|---|---|
| `[WRN]` | **363** |
| `[ERR]` | **0** |
| `处理播放器属性变更时出错`（全部是 `NullReferenceException`） | **360** |
| └ Position | 332 |
| └ CacheSpeed | 8 |
| └ PlaybackState | 7 |
| └ Duration | 3 |
| └ PlaybackRate | 3 |
| └ Volume | 3 |
| └ IsPlaybackInitialized | 2 |
| └ Title | 2 |
| 其它 WRN | 3（`mpv 播放器初始化超过 20 秒没返回` 1、`空降助手：所有服务器都没拿到片段` 1、`兴趣分析返回内容为空` 1） |

异常栈示例（21:31:43 首次出现的那批，行号即源码位置）：

```
System.NullReferenceException: Object reference not set to an instance of an object.
   at BiliCopilot.UI.ViewModels.Core.PlayerViewModel.TrackWatchedVideo() … PlayerViewModel.Methods.cs:line 704
   at BiliCopilot.UI.ViewModels.Core.PlayerViewModel.HandlePlayerPropertyChangedAsync(PropertyChangedEventArgs e) … line 746
   at … OnPlayerPropertyChanged … line 729
（同类：line 914 Duration / 927 Title / 952 IsPlaybackInitialized / 987 CacheSpeed）
```

`line 704` 是 `_watchTracker.Observe(Player.Position, Stopwatch.GetElapsedTime(0), Player.PlaybackState …)` —— `Player` 未判空即解引用。这条与 t17 的修复目标一致；本记录只报数，不判定归属。

另外：`git log -S "处理播放器属性变更时出错"` 显示这条 catch 文案来自提交 `630bfaf`（`fix(player): 播放位置回调里不再逐次碰 WinRT，修整窗「未响应」`）。

## A8. 本轮分档

### 实测

1. Debug / Release `-t:Rebuild` 均 退出码 0、0 警告 0 错误（167.5 s / 170.0 s），dll 4,495,360 B / 3,893,248 B。
2. 状态机计数三处一致：async 108、迭代器 7、`d__` 86。
3. 单测 84/84（128 ms）与 23/23（1 s），均 net11.0。
4. 冷启动 3 次 2,538 / 2,479 / 2,309 ms，中位 **2,479 ms**，全 Responding，已登录（304 元素、四个导航项）。
5. 播放器开关 30 轮：打开 29、关闭 29、失败 1（UIA 侧异常）；启动中位 2,291 ms、打开中位 1,177 ms、关闭中位 232 ms；进程无泄漏（每轮关窗后 UI=1/Visor=1，跑前 1/1 → 跑后 0/0）。
6. `dotnet-counters` 在 4 条开关的 HEAD 构建上采到 **210 行**（Gen2/Gen1 全 0、Gen0 0–1/s、alloc-rate 24.5 KB/s–4.09 MB/s、time-in-GC 0%、工作集 424.6–470.8 MB）；同一命令在第一轮 27 条开关的部署 bin 上是 0 行。
7. 播放窗口在 730–781 ms 内出现，但本轮 5 次尝试的 mpv 初始化都是 20 s 超时（`Load.MpvInit p50=20012.34ms`、`Load.MpvInit.Timeout=1`）。
8. 采集窗口内 `[WRN]` 363 条、`[ERR]` 0 条，其中 360 条是播放器属性回调的 NRE（Position 占 332）。

### 推断

1. 开关 30 轮里「关闭播放窗口后 UI=1/Visor=1、无第二个进程」说明播放窗口本身被正确销毁；但 `create-player:timeout … no dispose` 提示失败路径上 VM 未释放 —— 进程数看不出来，对象层可能残留（未做 dump/GC 计数验证）。

### 未验证

1. **播放 ≥60 秒 + 播放期 Responding**：本轮 4 次尝试全部卡在 mpv 初始化 20 s 超时，播放从未进入稳定态。21:40 那次成功（`create-player:end elapsed=11555ms`）与 21:56 之后连续失败的原因未查明。
2. **成功播放场景下的 counters / UiLag**：A7 的数值来自「播放窗口打开、mpv 超时」的窗口，不是成功播放。
3. 30 轮里那 1 次失败（第 26 轮）是 UIA 报 `Unrecognized error`，没有重试该轮，因此「30/30」这个数字没有取到。
4. mpv 初始化超时是否与 21:31–22:00 期间的反复杀进程/重开有关，本轮没有做隔离实验（例如重启 Visor、清空 LocalState 后单跑）。



---

# 附：第三轮实测（t20，2026-11-24 22:39–23:02）

> 第三轮把第二轮唯一没跑通的「播放 ≥60 秒 + 播放期 counters」补齐了。代码基线换到 t17 修复之后（`19d5d1d`）。
> 本节仍只放命令、原始输出与分档结论。

## C0. 本轮对象与环境

| 项 | 值 |
|---|---|
| HEAD | `4e7f539`（`docs(net11): 补 t19 第三轮运行时复测…`），`git status` **干净** |
| SDK | `11.0.100` |
| 隔离构建输出 | `E:\build-temp-dsh\out-verifier\` |
| 已注册包 | `Richasy.BiliCopilot.Dev_1.0.4.0_x64__g7ew4fxhv1f46` → `src\_build\x64\Debug\BiliCopilot.UI\bin` |
| 部署 bin 的 dll | 4,495,872 B，mtime **22:49:15**，sha16 `9A3358E1363706BC` |
| 部署 bin 的 runtimeconfig | **4 条**开关，`EventSource.IsSupported` **不存在**（counters 可用口径） |

**部署 bin 在我这轮里被重建过 3 次**（22:20:15 / 4,495,872 B → 22:44:02 / 4,496,384 B → 22:49:15 / 4,495,872 B；都是 4 条开关、状态机计数一致 108/7/86）。冷启动用的是 22:44:02 那份、播放与开关用 22:49:15 那份，逐项在正文里标了。我自己隔离目录的 Debug 产物是 4,495,872 B / 22:42:07 / sha16 `9772732FF2EA822A`。

## C1. 干净构建（Debug / Release 各一次）

```powershell
cd E:\Documents\deepseek-harness\default-workspace\bili-copilot-v2\src
D:\dotnet\dotnet.exe build Desktop\BiliCopilot.UI\BiliCopilot.UI.csproj -c Debug -t:Rebuild -p:Platform=x64 `
  "-p:SolutionDir=E:\Documents\deepseek-harness\default-workspace\bili-copilot-v2\src\" -p:BuildOutDir=E:/build-temp-dsh/out-verifier/
# Release 同一条，仅 -c Release
```

| 配置 | 退出码 | 警告 | 错误 | 墙钟 | `BiliCopilot.UI.dll` | runtimeconfig |
|---|---|---|---|---|---|---|
| Debug | 0 | 0 | 0 | 150.0 s（`已用时间 00:02:29.35`） | 4,495,872 B，mtime 22:42:07，sha16 `9772732F…` | 4 条 |
| Release | 0 | 0 | 0 | 167.7 s（`已用时间 00:02:46.78`） | 3,894,272 B，mtime 22:46:52，sha16 `78B7943D…` | 5 条（多 `MetadataUpdater.IsSupported`） |

两次构建都在「静默 + 锁文件」下进行（`E:\build-temp-dsh\ui-build.lock`），期间没有别的构建在跑。

## C2. 状态机计数（读元数据）

| 产物 | 大小 | async | asyncIterator | iterator | `d__` |
|---|---|---|---|---|---|
| Debug（22:42:07，本机隔离目录） | 4,495,872 B | **108** | 0 | **7** | **86** |
| Release（22:46:52，本机隔离目录） | 3,894,272 B | **108** | 0 | **7** | **86** |
| 部署 bin（22:49:15） | 4,495,872 B | **108** | 0 | **7** | **86** |

三轮（108 / 7 / 86）完全一致 —— t17 的空引用修复没有改变任何状态机数量。

## C3. 单测（独立复跑）

```powershell
cd E:\Documents\deepseek-harness\default-workspace\bili-copilot-v2
D:\dotnet\dotnet.exe test tests\BiliCopilot.CommentPoll.Tests\BiliCopilot.CommentPoll.Tests.csproj -p:BuildOutDir=E:/build-temp-dsh/out-verifier/
D:\dotnet\dotnet.exe test tests\BiliCopilot.Diagnostics.Tests\BiliCopilot.Diagnostics.Tests.csproj -p:BuildOutDir=E:/build-temp-dsh/out-verifier/
```

```
已通过! - 失败:     0，通过:    84，已跳过:     0，总计:    84，持续时间: 76 ms - BiliCopilot.CommentPoll.Tests.dll (net11.0)
已通过! - 失败:     0，通过:    23，已跳过:     0，总计:    23，持续时间: 725 ms - BiliCopilot.Diagnostics.Tests.dll (net11.0)
```

## C4. 冷启动 3 次

```powershell
pwsh -NoProfile -File E:\build-temp-dsh\verifier\coldstart.ps1 -Runs 3
```

| 次 | 出窗口 (ms) | 进程出现 (ms) | Responding | WorkingSet | UIA 元素 |
|---|---|---|---|---|---|
| 1 | 1,780 | 173 | True | 488.3 MB | 331 |
| 2 | 3,021 | 121 | True | 415.2 MB | 238 |
| 3 | 2,858 | 131 | True | 434.4 MB | 331 |

**中位数 = 2,858 ms**（排序 1,780 / 2,858 / 3,021）。三次主窗口标题「哔哩助理」、UIA 命中 `搜索|动态|直播|设置`、非登录页。跑前有一枚残留 Visor（procsBefore Visor=1），被脚本 Stop-App 一并清掉。

## C5. 播放 ≥60 秒（本轮跑通）

```powershell
pwsh -NoProfile -File E:\build-temp-dsh\verifier\r2-play3.ps1 -PlaySeconds 75 -CountersSeconds 30
```

第 1 次尝试 `player-init-timeout`，第 2 次成功（脚本自带重启重试）。成功那次的原始记录：

```
startup 2325 ms → 播放窗口 1228 ms → 播放器初始化 ready 4140 ms
lifecycle: 22:51:40.400 create-mpv-client:end elapsed=63ms
           22:51:41.241 create-player:start
           22:51:44.565 create-player:end elapsed=3321ms mpv=3D3DB3D
播放 75 s，15 次 Responding 采样全 True，工作集 428.4 → 555.8 MB
关闭播放窗口 342 ms
propWarnCount = 0（t17 修复后，播放器属性回调不再刷 NRE）
```

**对照第二轮**：同一脚本在 21:56–22:10 连续 5 次 mpv 初始化 20 s 超时；本轮第 2 次就 3.3 s 起播，第 1 次仍是超时 —— 说明这台机上 mpv 初始化本身是**间歇性**的（不是稳定的回归），三次会话里成功/失败都出现过。

### C5.1 一次被外部打断的会话（记录在案，不计入结果）

22:47:55 那轮第一次尝试其实已经 `create-player:end elapsed=13263ms` 起播成功，但 22:49:28 有**另一个应用进程**被启动（日志「启动读取播放增强记忆…」+「导航项被点击：MomentPage」22:49:32 + 新视频 aid 117668950580219 于 22:49:39 起播），我的会话进程随之消失（Responding 从 t≈20 s 起全 false，counters 只采到 113 行）。不是我的脚本所为（一轮只启一次应用）。该轮作废，全部数据丢弃。

同一时段的崩溃证据（Windows 事件日志，实测）：

```
22:46:48 | Application Error 1000 | BiliCopilot.UI.exe 1.0.0.0 | 出错模块 coreclr.dll 11.0.26.53018 |
          异常 0xc0000005，偏移 0x0000000000384f74
22:46:54 | Windows Error Reporting 1001 ×6 | 其中 Microsoft.UI.Xaml.Controls.dll 3.3.0.0 0xc0000005 @0x9dbd、
          combase.dll 10.0.29712.1000 0x80004005
```

（应用自己的 `crash-2026-11-24.txt` 只有 04:06:35 一条，是 `OverlayTransportControls.OnProgressValueChanged … xaml.cs:158` 的 NRE，与 22:46 这次无关 —— 也就是说 22:46 是硬崩，没走应用内异常处理。）

## C6. dotnet-counters（30 s，播放中）

```powershell
dotnet-counters collect -p <pid> --counters System.Runtime[gc-heap-size,alloc-rate,gen-0-gc-count,gen-1-gc-count,gen-2-gc-count,time-in-gc,working-set] -o counters-play-r2-20261124-225048.csv --duration 00:00:30
```

采集窗口 **22:51:45–22:52:14**（`create-player:end` 22:51:44 之后立即开始，落在 75 秒播放区间内）。**210 行 = 7 项 × 30 次**：

| 计数器 | 最小 | 最大 | 平均 |
|---|---|---|---|
| GC Heap Size (MB) | 15.08 | 64.16 | 43.3 |
| Allocation Rate (B/sec) | 80,760 | 11,102,464 | 1,799,914 |
| Working Set (MB) | 545.2 | 617.4 | 577.3 |
| % Time in GC | 0 | 5 | 4.8 |
| Gen 0 / Gen 1 / Gen 2 GC Count (Count/1 sec) | 11 / 10 / 10 | 60 / 60 / 60 | 见下 |

**GC 次数这一列的口径问题（实测 + 推断分列）**：三项代数在 `Rate` 列上都读到「首样本 11/10/10，其后恒定 60」，三列完全相同；`Rate` 计数器的 `DisplayRateTimeScale` 在运行时是 1 分钟（`RuntimeEventSource` 里 `IncrementingPollingCounter("gen-2-gc-count", …) { DisplayRateTimeScale = 00:01:00 }`），所以「60」不能直接读成「每秒 60 次」。**我无法把 60 还原成各代真实次数**，也不认为三列相等是真实分布 —— 标为「未验证」。可用的确定性指标是堆大小、分配速率、工作集与 % Time in GC 四项（Metric 类型，无缩放歧义）。

同一轮另有一份 `r2-toggle-final` 第 1 轮产出的 counters（211 行，`counters-play-r2-20261124-225325.csv`），与上面同量级。

## C7. UiLag（播放期，同一日志窗口）

```
2026-11-24 22:51:48.865 [INF] PERF UiLag: n=195 p50=0.14ms p95=981.15ms max=1457.37ms   ← 进播放页那一下
2026-11-24 22:51:58.866 [INF] PERF UiLag: n=198 p50=0.12ms p95=1.22ms  max=9.84ms
2026-11-24 22:52:18.867 [INF] PERF UiLag: n=394 p50=0.11ms p95=0.70ms  max=67.75ms
2026-11-24 22:52:28.867 [INF] PERF UiLag: n=198 p50=0.09ms p95=0.22ms  max=1.37ms
2026-11-24 22:52:38.867 [INF] PERF UiLag: n=198 p50=0.08ms p95=0.17ms  max=0.34ms
2026-11-24 22:52:48.868 [INF] PERF UiLag: n=198 p50=0.09ms p95=0.22ms  max=0.35ms
2026-11-24 22:52:58.869 [INF] PERF UiLag: n=198 p50=0.09ms p95=0.20ms  max=0.37ms
```

稳定播放期（22:51:58 起 6 个窗口）：**p50 0.08–0.12 ms、p95 0.17–1.22 ms**、max ≤ 67.75 ms；第一个窗口 p95 981.15 ms 是进入播放页那一下（`create-player:end` 22:51:44 之后 4 秒内），不算播放期稳态。

**日志告警（与 UiLag 分开报）**：该播放窗口内 `处理播放器属性变更时出错` = **0 条**，`[ERR]` = 0 条（第二轮同一路径是 360 条 NRE）。

## C8. 播放器开关 30 轮

```powershell
pwsh -NoProfile -File E:\build-temp-dsh\verifier\r2-toggle-final.ps1 -Rounds 30 -CountersSeconds 30
```

每轮：杀进程 → 冷启动 → 点首页最大卡片 → 等播放窗口（openMs）→ 关播放窗口（closeMs）→ 记录进程数 → 杀进程记录残留。

| 指标 | 值 |
|---|---|
| 轮数 | 30 |
| 打开成功 | **30 / 30** |
| 关闭成功 | **30 / 30** |
| 失败 | **0** |
| 启动中位数 | 2,286.5 ms |
| 打开中位数 | 1,626 ms（735–2,090） |
| 关闭中位数 | 228.5 ms（222–465） |
| Responding | 30 次采样全 True |
| 关窗后进程数 | 每一轮 UI=1 / Visor=1 |
| 杀进程后残留 | 每一轮 0 |
| 跑前 → 跑后 | UI 0→0、Visor 1→0 |

无进程泄漏（进程维度）。

## C9. 本轮分档

### 实测
1. Debug / Release `-t:Rebuild`：退出码 0、0 警告 0 错误（150.0 s / 167.7 s），dll 4,495,872 B / 3,894,272 B，4 / 5 条 runtimeconfig 开关。
2. 状态机三处一致：async 108、迭代器 7、`d__` 86。
3. 单测 84/84（76 ms）与 23/23（725 ms），均 net11.0。
4. 冷启动 3 次 1,780 / 3,021 / 2,858 ms，中位 **2,858 ms**，全 Responding、已登录。
5. 播放 75 s：15/15 Responding True，工作集 428.4→555.8 MB，播放器初始化 ready 4,140 ms（lifecycle `create-player:end 3321ms`），关窗 342 ms。
6. 播放期 UiLag：p50 0.08–0.12 ms、p95 0.17–1.22 ms；同窗口 NRE 0 条、ERR 0 条。
7. counters（播放中 30 s，210 行）：堆 15.08–64.16 MB、分配速率 80,760–11,102,464 B/s、工作集 545.2–617.4 MB、% Time in GC 0→5。
8. 开关 30 轮：30/30 打开、30/30 关闭、0 失败；启动中位 2,286.5 ms、打开中位 1,626 ms、关闭中位 228.5 ms；每轮关窗后 UI=1/Visor=1、残留 0、跑前 0/1 → 跑后 0/0。
9. 22:46:48 与 22:46:54 的事件日志里有一次 hard crash（coreclr.dll 0xc0000005），发生在我这轮开始之前，属于另一个会话。
10. mpv 初始化在三次会话里成功过（21:40 11.5 s、22:51 3.3 s）、也超时过（21:56–22:10 五次、22:50 一次）—— 间歇性。

### 推断
1. 播放期 Gen0/Gen1/Gen2 三列读数相同且恒定 60，可能是 dotnet-counters 对 1 分钟 `DisplayRateTimeScale` 的换算或 .NET 11 Meter 维度未拆分导致的；未验证，不能当真实分代计数用。
2. 第一轮那句「UiLag 只能说明 UI 线程不卡」在这里再次成立：本轮 NRE 计数为 0，是 t17 的守卫生效，不是「本来就没有」。

### 未验证
1. 22:46 那次 coreclr 0xc0000005 的触发原因（发生在别人会话里，我没有 dump）。
2. mpv 初始化超时的根因（间歇性，本轮未能稳定复现条件）。**后续**：本轮构建之后出现的提交 `70237e3` 声称查清「等的是 PlayAsync 开播、媒体地址 403 会伪装成超时」——该提交不在本轮构建（HEAD 4e7f539）里，我未验证。
3. 各代 GC 真实次数（见推断 1）。
4. 播放期内存从 428 MB 涨到 556 MB 是否为缓存增长/泄漏 —— 只跑了 75 秒，没有长跑与 GC 后对照。


