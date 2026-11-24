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

