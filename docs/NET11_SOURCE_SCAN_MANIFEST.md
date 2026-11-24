# .NET 11 全仓库源码扫描清单（逐文件）

> 分支 `exp/net11-optimization` · 仓库 `bili-copilot-v2` · 扫描日期 2026-11-24
> 配套文档：[NET11_HOTSPOTS.md](NET11_HOTSPOTS.md)（P0/P1/P2/P3 热点评级与调用链）、[NET11_BUILD_AUDIT.md](NET11_BUILD_AUDIT.md)（构建配置实测）、[NET11_RESEARCH.md](NET11_RESEARCH.md)（.NET 11 特性清单）
> 本文只读仓库、不改任何源码；分析脚本全部落在仓库外 `_tmp/scan/`。

## 1. 口径与总账

> **扫描快照**：语义数据于 2026-11-24 19:40–19:50 之间取得。在此期间团队并行提交了 4 个改动（`4e3566b` 修 Release 注释红灯、`d781292` 拆掉 UI 工程常开的 `PublishAot`、`f7fa0e3` 给 Visor.Models 补 runtime-async、`5fe32e5` 三个测试工程升 net11.0）。它们**只改构建属性与 1 行文档注释，没有改任何业务源码**，因此本文 735 条语义命中与逐文件清单在当前 HEAD 上仍然成立；受影响的只有"测试工程 TFM"与"PublishAot 开关"这两处叙述，已在正文按当前树标注。

| 口径 | 数量 | 说明 |
|---|---|---|
| 参与编译的 .cs 文件（8 个编译单元） | 1268 | 5 个 src 工程 + 3 个测试工程，取 MSBuild 真实 csc 命令行里的源文件列表（已剔除混入的 .dll 参数） |
| 其中仓库内手写源码 | 769 | 排除 obj/ 下的 XAML/源生成产物；含测试工程双宿主编译的 8 个链接文件（同一文件在清单里出现两次，是刻意的） |
| 其中仓库内生成代码 | 494 | *.g.cs / *.g.i.cs / XamlTypeInfo.g.cs / AssemblyInfo 等，**单列不修改** |
| 其中包内/外部 .cs | 5 | 编译单元里由 NuGet 包带入的源码（如 Microsoft.NET.Test.Sdk 的 Program.cs），不属于本仓库（§6.4） |
| 手写源码行数 | 85,166 | Roslyn 语法树行数 |
| 有命中的文件 | 189 | 命中 = 至少一条语义检查 |
| 语义命中总数 | 735 | 20 类检查，见 §2 |
| 命中中落在生成代码里的 | 0 | 全部排除（生成代码只看不改） |
| XAML 标记文件 | 246 | 单列清点（§7），未做语义分析，原因见 §8 |
| 已剔除的误报 | 24 | 测试工程里的 MISSING_CT（测试方法本来就不需要取消令牌），清单与统计均已剔除 |

## 2. 扫描方法（为什么不是正则）

1. **重建真实编译单元**：从 `dotnet build -v:detailed` 日志里抓出每个程序集**真实的 csc 命令行**（`/reference:` 338 条、`/define:`、`/langversion:`、源文件清单），用 Roslyn 按同一份参数重建 `CSharpCompilation`。这样语义符号与编译器看到的一致，不靠文本猜测。
2. **20 类语义检查**：每条命中都要求 `SemanticModel` 能解析出符号/类型（例如"这是 Task 上的 `.Result`"、"这是 `System.Linq.Enumerable.ToList`"、"这个 lambda 的 ConvertedType 委托返回 void"），无法解析的一律不报。
3. **编译器与分析器诊断**：另跑两轮真实构建——(a) `NoWarn` 清空（只留 XAML 必需的 `WMC1510`）拿到 1982×CS1591 / 884×MVVMTK0045 / 208×IDE0005 / 10×IL2026 等；(b) 命令行 `-p:EnableNETAnalyzers=true` 押过 NuGet 包里的 `DisableNETAnalyzersForNuGetPackage.props`，拿到 342 条 CA 诊断（CA1873 300 条为主）。**这一条同时暴露了仓库分析器治理是空转的**：`Directory.Build.props` 里 `EnableNETAnalyzers=true` / `AnalysisMode=AllEnabledByDefault` / `AnalysisLevel=latest` 三项求值结果 `EnableNETAnalyzers=false`，CA 规则一条都不跑（详见 §5 与 [NET11_BUILD_AUDIT.md](NET11_BUILD_AUDIT.md) §12）。
4. **调用索引**：同一遍扫描顺手记录 6,088 条调用点（项目内调用 + 关注的外部 API），用于在热点文档里给"谁调用了它"的调用链。

复现命令见仓库外 `_tmp/scan/`：`extract-specs.ps1`（日志→编译规格）、`RoslynScan.csproj`/`Program.cs`（分析器本体，直连 SDK 自带的 `Microsoft.CodeAnalysis*.dll`）、报告数据 `out2/{findings,callsites,filestats,diag}.jsonl`。

## 3. 列含义

| 列 | 含义 |
|---|---|
| 项目 | 所属编译单元（`BiliCopilot.UI` / `UI.Models` / `Visor` / `Visor.Models` / `ResourceGenerator` / 三个测试工程） |
| 类型 | 按路径归类：ViewModel / VM-属性 / VM-方法 / Service / Service-直播 / Reactor 组件 / XAML code-behind / 控件 / 页面 / 窗口 / Toolkit / Converter / Extension / Model / Provider / 测试 / 生成 |
| 扫描状态 | `已扫·命中N` / `已扫·无命中` / `已扫·生成`（生成代码只清点不分析） / `已扫·双宿主`（同一文件被 UI 与测试工程各编一次） |
| 问题 | 命中的检查项与次数，如 `ASYNC_VOID×2, LINQ_IN_LOOP×1` |
| 适用优化 | 与命中项对应的改造动作（与 .NET 11 的相关性标注在括号里，如 runtime-async 不覆盖 async void） |
| 需改 | `是·P0` / `是·P1` / `评估·P2` / `否·P3`，由最严重命中级决定 |
| 原因 | 一句话归纳该文件为什么要动（或为什么不动） |

## 4. 按工程汇总

| 项目 | 手写文件 | 行数 | 有命中文件 | 命中数 | 需改(是) | 需评估 | 说明 |
|---|---|---|---|---|---|---|---|
| BiliCopilot.CommentPoll.Tests | 12 | 2,279 | 4 | 25 | 1 | 2 | 测试工程（net11.0，5fe32e5 升级），链接了 UI 的源码 |
| BiliCopilot.Diagnostics.Tests | 9 | 1,911 | 2 | 3 | 1 | 1 | 测试工程（net11.0，5fe32e5 升级），链接了 UI 的源码 |
| BiliCopilot.Moment.Tests | 3 | 205 | 0 | 0 | 0 | 0 | 测试工程（net11.0，5fe32e5 升级），链接了 UI 的源码 |
| BiliCopilot.UI | 696 | 78,415 | 181 | 698 | 82 | 78 | 主战场：WinUI3 + Reactor 混合，状态机/分配/异步形态都在这里 |
| BiliCopilot.UI.Models | 42 | 1,563 | 0 | 0 | 0 | 0 | netstandard2.0：本工程永远拿不到 runtime-async（见 t2 §12-7） |
| BiliCopilot.UI.ResourceGenerator | 1 | 93 | 0 | 0 | 0 | 0 | Roslyn 源生成器（netstandard2.0） |
| BiliCopilot.Visor | 5 | 615 | 2 | 9 | 1 | 0 | 独立管道进程，Native AOT 发布 |
| BiliCopilot.Visor.Models | 1 | 85 | 0 | 0 | 0 | 0 | net11.0；runtime-async 已于 f7fa0e3 补上 |

## 5. 命中项分布（20 类检查）

| 检查 | 命中 | 涉及文件 | 生成代码 | 判定 |
|---|---|---|---|---|
| CONFIGUREAWAIT | 199 | 37 | 0 | P3 |
| LINQ_MATERIALIZE | 117 | 63 | 0 | P3 |
| MISSING_CT | 90 | 50 | 0 | P2 |
| ASYNC_VOID | 79 | 55 | 0 | P1 |
| LINQ_IN_LOOP | 59 | 37 | 0 | P2 |
| HTTP_BUFFERED | 53 | 24 | 0 | P2 |
| ASYNC_LAMBDA_VOID | 29 | 24 | 0 | P2 |
| NEW_HTTPCLIENT | 28 | 22 | 0 | P1 |
| TASK_RUN | 20 | 10 | 0 | P2 |
| IMAGE_ALLOC | 16 | 11 | 0 | P2 |
| JSON_REFLECTION | 11 | 6 | 0 | P1 |
| LOOP_DELAY | 9 | 9 | 0 | P3 |
| SYNC_OVER_ASYNC | 8 | 3 | 0 | P0 |
| CLOSURE_IN_LOOP | 4 | 4 | 0 | P2 |
| BLOCKING_SLEEP | 4 | 4 | 0 | P1 |
| IMAGE_IO | 3 | 3 | 0 | P2 |
| ASYNC_NO_AWAIT | 2 | 2 | 0 | P2 |
| STRING_CONCAT_LOOP | 2 | 1 | 0 | P1 |
| JSONDOC_NO_USING | 1 | 1 | 0 | P2 |
| DISCARDED_TASK | 1 | 1 | 0 | P1 |

> 判定口径：P0/P1/P2/P3 的完整依据、调用链与复现路径见 [NET11_HOTSPOTS.md](NET11_HOTSPOTS.md)。
>
> `JSON_REFLECTION` 11 条里有 7 条是误报（调用点已传 `JsonTypeInfo`，误报原因是重建编译单元没跑 STJ 源生成器，`GlobalSerializeContext.Default` 在语义模型里成了错误类型）；逐点核对后**真反射是 4 条**：`Services/AppStateStore.cs:107`、`:136`、`Services/LiveEmoteStore.cs:142`、`:165`。这 4 条在当前的 `JsonSerializer.IsReflectionEnabledByDefault=false` 会抛异常（实测），详见热点文档 P0-2。

## 6. 逐文件清单

### 6.1 有命中的手写文件（完整列）

#### BiliCopilot.CommentPoll.Tests

| 路径 | 类型 | 扫描状态 | 问题 | 适用优化 | 需改 | 原因 |
|---|---|---|---|---|---|---|
| src\Desktop\BiliCopilot.UI\Services\CommentVote\CommentVoteService.cs | Service-评论·测试宿主 | 已扫·命中22 | CONFIGUREAWAIT×12, HTTP_BUFFERED×3, LINQ_IN_LOOP×2, LINQ_MATERIALIZE×3, NEW_HTTPCLIENT×2 | UI 层可不用；库/后台层保留（现状多为正确用法）；流式读取（ReadAsStreamAsync / JsonDocument 流式）；循环内改索引/Span 遍历，或预先物化 | 是·P1 | ConfigureAwait 使用、整串缓冲、循环内 LINQ 分配、LINQ 物化、热路径新建 HttpClient |
| tests\BiliCopilot.CommentPoll.Tests\CommentVoteGeometryTests.cs | 测试 | 已扫·命中1 | LINQ_IN_LOOP×1 | 循环内改索引/Span 遍历，或预先物化 | 评估·P2 | 循环内 LINQ 分配 |
| tests\BiliCopilot.CommentPoll.Tests\CommentVoteServiceTests.cs | 测试 | 已扫·命中1 | LOOP_DELAY×1 | 轮询节奏点：确认可取消、必要时改事件驱动 | 否·P3 | 循环轮询 |
| tests\BiliCopilot.CommentPoll.Tests\FakeHttpHandler.cs | 测试辅助 | 已扫·命中1 | HTTP_BUFFERED×1 | 流式读取（ReadAsStreamAsync / JsonDocument 流式） | 评估·P2 | 整串缓冲 |

#### BiliCopilot.Diagnostics.Tests

| 路径 | 类型 | 扫描状态 | 问题 | 适用优化 | 需改 | 原因 |
|---|---|---|---|---|---|---|
| src\Desktop\BiliCopilot.UI\Services\Diagnostics\PlaybackStallDump.cs | Service-诊断 | 已扫·命中2 | LINQ_IN_LOOP×1, LINQ_MATERIALIZE×1 | 循环内改索引/Span 遍历，或预先物化；评估是否必须 ToList/ToArray（可 IEnumerable 直传） | 评估·P2 | 循环内 LINQ 分配、LINQ 物化 |
| src\Desktop\BiliCopilot.UI\Services\Diagnostics\PlaybackStallWatch.cs | Service-诊断 | 已扫·命中1 | BLOCKING_SLEEP×1 | Thread.Sleep → await Task.Delay(token) | 是·P1 | 阻塞线程 |

#### BiliCopilot.UI

| 路径 | 类型 | 扫描状态 | 问题 | 适用优化 | 需改 | 原因 |
|---|---|---|---|---|---|---|
| src\Desktop\BiliCopilot.UI\App.xaml.cs | 启动 | 已扫·命中1 | JSON_REFLECTION×1 | 扩 GlobalSerializeContext 源生成，消掉反射序列化 | 是·P1 | JSON 走反射（与裁剪开关冲突） |
| src\Desktop\BiliCopilot.UI\Controls\AI\CustomPromptDialog.xaml.cs | XAML code-behind | 已扫·命中2 | ASYNC_VOID×2 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态） | 是·P1 | async void 形态 |
| src\Desktop\BiliCopilot.UI\Controls\Article\ArticlePartitionMainBody.xaml.cs | XAML code-behind | 已扫·命中1 | ASYNC_VOID×1 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态） | 是·P1 | async void 形态 |
| src\Desktop\BiliCopilot.UI\Controls\Article\ArticleReader.xaml.cs | XAML code-behind | 已扫·命中5 | ASYNC_VOID×3, JSONDOC_NO_USING×1, MISSING_CT×1 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态）；using/Dispose JsonDocument；补 CancellationToken 形参并透传 | 是·P1 | async void 形态、JsonDocument 未 using、缺少取消令牌 |
| src\Desktop\BiliCopilot.UI\Controls\Comment\CommentDetailPanel.xaml.cs | XAML code-behind | 已扫·命中1 | ASYNC_VOID×1 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态） | 是·P1 | async void 形态 |
| src\Desktop\BiliCopilot.UI\Controls\Comment\CommentMainPanel.xaml.cs | XAML code-behind | 已扫·命中1 | ASYNC_VOID×1 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态） | 是·P1 | async void 形态 |
| src\Desktop\BiliCopilot.UI\Controls\Comment\CommentTextBox.xaml.cs | XAML code-behind | 已扫·命中3 | ASYNC_VOID×2, NEW_HTTPCLIENT×1 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态）；复用静态 HttpClient（SocketsHttpHandler） | 是·P1 | async void 形态、热路径新建 HttpClient |
| src\Desktop\BiliCopilot.UI\Controls\Common\AiInterestBanner.xaml.cs | XAML code-behind | 已扫·命中1 | MISSING_CT×1 | 补 CancellationToken 形参并透传 | 评估·P2 | 缺少取消令牌 |
| src\Desktop\BiliCopilot.UI\Controls\Components\EmotePanel.xaml.cs | XAML code-behind | 已扫·命中1 | ASYNC_VOID×1 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态） | 是·P1 | async void 形态 |
| src\Desktop\BiliCopilot.UI\Controls\Components\EmoteTextBlock.xaml.cs | XAML code-behind | 已扫·命中2 | IMAGE_ALLOC×1, LINQ_MATERIALIZE×1 | 先设 DecodePixelWidth 再取源；走 SmallImageCache；评估是否必须 ToList/ToArray（可 IEnumerable 直传） | 评估·P2 | 图片解码分配、LINQ 物化 |
| src\Desktop\BiliCopilot.UI\Controls\Components\MomentCardControl\AnimatedRainbowBorder.cs | 控件 | 已扫·命中1 | LINQ_MATERIALIZE×1 | 评估是否必须 ToList/ToArray（可 IEnumerable 直传） | 否·P3 | LINQ 物化 |
| src\Desktop\BiliCopilot.UI\Controls\Core\ChapterPanel.xaml.cs | XAML code-behind | 已扫·命中1 | ASYNC_VOID×1 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态） | 是·P1 | async void 形态 |
| src\Desktop\BiliCopilot.UI\Controls\Core\OverlayTransportControls.xaml.cs | XAML code-behind | 已扫·命中7 | ASYNC_VOID×7 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态） | 是·P1 | async void 形态 |
| src\Desktop\BiliCopilot.UI\Controls\Core\PlayerInteractivePanel.xaml.cs | XAML code-behind | 已扫·命中6 | ASYNC_LAMBDA_VOID×1, ASYNC_VOID×5 | async lambda → 显式 async Task 方法；async void → async Task；异常自兜底（runtime-async 不覆盖此形态） | 是·P1 | async void lambda、async void 形态 |
| src\Desktop\BiliCopilot.UI\Controls\Core\PlayerOverlay.xaml.cs | XAML code-behind | 已扫·命中2 | ASYNC_LAMBDA_VOID×1, CLOSURE_IN_LOOP×1 | async lambda → 显式 async Task 方法；循环内提局部变量，避免闭包分配与捕获错位 | 评估·P2 | async void lambda、闭包捕获循环变量 |
| src\Desktop\BiliCopilot.UI\Controls\Core\WebVideoPlayer.xaml.cs | XAML code-behind | 已扫·命中1 | MISSING_CT×1 | 补 CancellationToken 形参并透传 | 评估·P2 | 缺少取消令牌 |
| src\Desktop\BiliCopilot.UI\Controls\Danmaku\VideoDanmakuPanel.xaml.cs | Service-弹幕 | 已扫·命中2 | ASYNC_LAMBDA_VOID×1, ASYNC_VOID×1 | async lambda → 显式 async Task 方法；async void → async Task；异常自兜底（runtime-async 不覆盖此形态） | 是·P1 | async void lambda、async void 形态 |
| src\Desktop\BiliCopilot.UI\Controls\Dialogs\ForwardDialog.cs | 控件 | 已扫·命中2 | IMAGE_ALLOC×1, MISSING_CT×1 | 先设 DecodePixelWidth 再取源；走 SmallImageCache；补 CancellationToken 形参并透传 | 评估·P2 | 图片解码分配、缺少取消令牌 |
| src\Desktop\BiliCopilot.UI\Controls\Favorites\PgcFavoriteBody.xaml.cs | XAML code-behind | 已扫·命中1 | ASYNC_VOID×1 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态） | 是·P1 | async void 形态 |
| src\Desktop\BiliCopilot.UI\Controls\Favorites\PgcFavoriteHeader.xaml.cs | XAML code-behind | 已扫·命中1 | ASYNC_VOID×1 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态） | 是·P1 | async void 形态 |
| src\Desktop\BiliCopilot.UI\Controls\Favorites\UgcFavoriteBody.xaml.cs | XAML code-behind | 已扫·命中1 | ASYNC_VOID×1 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态） | 是·P1 | async void 形态 |
| src\Desktop\BiliCopilot.UI\Controls\Favorites\VideoFavoriteBody.xaml.cs | XAML code-behind | 已扫·命中1 | ASYNC_VOID×1 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态） | 是·P1 | async void 形态 |
| src\Desktop\BiliCopilot.UI\Controls\History\ArticleHistorySection.xaml.cs | XAML code-behind | 已扫·命中1 | ASYNC_VOID×1 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态） | 是·P1 | async void 形态 |
| src\Desktop\BiliCopilot.UI\Controls\History\HistoryVideoSearchSection.xaml.cs | XAML code-behind | 已扫·命中1 | ASYNC_VOID×1 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态） | 是·P1 | async void 形态 |
| src\Desktop\BiliCopilot.UI\Controls\History\LiveHistorySection.xaml.cs | XAML code-behind | 已扫·命中1 | ASYNC_VOID×1 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态） | 是·P1 | async void 形态 |
| src\Desktop\BiliCopilot.UI\Controls\History\VideoHistorySection.xaml.cs | XAML code-behind | 已扫·命中1 | ASYNC_VOID×1 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态） | 是·P1 | async void 形态 |
| src\Desktop\BiliCopilot.UI\Controls\IncrementVerticalScrollControl.xaml.cs | XAML code-behind | 已扫·命中1 | MISSING_CT×1 | 补 CancellationToken 形参并透传 | 评估·P2 | 缺少取消令牌 |
| src\Desktop\BiliCopilot.UI\Controls\LivePartition\LiveRecommendMainBody.xaml.cs | XAML code-behind | 已扫·命中1 | ASYNC_VOID×1 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态） | 是·P1 | async void 形态 |
| src\Desktop\BiliCopilot.UI\Controls\LivePartition\LiveSubPartitionMainBody.xaml.cs | XAML code-behind | 已扫·命中1 | ASYNC_VOID×1 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态） | 是·P1 | async void 形态 |
| src\Desktop\BiliCopilot.UI\Controls\Markdown\DefaultSVGRenderer.cs | 控件 | 已扫·命中2 | IMAGE_IO×1, MISSING_CT×1 | 解码尺寸/流式，避免整图缓冲；补 CancellationToken 形参并透传 | 评估·P2 | 图片 IO/解码、缺少取消令牌 |
| src\Desktop\BiliCopilot.UI\Controls\Markdown\TextElements\MyImage.cs | 控件 | 已扫·命中5 | ASYNC_VOID×1, HTTP_BUFFERED×1, IMAGE_ALLOC×1, IMAGE_IO×1, NEW_HTTPCLIENT×1 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态）；流式读取（ReadAsStreamAsync / JsonDocument 流式）；先设 DecodePixelWidth 再取源；走 SmallImageCache | 是·P1 | async void 形态、整串缓冲、图片解码分配、图片 IO/解码、热路径新建 HttpClient |
| src\Desktop\BiliCopilot.UI\Controls\Message\MessagePageSideBody.xaml.cs | XAML code-behind | 已扫·命中1 | ASYNC_VOID×1 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态） | 是·P1 | async void 形态 |
| src\Desktop\BiliCopilot.UI\Controls\Message\NotifyMessageBody.xaml.cs | XAML code-behind | 已扫·命中1 | ASYNC_VOID×1 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态） | 是·P1 | async void 形态 |
| src\Desktop\BiliCopilot.UI\Controls\Moment\ComprehensiveMomentSpaceControl.xaml.cs | XAML code-behind | 已扫·命中1 | ASYNC_VOID×1 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态） | 是·P1 | async void 形态 |
| src\Desktop\BiliCopilot.UI\Controls\Moment\LiveMomentSectionDetailControl.xaml.cs | XAML code-behind | 已扫·命中1 | ASYNC_VOID×1 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态） | 是·P1 | async void 形态 |
| src\Desktop\BiliCopilot.UI\Controls\Moment\UserSpaceVideoSearchSection.xaml.cs | XAML code-behind | 已扫·命中1 | ASYNC_VOID×1 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态） | 是·P1 | async void 形态 |
| src\Desktop\BiliCopilot.UI\Controls\Moment\VideoMomentSectionDetailControl.xaml.cs | XAML code-behind | 已扫·命中1 | ASYNC_VOID×1 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态） | 是·P1 | async void 形态 |
| src\Desktop\BiliCopilot.UI\Controls\Moment\VideoMomentSpaceControl.xaml.cs | XAML code-behind | 已扫·命中1 | ASYNC_VOID×1 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态） | 是·P1 | async void 形态 |
| src\Desktop\BiliCopilot.UI\Controls\Pgc\EntertainmentIndexMainControl.xaml.cs | XAML code-behind | 已扫·命中1 | ASYNC_VOID×1 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态） | 是·P1 | async void 形态 |
| src\Desktop\BiliCopilot.UI\Controls\Pinner.xaml.cs | XAML code-behind | 已扫·命中1 | ASYNC_VOID×1 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态） | 是·P1 | async void 形态 |
| src\Desktop\BiliCopilot.UI\Controls\Player\CdnIndicatorControl.xaml.cs | XAML code-behind | 已扫·命中1 | ASYNC_VOID×1 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态） | 是·P1 | async void 形态 |
| src\Desktop\BiliCopilot.UI\Controls\Player\LiveSidePanelHost.xaml.cs | XAML code-behind | 已扫·命中1 | CLOSURE_IN_LOOP×1 | 循环内提局部变量，避免闭包分配与捕获错位 | 评估·P2 | 闭包捕获循环变量 |
| src\Desktop\BiliCopilot.UI\Controls\Player\PgcEpisodesSection.xaml.cs | XAML code-behind | 已扫·命中2 | ASYNC_VOID×2 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态） | 是·P1 | async void 形态 |
| src\Desktop\BiliCopilot.UI\Controls\Player\PlaybackEndPanel.xaml.cs | XAML code-behind | 已扫·命中1 | ASYNC_VOID×1 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态） | 是·P1 | async void 形态 |
| src\Desktop\BiliCopilot.UI\Controls\Popular\PopularMainBody.xaml.cs | XAML code-behind | 已扫·命中1 | ASYNC_VOID×1 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态） | 是·P1 | async void 形态 |
| src\Desktop\BiliCopilot.UI\Controls\Search\ArticleSectionDetailControl.xaml.cs | XAML code-behind | 已扫·命中1 | ASYNC_VOID×1 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态） | 是·P1 | async void 形态 |
| src\Desktop\BiliCopilot.UI\Controls\Search\LiveSectionDetailControl.xaml.cs | XAML code-behind | 已扫·命中1 | ASYNC_VOID×1 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态） | 是·P1 | async void 形态 |
| src\Desktop\BiliCopilot.UI\Controls\Search\PgcSectionDetailControl.xaml.cs | XAML code-behind | 已扫·命中1 | ASYNC_VOID×1 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态） | 是·P1 | async void 形态 |
| src\Desktop\BiliCopilot.UI\Controls\Search\UserSectionDetailControl.xaml.cs | XAML code-behind | 已扫·命中1 | ASYNC_VOID×1 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态） | 是·P1 | async void 形态 |
| src\Desktop\BiliCopilot.UI\Controls\Search\VideoSectionDetailControl.xaml.cs | XAML code-behind | 已扫·命中1 | ASYNC_VOID×1 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态） | 是·P1 | async void 形态 |
| src\Desktop\BiliCopilot.UI\Controls\Settings\LoggerSettingControl.xaml.cs | XAML code-behind | 已扫·命中1 | ASYNC_VOID×1 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态） | 是·P1 | async void 形态 |
| src\Desktop\BiliCopilot.UI\Controls\Settings\MpvExtraSettingControl.xaml.cs | XAML code-behind | 已扫·命中1 | ASYNC_VOID×1 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态） | 是·P1 | async void 形态 |
| src\Desktop\BiliCopilot.UI\Controls\Users\FansBody.xaml.cs | XAML code-behind | 已扫·命中1 | ASYNC_VOID×1 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态） | 是·P1 | async void 形态 |
| src\Desktop\BiliCopilot.UI\Controls\Users\FollowsMainBody.xaml.cs | XAML code-behind | 已扫·命中1 | ASYNC_VOID×1 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态） | 是·P1 | async void 形态 |
| src\Desktop\BiliCopilot.UI\Controls\VideoPartition\VideoPartitionMainBody.xaml.cs | XAML code-behind | 已扫·命中1 | ASYNC_VOID×1 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态） | 是·P1 | async void 形态 |
| src\Desktop\BiliCopilot.UI\Controls\ViewLater\ViewLaterBody.xaml.cs | XAML code-behind | 已扫·命中1 | ASYNC_VOID×1 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态） | 是·P1 | async void 形态 |
| src\Desktop\BiliCopilot.UI\Converters\LevelImageConverter.cs | Converter | 已扫·命中1 | IMAGE_ALLOC×1 | 先设 DecodePixelWidth 再取源；走 SmallImageCache | 评估·P2 | 图片解码分配 |
| src\Desktop\BiliCopilot.UI\Converters\UriToBitmapConverter.cs | Converter | 已扫·命中2 | IMAGE_ALLOC×2 | 先设 DecodePixelWidth 再取源；走 SmallImageCache | 评估·P2 | 图片解码分配 |
| src\Desktop\BiliCopilot.UI\Extensions\InternalHttpExtensions.cs | Extension | 已扫·命中2 | CONFIGUREAWAIT×1, NEW_HTTPCLIENT×1 | UI 层可不用；库/后台层保留（现状多为正确用法）；复用静态 HttpClient（SocketsHttpHandler） | 是·P1 | ConfigureAwait 使用、热路径新建 HttpClient |
| src\Desktop\BiliCopilot.UI\Extensions\SmallImageCache.cs | Extension | 已扫·命中1 | IMAGE_ALLOC×1 | 先设 DecodePixelWidth 再取源；走 SmallImageCache | 评估·P2 | 图片解码分配 |
| src\Desktop\BiliCopilot.UI\Extensions\VisorClient.cs | Extension | 已扫·命中2 | JSON_REFLECTION×2 | 扩 GlobalSerializeContext 源生成，消掉反射序列化 | 是·P1 | JSON 走反射（与裁剪开关冲突） |
| src\Desktop\BiliCopilot.UI\Forms\GalleryWindow.xaml.cs | 窗口 | 已扫·命中1 | MISSING_CT×1 | 补 CancellationToken 形参并透传 | 评估·P2 | 缺少取消令牌 |
| src\Desktop\BiliCopilot.UI\Forms\MainWindow.xaml.cs | 窗口 | 已扫·命中2 | ASYNC_VOID×1, MISSING_CT×1 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态）；补 CancellationToken 形参并透传 | 是·P1 | async void 形态、缺少取消令牌 |
| src\Desktop\BiliCopilot.UI\Forms\PlayerWindow.cs | 窗口 | 已扫·命中10 | ASYNC_LAMBDA_VOID×1, ASYNC_VOID×1, SYNC_OVER_ASYNC×4, TASK_RUN×4 | async lambda → 显式 async Task 方法；async void → async Task；异常自兜底（runtime-async 不覆盖此形态）；去同步阻塞：改 await / DispatcherQueue 异步 | 是·P0 | async void lambda、async void 形态、UI 线程同步等 Task、多余 Task.Run |
| src\Desktop\BiliCopilot.UI\Forms\WebSignInWindow.xaml.cs | 窗口 | 已扫·命中3 | ASYNC_VOID×2, LINQ_MATERIALIZE×1 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态）；评估是否必须 ToList/ToArray（可 IEnumerable 直传） | 是·P1 | async void 形态、LINQ 物化 |
| src\Desktop\BiliCopilot.UI\Pages\Overlay\ArticleReaderPage.xaml.cs | 页面 | 已扫·命中1 | ASYNC_VOID×1 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态） | 是·P1 | async void 形态 |
| src\Desktop\BiliCopilot.UI\Pages\SearchHomePage.xaml.cs | 页面 | 已扫·命中5 | ASYNC_VOID×3, IMAGE_ALLOC×2 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态）；先设 DecodePixelWidth 再取源；走 SmallImageCache | 是·P1 | async void 形态、图片解码分配 |
| src\Desktop\BiliCopilot.UI\Pages\SettingsPage.xaml.cs | 页面 | 已扫·命中1 | ASYNC_VOID×1 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态） | 是·P1 | async void 形态 |
| src\Desktop\BiliCopilot.UI\Pages\StartupPage.xaml.cs | 页面 | 已扫·命中2 | ASYNC_VOID×2 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态） | 是·P1 | async void 形态 |
| src\Desktop\BiliCopilot.UI\Reactor\CommentPanel.cs | Reactor 组件 | 已扫·命中7 | ASYNC_LAMBDA_VOID×1, CONFIGUREAWAIT×1, IMAGE_ALLOC×2, LINQ_MATERIALIZE×3 | async lambda → 显式 async Task 方法；UI 层可不用；库/后台层保留（现状多为正确用法）；先设 DecodePixelWidth 再取源；走 SmallImageCache | 评估·P2 | async void lambda、ConfigureAwait 使用、图片解码分配、LINQ 物化 |
| src\Desktop\BiliCopilot.UI\Reactor\LiveSidePanel.cs | Reactor 组件 | 已扫·命中15 | ASYNC_LAMBDA_VOID×1, CLOSURE_IN_LOOP×1, CONFIGUREAWAIT×2, IMAGE_ALLOC×3, LINQ_IN_LOOP×4, LINQ_MATERIALIZE×4 | async lambda → 显式 async Task 方法；循环内提局部变量，避免闭包分配与捕获错位；UI 层可不用；库/后台层保留（现状多为正确用法） | 评估·P2 | async void lambda、闭包捕获循环变量、ConfigureAwait 使用、图片解码分配、循环内 LINQ 分配、LINQ 物化 |
| src\Desktop\BiliCopilot.UI\Reactor\MomentWaterfall.cs | Reactor 组件 | 已扫·命中1 | LINQ_MATERIALIZE×1 | 评估是否必须 ToList/ToArray（可 IEnumerable 直传） | 否·P3 | LINQ 物化 |
| src\Desktop\BiliCopilot.UI\Reactor\SearchShell.cs | Reactor 组件 | 已扫·命中1 | LINQ_MATERIALIZE×1 | 评估是否必须 ToList/ToArray（可 IEnumerable 直传） | 否·P3 | LINQ 物化 |
| src\Desktop\BiliCopilot.UI\Reactor\SeasonDetailShell.cs | Reactor 组件 | 已扫·命中3 | ASYNC_LAMBDA_VOID×1, LINQ_MATERIALIZE×2 | async lambda → 显式 async Task 方法；评估是否必须 ToList/ToArray（可 IEnumerable 直传） | 评估·P2 | async void lambda、LINQ 物化 |
| src\Desktop\BiliCopilot.UI\Resolvers\LiveMediaSourceResolver.cs | 其他 | 已扫·命中3 | LINQ_MATERIALIZE×2, MISSING_CT×1 | 评估是否必须 ToList/ToArray（可 IEnumerable 直传）；补 CancellationToken 形参并透传 | 评估·P2 | LINQ 物化、缺少取消令牌 |
| src\Desktop\BiliCopilot.UI\Resolvers\PgcMediaHistoryResolver.cs | 其他 | 已扫·命中1 | MISSING_CT×1 | 补 CancellationToken 形参并透传 | 评估·P2 | 缺少取消令牌 |
| src\Desktop\BiliCopilot.UI\Resolvers\PgcMediaSourceResolver.cs | 其他 | 已扫·命中6 | LINQ_MATERIALIZE×3, MISSING_CT×3 | 评估是否必须 ToList/ToArray（可 IEnumerable 直传）；补 CancellationToken 形参并透传 | 评估·P2 | LINQ 物化、缺少取消令牌 |
| src\Desktop\BiliCopilot.UI\Resolvers\VideoMediaHistoryResolver.cs | 其他 | 已扫·命中1 | MISSING_CT×1 | 补 CancellationToken 形参并透传 | 评估·P2 | 缺少取消令牌 |
| src\Desktop\BiliCopilot.UI\Resolvers\VideoMediaSourceResolver.cs | 其他 | 已扫·命中8 | LINQ_IN_LOOP×1, LINQ_MATERIALIZE×4, MISSING_CT×3 | 循环内改索引/Span 遍历，或预先物化；评估是否必须 ToList/ToArray（可 IEnumerable 直传）；补 CancellationToken 形参并透传 | 评估·P2 | 循环内 LINQ 分配、LINQ 物化、缺少取消令牌 |
| src\Desktop\BiliCopilot.UI\Services\AdCloudService.cs | Service | 已扫·命中3 | HTTP_BUFFERED×1, MISSING_CT×1, NEW_HTTPCLIENT×1 | 流式读取（ReadAsStreamAsync / JsonDocument 流式）；补 CancellationToken 形参并透传；复用静态 HttpClient（SocketsHttpHandler） | 是·P1 | 整串缓冲、缺少取消令牌、热路径新建 HttpClient |
| src\Desktop\BiliCopilot.UI\Services\AiAdAnalyzer.cs | Service | 已扫·命中10 | HTTP_BUFFERED×6, LOOP_DELAY×1, MISSING_CT×2, NEW_HTTPCLIENT×1 | 流式读取（ReadAsStreamAsync / JsonDocument 流式）；轮询节奏点：确认可取消、必要时改事件驱动；补 CancellationToken 形参并透传 | 是·P1 | 整串缓冲、循环轮询、缺少取消令牌、热路径新建 HttpClient |
| src\Desktop\BiliCopilot.UI\Services\AiEndpointTester.cs | Service | 已扫·命中16 | CONFIGUREAWAIT×8, HTTP_BUFFERED×4, MISSING_CT×4 | UI 层可不用；库/后台层保留（现状多为正确用法）；流式读取（ReadAsStreamAsync / JsonDocument 流式）；补 CancellationToken 形参并透传 | 评估·P2 | ConfigureAwait 使用、整串缓冲、缺少取消令牌 |
| src\Desktop\BiliCopilot.UI\Services\AiInterestAnalyzer.cs | Service | 已扫·命中14 | CONFIGUREAWAIT×8, HTTP_BUFFERED×1, LINQ_IN_LOOP×2, LINQ_MATERIALIZE×2, MISSING_CT×1 | UI 层可不用；库/后台层保留（现状多为正确用法）；流式读取（ReadAsStreamAsync / JsonDocument 流式）；循环内改索引/Span 遍历，或预先物化 | 评估·P2 | ConfigureAwait 使用、整串缓冲、循环内 LINQ 分配、LINQ 物化、缺少取消令牌 |
| src\Desktop\BiliCopilot.UI\Services\AppStateStore.cs | Service | 已扫·命中2 | JSON_REFLECTION×2 | 扩 GlobalSerializeContext 源生成，消掉反射序列化 | 是·P1 | JSON 走反射（与裁剪开关冲突） |
| src\Desktop\BiliCopilot.UI\Services\BackgroundLibrary.cs | Service | 已扫·命中3 | BLOCKING_SLEEP×1, MISSING_CT×1, NEW_HTTPCLIENT×1 | Thread.Sleep → await Task.Delay(token)；补 CancellationToken 形参并透传；复用静态 HttpClient（SocketsHttpHandler） | 是·P1 | 阻塞线程、缺少取消令牌、热路径新建 HttpClient |
| src\Desktop\BiliCopilot.UI\Services\BiliEmoteService.cs | Service | 已扫·命中5 | CONFIGUREAWAIT×2, HTTP_BUFFERED×1, LINQ_MATERIALIZE×1, MISSING_CT×1 | UI 层可不用；库/后台层保留（现状多为正确用法）；流式读取（ReadAsStreamAsync / JsonDocument 流式）；评估是否必须 ToList/ToArray（可 IEnumerable 直传） | 评估·P2 | ConfigureAwait 使用、整串缓冲、LINQ 物化、缺少取消令牌 |
| src\Desktop\BiliCopilot.UI\Services\BiliJumpService.cs | Service | 已扫·命中8 | CONFIGUREAWAIT×4, HTTP_BUFFERED×2, MISSING_CT×2 | UI 层可不用；库/后台层保留（现状多为正确用法）；流式读取（ReadAsStreamAsync / JsonDocument 流式）；补 CancellationToken 形参并透传 | 评估·P2 | ConfigureAwait 使用、整串缓冲、缺少取消令牌 |
| src\Desktop\BiliCopilot.UI\Services\CdnAccelerator.cs | Service | 已扫·命中10 | CONFIGUREAWAIT×3, LINQ_MATERIALIZE×4, MISSING_CT×2, NEW_HTTPCLIENT×1 | UI 层可不用；库/后台层保留（现状多为正确用法）；评估是否必须 ToList/ToArray（可 IEnumerable 直传）；补 CancellationToken 形参并透传 | 是·P1 | ConfigureAwait 使用、LINQ 物化、缺少取消令牌、热路径新建 HttpClient |
| src\Desktop\BiliCopilot.UI\Services\CdnPoolService.cs | Service | 已扫·命中11 | CONFIGUREAWAIT×6, HTTP_BUFFERED×2, LINQ_IN_LOOP×1, LINQ_MATERIALIZE×1, MISSING_CT×1 | UI 层可不用；库/后台层保留（现状多为正确用法）；流式读取（ReadAsStreamAsync / JsonDocument 流式）；循环内改索引/Span 遍历，或预先物化 | 评估·P2 | ConfigureAwait 使用、整串缓冲、循环内 LINQ 分配、LINQ 物化、缺少取消令牌 |
| src\Desktop\BiliCopilot.UI\Services\CommentEmoteEnricher.cs | Service | 已扫·命中5 | CONFIGUREAWAIT×2, HTTP_BUFFERED×1, MISSING_CT×1, NEW_HTTPCLIENT×1 | UI 层可不用；库/后台层保留（现状多为正确用法）；流式读取（ReadAsStreamAsync / JsonDocument 流式）；补 CancellationToken 形参并透传 | 是·P1 | ConfigureAwait 使用、整串缓冲、缺少取消令牌、热路径新建 HttpClient |
| src\Desktop\BiliCopilot.UI\Services\CommentVote\CommentVoteService.cs | Service-评论 | 已扫·命中22 | CONFIGUREAWAIT×12, HTTP_BUFFERED×3, LINQ_IN_LOOP×2, LINQ_MATERIALIZE×3, NEW_HTTPCLIENT×2 | UI 层可不用；库/后台层保留（现状多为正确用法）；流式读取（ReadAsStreamAsync / JsonDocument 流式）；循环内改索引/Span 遍历，或预先物化 | 是·P1 | ConfigureAwait 使用、整串缓冲、循环内 LINQ 分配、LINQ 物化、热路径新建 HttpClient |
| src\Desktop\BiliCopilot.UI\Services\DanmakuListLoader.cs | Service-弹幕 | 已扫·命中1 | NEW_HTTPCLIENT×1 | 复用静态 HttpClient（SocketsHttpHandler） | 是·P1 | 热路径新建 HttpClient |
| src\Desktop\BiliCopilot.UI\Services\Diagnostics\DiagnosticsReport.cs | Service-诊断 | 已扫·命中4 | CONFIGUREAWAIT×1, LINQ_MATERIALIZE×2, TASK_RUN×1 | UI 层可不用；库/后台层保留（现状多为正确用法）；评估是否必须 ToList/ToArray（可 IEnumerable 直传）；直调 async 方法，去掉多余线程池跳转 | 评估·P2 | ConfigureAwait 使用、LINQ 物化、多余 Task.Run |
| src\Desktop\BiliCopilot.UI\Services\Diagnostics\PlaybackStallDump.cs | Service-诊断 | 已扫·命中2 | LINQ_IN_LOOP×1, LINQ_MATERIALIZE×1 | 循环内改索引/Span 遍历，或预先物化；评估是否必须 ToList/ToArray（可 IEnumerable 直传） | 评估·P2 | 循环内 LINQ 分配、LINQ 物化 |
| src\Desktop\BiliCopilot.UI\Services\Diagnostics\PlaybackStallWatch.cs | Service-诊断 | 已扫·命中1 | BLOCKING_SLEEP×1 | Thread.Sleep → await Task.Delay(token) | 是·P1 | 阻塞线程 |
| src\Desktop\BiliCopilot.UI\Services\DynamicShareService.cs | Service | 已扫·命中24 | CONFIGUREAWAIT×17, HTTP_BUFFERED×3, LOOP_DELAY×1, MISSING_CT×3 | UI 层可不用；库/后台层保留（现状多为正确用法）；流式读取（ReadAsStreamAsync / JsonDocument 流式）；轮询节奏点：确认可取消、必要时改事件驱动 | 评估·P2 | ConfigureAwait 使用、整串缓冲、循环轮询、缺少取消令牌 |
| src\Desktop\BiliCopilot.UI\Services\ImageDiagnostics.cs | Service | 已扫·命中1 | CONFIGUREAWAIT×1 | UI 层可不用；库/后台层保留（现状多为正确用法） | 否·P3 | ConfigureAwait 使用 |
| src\Desktop\BiliCopilot.UI\Services\Live\BiliWbiSigner.cs | Service-直播 | 已扫·命中13 | CONFIGUREAWAIT×7, HTTP_BUFFERED×3, MISSING_CT×3 | UI 层可不用；库/后台层保留（现状多为正确用法）；流式读取（ReadAsStreamAsync / JsonDocument 流式）；补 CancellationToken 形参并透传 | 评估·P2 | ConfigureAwait 使用、整串缓冲、缺少取消令牌 |
| src\Desktop\BiliCopilot.UI\Services\Live\IpV4Connector.cs | Service-直播 | 已扫·命中2 | CONFIGUREAWAIT×2 | UI 层可不用；库/后台层保留（现状多为正确用法） | 否·P3 | ConfigureAwait 使用 |
| src\Desktop\BiliCopilot.UI\Services\Live\LiveDanmakuClient.cs | Service-直播 | 已扫·命中28 | CONFIGUREAWAIT×21, HTTP_BUFFERED×1, LOOP_DELAY×1, MISSING_CT×3, TASK_RUN×2 | UI 层可不用；库/后台层保留（现状多为正确用法）；流式读取（ReadAsStreamAsync / JsonDocument 流式）；轮询节奏点：确认可取消、必要时改事件驱动 | 评估·P2 | ConfigureAwait 使用、整串缓冲、循环轮询、缺少取消令牌、多余 Task.Run |
| src\Desktop\BiliCopilot.UI\Services\Live\LiveFollowService.cs | Service-直播 | 已扫·命中20 | CONFIGUREAWAIT×10, HTTP_BUFFERED×3, LINQ_IN_LOOP×2, LINQ_MATERIALIZE×2, NEW_HTTPCLIENT×3 | UI 层可不用；库/后台层保留（现状多为正确用法）；流式读取（ReadAsStreamAsync / JsonDocument 流式）；循环内改索引/Span 遍历，或预先物化 | 是·P1 | ConfigureAwait 使用、整串缓冲、循环内 LINQ 分配、LINQ 物化、热路径新建 HttpClient |
| src\Desktop\BiliCopilot.UI\Services\Live\LiveRoomApi.cs | Service-直播 | 已扫·命中23 | CONFIGUREAWAIT×11, HTTP_BUFFERED×2, LINQ_MATERIALIZE×1, MISSING_CT×8, NEW_HTTPCLIENT×1 | UI 层可不用；库/后台层保留（现状多为正确用法）；流式读取（ReadAsStreamAsync / JsonDocument 流式）；评估是否必须 ToList/ToArray（可 IEnumerable 直传） | 是·P1 | ConfigureAwait 使用、整串缓冲、LINQ 物化、缺少取消令牌、热路径新建 HttpClient |
| src\Desktop\BiliCopilot.UI\Services\Live\LiveRoomSendApi.cs | Service-直播 | 已扫·命中15 | CONFIGUREAWAIT×8, HTTP_BUFFERED×2, LINQ_MATERIALIZE×1, MISSING_CT×3, NEW_HTTPCLIENT×1 | UI 层可不用；库/后台层保留（现状多为正确用法）；流式读取（ReadAsStreamAsync / JsonDocument 流式）；评估是否必须 ToList/ToArray（可 IEnumerable 直传） | 是·P1 | ConfigureAwait 使用、整串缓冲、LINQ 物化、缺少取消令牌、热路径新建 HttpClient |
| src\Desktop\BiliCopilot.UI\Services\LiveEmoteService.cs | Service | 已扫·命中6 | CONFIGUREAWAIT×3, HTTP_BUFFERED×1, MISSING_CT×1, NEW_HTTPCLIENT×1 | UI 层可不用；库/后台层保留（现状多为正确用法）；流式读取（ReadAsStreamAsync / JsonDocument 流式）；补 CancellationToken 形参并透传 | 是·P1 | ConfigureAwait 使用、整串缓冲、缺少取消令牌、热路径新建 HttpClient |
| src\Desktop\BiliCopilot.UI\Services\LiveEmoteStore.cs | Service | 已扫·命中2 | JSON_REFLECTION×2 | 扩 GlobalSerializeContext 源生成，消掉反射序列化 | 是·P1 | JSON 走反射（与裁剪开关冲突） |
| src\Desktop\BiliCopilot.UI\Services\LiveSectionCache.cs | Service | 已扫·命中2 | CONFIGUREAWAIT×1, MISSING_CT×1 | UI 层可不用；库/后台层保留（现状多为正确用法）；补 CancellationToken 形参并透传 | 评估·P2 | ConfigureAwait 使用、缺少取消令牌 |
| src\Desktop\BiliCopilot.UI\Services\MpvStatsProbe.cs | Service | 已扫·命中1 | MISSING_CT×1 | 补 CancellationToken 形参并透传 | 评估·P2 | 缺少取消令牌 |
| src\Desktop\BiliCopilot.UI\Services\PageInterestAnalyzer.cs | Service | 已扫·命中2 | CONFIGUREAWAIT×1, LINQ_MATERIALIZE×1 | UI 层可不用；库/后台层保留（现状多为正确用法）；评估是否必须 ToList/ToArray（可 IEnumerable 直传） | 否·P3 | ConfigureAwait 使用、LINQ 物化 |
| src\Desktop\BiliCopilot.UI\Services\PerfProbe.cs | Service | 已扫·命中5 | CONFIGUREAWAIT×1, LINQ_IN_LOOP×2, LOOP_DELAY×1, TASK_RUN×1 | UI 层可不用；库/后台层保留（现状多为正确用法）；循环内改索引/Span 遍历，或预先物化；轮询节奏点：确认可取消、必要时改事件驱动 | 评估·P2 | ConfigureAwait 使用、循环内 LINQ 分配、循环轮询、多余 Task.Run |
| src\Desktop\BiliCopilot.UI\Services\ProxiedPlayUrlService.cs | Service | 已扫·命中2 | MISSING_CT×1, NEW_HTTPCLIENT×1 | 补 CancellationToken 形参并透传；复用静态 HttpClient（SocketsHttpHandler） | 是·P1 | 缺少取消令牌、热路径新建 HttpClient |
| src\Desktop\BiliCopilot.UI\Services\QualityUnlockService.cs | Service | 已扫·命中42 | CLOSURE_IN_LOOP×1, CONFIGUREAWAIT×29, HTTP_BUFFERED×7, LINQ_IN_LOOP×1, LINQ_MATERIALIZE×1, MISSING_CT×3 | 循环内提局部变量，避免闭包分配与捕获错位；UI 层可不用；库/后台层保留（现状多为正确用法）；流式读取（ReadAsStreamAsync / JsonDocument 流式） | 评估·P2 | 闭包捕获循环变量、ConfigureAwait 使用、整串缓冲、循环内 LINQ 分配、LINQ 物化、缺少取消令牌 |
| src\Desktop\BiliCopilot.UI\Services\SeasonSubscriptionService.cs | Service | 已扫·命中4 | CONFIGUREAWAIT×2, HTTP_BUFFERED×1, MISSING_CT×1 | UI 层可不用；库/后台层保留（现状多为正确用法）；流式读取（ReadAsStreamAsync / JsonDocument 流式）；补 CancellationToken 形参并透传 | 评估·P2 | ConfigureAwait 使用、整串缓冲、缺少取消令牌 |
| src\Desktop\BiliCopilot.UI\Services\SponsorBlockService.cs | Service | 已扫·命中6 | HTTP_BUFFERED×2, MISSING_CT×3, NEW_HTTPCLIENT×1 | 流式读取（ReadAsStreamAsync / JsonDocument 流式）；补 CancellationToken 形参并透传；复用静态 HttpClient（SocketsHttpHandler） | 是·P1 | 整串缓冲、缺少取消令牌、热路径新建 HttpClient |
| src\Desktop\BiliCopilot.UI\Services\SubtitleSampler.cs | Service | 已扫·命中12 | CONFIGUREAWAIT×8, HTTP_BUFFERED×1, LINQ_IN_LOOP×1, LINQ_MATERIALIZE×1, MISSING_CT×1 | UI 层可不用；库/后台层保留（现状多为正确用法）；流式读取（ReadAsStreamAsync / JsonDocument 流式）；循环内改索引/Span 遍历，或预先物化 | 评估·P2 | ConfigureAwait 使用、整串缓冲、循环内 LINQ 分配、LINQ 物化、缺少取消令牌 |
| src\Desktop\BiliCopilot.UI\Toolkits\AppToolkit.cs | Toolkit | 已扫·命中2 | MISSING_CT×2 | 补 CancellationToken 形参并透传 | 评估·P2 | 缺少取消令牌 |
| src\Desktop\BiliCopilot.UI\Toolkits\CommentVotePalette.cs | Toolkit | 已扫·命中1 | LINQ_MATERIALIZE×1 | 评估是否必须 ToList/ToArray（可 IEnumerable 直传） | 否·P3 | LINQ 物化 |
| src\Desktop\BiliCopilot.UI\Toolkits\IdToolkit.cs | Toolkit | 已扫·命中2 | LINQ_MATERIALIZE×2 | 评估是否必须 ToList/ToArray（可 IEnumerable 直传） | 否·P3 | LINQ 物化 |
| src\Desktop\BiliCopilot.UI\Toolkits\PlayUrlOptimizer.cs | Toolkit | 已扫·命中6 | LINQ_MATERIALIZE×4, MISSING_CT×1, NEW_HTTPCLIENT×1 | 评估是否必须 ToList/ToArray（可 IEnumerable 直传）；补 CancellationToken 形参并透传；复用静态 HttpClient（SocketsHttpHandler） | 是·P1 | LINQ 物化、缺少取消令牌、热路径新建 HttpClient |
| src\Desktop\BiliCopilot.UI\ViewModels\Components\AccountViewModel\AccountViewModel.cs | ViewModel | 已扫·命中1 | CONFIGUREAWAIT×1 | UI 层可不用；库/后台层保留（现状多为正确用法） | 否·P3 | ConfigureAwait 使用 |
| src\Desktop\BiliCopilot.UI\ViewModels\Components\AnimeTimelineViewModel\AnimeTimelineViewModel.cs | ViewModel | 已扫·命中9 | LINQ_IN_LOOP×6, LINQ_MATERIALIZE×1, TASK_RUN×2 | 循环内改索引/Span 遍历，或预先物化；评估是否必须 ToList/ToArray（可 IEnumerable 直传）；直调 async 方法，去掉多余线程池跳转 | 评估·P2 | 循环内 LINQ 分配、LINQ 物化、多余 Task.Run |
| src\Desktop\BiliCopilot.UI\ViewModels\Components\ArticlePartitionDetailViewModel\ArticlePartitionDetailViewModel.cs | ViewModel | 已扫·命中7 | ASYNC_LAMBDA_VOID×3, LINQ_IN_LOOP×1, LINQ_MATERIALIZE×3 | async lambda → 显式 async Task 方法；循环内改索引/Span 遍历，或预先物化；评估是否必须 ToList/ToArray（可 IEnumerable 直传） | 评估·P2 | async void lambda、循环内 LINQ 分配、LINQ 物化 |
| src\Desktop\BiliCopilot.UI\ViewModels\Components\CommentDetailViewModel\CommentDetailViewModel.cs | ViewModel | 已扫·命中2 | ASYNC_LAMBDA_VOID×1, LINQ_IN_LOOP×1 | async lambda → 显式 async Task 方法；循环内改索引/Span 遍历，或预先物化 | 评估·P2 | async void lambda、循环内 LINQ 分配 |
| src\Desktop\BiliCopilot.UI\ViewModels\Components\CommentMainViewModel\CommentMainViewModel.cs | ViewModel | 已扫·命中2 | LINQ_IN_LOOP×1, LINQ_MATERIALIZE×1 | 循环内改索引/Span 遍历，或预先物化；评估是否必须 ToList/ToArray（可 IEnumerable 直传） | 评估·P2 | 循环内 LINQ 分配、LINQ 物化 |
| src\Desktop\BiliCopilot.UI\ViewModels\Components\ComprehensiveMomentSectionDetailViewModel\ComprehensiveMomentSectionDetailViewModel.cs | ViewModel | 已扫·命中1 | ASYNC_LAMBDA_VOID×1 | async lambda → 显式 async Task 方法 | 评估·P2 | async void lambda |
| src\Desktop\BiliCopilot.UI\ViewModels\Components\EntertainmentIndexViewModel\EntertainmentIndexViewModel.cs | ViewModel | 已扫·命中2 | ASYNC_LAMBDA_VOID×1, LINQ_MATERIALIZE×1 | async lambda → 显式 async Task 方法；评估是否必须 ToList/ToArray（可 IEnumerable 直传） | 评估·P2 | async void lambda、LINQ 物化 |
| src\Desktop\BiliCopilot.UI\ViewModels\Components\LiveHistorySectionDetailViewModel\LiveHistorySectionDetailViewModel.cs | ViewModel | 已扫·命中1 | ASYNC_LAMBDA_VOID×1 | async lambda → 显式 async Task 方法 | 评估·P2 | async void lambda |
| src\Desktop\BiliCopilot.UI\ViewModels\Components\LiveMomentSectionDetailViewModel\LiveMomentSectionDetailViewModel.cs | ViewModel | 已扫·命中7 | LINQ_IN_LOOP×3, LINQ_MATERIALIZE×4 | 循环内改索引/Span 遍历，或预先物化；评估是否必须 ToList/ToArray（可 IEnumerable 直传） | 评估·P2 | 循环内 LINQ 分配、LINQ 物化 |
| src\Desktop\BiliCopilot.UI\ViewModels\Components\LivePartitionDetailViewModel\LivePartitionDetailViewModel.cs | ViewModel | 已扫·命中3 | ASYNC_LAMBDA_VOID×2, LINQ_MATERIALIZE×1 | async lambda → 显式 async Task 方法；评估是否必须 ToList/ToArray（可 IEnumerable 直传） | 评估·P2 | async void lambda、LINQ 物化 |
| src\Desktop\BiliCopilot.UI\ViewModels\Components\LiveSearchSectionDetailViewModel\LiveSearchSectionDetailViewModel.cs | ViewModel | 已扫·命中1 | LINQ_IN_LOOP×1 | 循环内改索引/Span 遍历，或预先物化 | 评估·P2 | 循环内 LINQ 分配 |
| src\Desktop\BiliCopilot.UI\ViewModels\Components\PgcFavoriteSectionDetailViewModel\PgcFavoriteSectionDetailViewModel.cs | ViewModel | 已扫·命中2 | LINQ_MATERIALIZE×2 | 评估是否必须 ToList/ToArray（可 IEnumerable 直传） | 否·P3 | LINQ 物化 |
| src\Desktop\BiliCopilot.UI\ViewModels\Components\PgcSearchSectionDetailViewModel\PgcSearchSectionDetailViewModel.cs | ViewModel | 已扫·命中1 | LINQ_IN_LOOP×1 | 循环内改索引/Span 遍历，或预先物化 | 评估·P2 | 循环内 LINQ 分配 |
| src\Desktop\BiliCopilot.UI\ViewModels\Components\SubscribedSeasonSectionDetailViewModel\SubscribedSeasonSectionDetailViewModel.cs | ViewModel | 已扫·命中5 | ASYNC_NO_AWAIT×1, CONFIGUREAWAIT×2, LINQ_MATERIALIZE×2 | 去掉 async（runtime-async 下也无收益）；UI 层可不用；库/后台层保留（现状多为正确用法）；评估是否必须 ToList/ToArray（可 IEnumerable 直传） | 评估·P2 | async 无 await、ConfigureAwait 使用、LINQ 物化 |
| src\Desktop\BiliCopilot.UI\ViewModels\Components\UserMomentDetailViewModel\UserMomentDetailViewModel.cs | ViewModel | 已扫·命中1 | LINQ_IN_LOOP×1 | 循环内改索引/Span 遍历，或预先物化 | 评估·P2 | 循环内 LINQ 分配 |
| src\Desktop\BiliCopilot.UI\ViewModels\Components\UserSearchSectionDetailViewModel\UserSearchSectionDetailViewModel.cs | ViewModel | 已扫·命中1 | LINQ_IN_LOOP×1 | 循环内改索引/Span 遍历，或预先物化 | 评估·P2 | 循环内 LINQ 分配 |
| src\Desktop\BiliCopilot.UI\ViewModels\Components\VideoFavoriteSectionDetailViewModel\VideoFavoriteSectionDetailViewModel.cs | ViewModel | 已扫·命中1 | LINQ_MATERIALIZE×1 | 评估是否必须 ToList/ToArray（可 IEnumerable 直传） | 否·P3 | LINQ 物化 |
| src\Desktop\BiliCopilot.UI\ViewModels\Components\VideoMomentSectionDetailViewModel\VideoMomentSectionDetailViewModel.cs | ViewModel | 已扫·命中10 | ASYNC_LAMBDA_VOID×1, CONFIGUREAWAIT×2, LINQ_IN_LOOP×2, LINQ_MATERIALIZE×4, TASK_RUN×1 | async lambda → 显式 async Task 方法；UI 层可不用；库/后台层保留（现状多为正确用法）；循环内改索引/Span 遍历，或预先物化 | 评估·P2 | async void lambda、ConfigureAwait 使用、循环内 LINQ 分配、LINQ 物化、多余 Task.Run |
| src\Desktop\BiliCopilot.UI\ViewModels\Components\VideoPartitionDetailViewModel\VideoPartitionDetailViewModel.cs | ViewModel | 已扫·命中2 | LINQ_MATERIALIZE×2 | 评估是否必须 ToList/ToArray（可 IEnumerable 直传） | 否·P3 | LINQ 物化 |
| src\Desktop\BiliCopilot.UI\ViewModels\Components\VideoPlayerDanmakuSectionDetailViewModel.cs | ViewModel | 已扫·命中5 | LINQ_IN_LOOP×2, LINQ_MATERIALIZE×3 | 循环内改索引/Span 遍历，或预先物化；评估是否必须 ToList/ToArray（可 IEnumerable 直传） | 评估·P2 | 循环内 LINQ 分配、LINQ 物化 |
| src\Desktop\BiliCopilot.UI\ViewModels\Components\VideoPlayerPartSectionDetailViewModel.cs | ViewModel | 已扫·命中1 | LINQ_MATERIALIZE×1 | 评估是否必须 ToList/ToArray（可 IEnumerable 直传） | 否·P3 | LINQ 物化 |
| src\Desktop\BiliCopilot.UI\ViewModels\Components\VideoSearchSectionDetailViewModel\VideoSearchSectionDetailViewModel.cs | ViewModel | 已扫·命中3 | ASYNC_LAMBDA_VOID×1, LINQ_IN_LOOP×2 | async lambda → 显式 async Task 方法；循环内改索引/Span 遍历，或预先物化 | 评估·P2 | async void lambda、循环内 LINQ 分配 |
| src\Desktop\BiliCopilot.UI\ViewModels\Core\AIViewModel\AIViewModel.Chat.cs | ViewModel | 已扫·命中5 | CONFIGUREAWAIT×2, LINQ_IN_LOOP×1, STRING_CONCAT_LOOP×2 | UI 层可不用；库/后台层保留（现状多为正确用法）；循环内改索引/Span 遍历，或预先物化；StringBuilder / string.Join / ValueStringBuilder | 是·P1 | ConfigureAwait 使用、循环内 LINQ 分配、循环内字符串拼接 |
| src\Desktop\BiliCopilot.UI\ViewModels\Core\AIViewModel\AIViewModel.cs | ViewModel | 已扫·命中3 | ASYNC_VOID×2, LINQ_MATERIALIZE×1 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态）；评估是否必须 ToList/ToArray（可 IEnumerable 直传） | 是·P1 | async void 形态、LINQ 物化 |
| src\Desktop\BiliCopilot.UI\ViewModels\Core\AppViewModel\AppViewModel.cs | ViewModel | 已扫·命中7 | ASYNC_LAMBDA_VOID×1, ASYNC_NO_AWAIT×1, CONFIGUREAWAIT×1, MISSING_CT×1, TASK_RUN×3 | async lambda → 显式 async Task 方法；去掉 async（runtime-async 下也无收益）；UI 层可不用；库/后台层保留（现状多为正确用法） | 评估·P2 | async void lambda、async 无 await、ConfigureAwait 使用、缺少取消令牌、多余 Task.Run |
| src\Desktop\BiliCopilot.UI\ViewModels\Core\DanmakuSendViewModel\DanmakuSendViewModel.cs | ViewModel | 已扫·命中1 | MISSING_CT×1 | 补 CancellationToken 形参并透传 | 评估·P2 | 缺少取消令牌 |
| src\Desktop\BiliCopilot.UI\ViewModels\Core\DownloadViewModel\DownloadViewModel.cs | ViewModel | 已扫·命中5 | SYNC_OVER_ASYNC×3, TASK_RUN×2 | 去同步阻塞：改 await / DispatcherQueue 异步；直调 async 方法，去掉多余线程池跳转 | 是·P0 | UI 线程同步等 Task、多余 Task.Run |
| src\Desktop\BiliCopilot.UI\ViewModels\Core\NotificationViewModel\NotificationViewModel.Live.cs | ViewModel | 已扫·命中11 | CONFIGUREAWAIT×3, HTTP_BUFFERED×1, LINQ_IN_LOOP×2, LINQ_MATERIALIZE×3, LOOP_DELAY×1, NEW_HTTPCLIENT×1 | UI 层可不用；库/后台层保留（现状多为正确用法）；流式读取（ReadAsStreamAsync / JsonDocument 流式）；循环内改索引/Span 遍历，或预先物化 | 是·P1 | ConfigureAwait 使用、整串缓冲、循环内 LINQ 分配、LINQ 物化、循环轮询、热路径新建 HttpClient |
| src\Desktop\BiliCopilot.UI\ViewModels\Core\NotificationViewModel\NotificationViewModel.Moment.cs | ViewModel | 已扫·命中7 | JSON_REFLECTION×2, LINQ_MATERIALIZE×4, LOOP_DELAY×1 | 扩 GlobalSerializeContext 源生成，消掉反射序列化；评估是否必须 ToList/ToArray（可 IEnumerable 直传）；轮询节奏点：确认可取消、必要时改事件驱动 | 是·P1 | JSON 走反射（与裁剪开关冲突）、LINQ 物化、循环轮询 |
| src\Desktop\BiliCopilot.UI\ViewModels\Core\NotificationViewModel\NotificationViewModel.Reply.cs | ViewModel | 已扫·命中4 | CONFIGUREAWAIT×1, LINQ_MATERIALIZE×3 | UI 层可不用；库/后台层保留（现状多为正确用法）；评估是否必须 ToList/ToArray（可 IEnumerable 直传） | 否·P3 | ConfigureAwait 使用、LINQ 物化 |
| src\Desktop\BiliCopilot.UI\ViewModels\Core\NotificationViewModel\NotificationViewModel.Season.cs | ViewModel | 已扫·命中4 | CONFIGUREAWAIT×2, LINQ_MATERIALIZE×1, LOOP_DELAY×1 | UI 层可不用；库/后台层保留（现状多为正确用法）；评估是否必须 ToList/ToArray（可 IEnumerable 直传）；轮询节奏点：确认可取消、必要时改事件驱动 | 否·P3 | ConfigureAwait 使用、LINQ 物化、循环轮询 |
| src\Desktop\BiliCopilot.UI\ViewModels\Core\PgcConnectorViewModel\PgcConnectorViewModel.cs | ViewModel | 已扫·命中3 | MISSING_CT×3 | 补 CancellationToken 形参并透传 | 评估·P2 | 缺少取消令牌 |
| src\Desktop\BiliCopilot.UI\ViewModels\Core\PgcConnectorViewModel\PgcConnectorViewModel.Operations.cs | ViewModel | 已扫·命中1 | LINQ_MATERIALIZE×1 | 评估是否必须 ToList/ToArray（可 IEnumerable 直传） | 否·P3 | LINQ 物化 |
| src\Desktop\BiliCopilot.UI\ViewModels\Core\PinnerViewModel.cs | ViewModel | 已扫·命中1 | LINQ_MATERIALIZE×1 | 评估是否必须 ToList/ToArray（可 IEnumerable 直传） | 否·P3 | LINQ 物化 |
| src\Desktop\BiliCopilot.UI\ViewModels\Core\PlayerViewModel\PlayerViewModel.Commands.cs | VM-命令 | 已扫·命中3 | MISSING_CT×3 | 补 CancellationToken 形参并透传 | 评估·P2 | 缺少取消令牌 |
| src\Desktop\BiliCopilot.UI\ViewModels\Core\PlayerViewModel\PlayerViewModel.cs | ViewModel | 已扫·命中6 | LINQ_IN_LOOP×1, LINQ_MATERIALIZE×1, MISSING_CT×2, NEW_HTTPCLIENT×2 | 循环内改索引/Span 遍历，或预先物化；评估是否必须 ToList/ToArray（可 IEnumerable 直传）；补 CancellationToken 形参并透传 | 是·P1 | 循环内 LINQ 分配、LINQ 物化、缺少取消令牌、热路径新建 HttpClient |
| src\Desktop\BiliCopilot.UI\ViewModels\Core\PlayerViewModel\PlayerViewModel.Danmaku.cs | ViewModel | 已扫·命中2 | TASK_RUN×2 | 直调 async 方法，去掉多余线程池跳转 | 评估·P2 | 多余 Task.Run |
| src\Desktop\BiliCopilot.UI\ViewModels\Core\PlayerViewModel\PlayerViewModel.EndScreen.cs | ViewModel | 已扫·命中1 | LINQ_IN_LOOP×1 | 循环内改索引/Span 遍历，或预先物化 | 评估·P2 | 循环内 LINQ 分配 |
| src\Desktop\BiliCopilot.UI\ViewModels\Core\PlayerViewModel\PlayerViewModel.Engine.cs | ViewModel | 已扫·命中3 | MISSING_CT×3 | 补 CancellationToken 形参并透传 | 评估·P2 | 缺少取消令牌 |
| src\Desktop\BiliCopilot.UI\ViewModels\Core\PlayerViewModel\PlayerViewModel.Live.cs | ViewModel | 已扫·命中2 | IMAGE_ALLOC×1, NEW_HTTPCLIENT×1 | 先设 DecodePixelWidth 再取源；走 SmallImageCache；复用静态 HttpClient（SocketsHttpHandler） | 是·P1 | 图片解码分配、热路径新建 HttpClient |
| src\Desktop\BiliCopilot.UI\ViewModels\Core\PlayerViewModel\PlayerViewModel.LiveTimeline.cs | ViewModel | 已扫·命中1 | MISSING_CT×1 | 补 CancellationToken 形参并透传 | 评估·P2 | 缺少取消令牌 |
| src\Desktop\BiliCopilot.UI\ViewModels\Core\PlayerViewModel\PlayerViewModel.Methods.cs | VM-方法 | 已扫·命中6 | ASYNC_VOID×5, MISSING_CT×1 | async void → async Task；异常自兜底（runtime-async 不覆盖此形态）；补 CancellationToken 形参并透传 | 是·P1 | async void 形态、缺少取消令牌 |
| src\Desktop\BiliCopilot.UI\ViewModels\Core\PlayerViewModel\PlayerViewModel.Reload.cs | ViewModel | 已扫·命中2 | MISSING_CT×2 | 补 CancellationToken 形参并透传 | 评估·P2 | 缺少取消令牌 |
| src\Desktop\BiliCopilot.UI\ViewModels\Core\PlayerViewModel\PlayerViewModel.Statistics.cs | ViewModel | 已扫·命中2 | ASYNC_LAMBDA_VOID×1, LINQ_MATERIALIZE×1 | async lambda → 显式 async Task 方法；评估是否必须 ToList/ToArray（可 IEnumerable 直传） | 评估·P2 | async void lambda、LINQ 物化 |
| src\Desktop\BiliCopilot.UI\ViewModels\Core\VideoConnectorViewModel\VideoConnectorViewModel.cs | ViewModel | 已扫·命中3 | MISSING_CT×3 | 补 CancellationToken 形参并透传 | 评估·P2 | 缺少取消令牌 |
| src\Desktop\BiliCopilot.UI\ViewModels\Core\VideoConnectorViewModel\VideoConnectorViewModel.Methods.cs | VM-方法 | 已扫·命中1 | LINQ_MATERIALIZE×1 | 评估是否必须 ToList/ToArray（可 IEnumerable 直传） | 否·P3 | LINQ 物化 |
| src\Desktop\BiliCopilot.UI\ViewModels\Core\VideoConnectorViewModel\VideoConnectorViewModel.Operations.cs | ViewModel | 已扫·命中1 | LINQ_MATERIALIZE×1 | 评估是否必须 ToList/ToArray（可 IEnumerable 直传） | 否·P3 | LINQ 物化 |
| src\Desktop\BiliCopilot.UI\ViewModels\Items\CommentItemViewModel\CommentItemViewModel.cs | ViewModel | 已扫·命中2 | LINQ_MATERIALIZE×1, MISSING_CT×1 | 评估是否必须 ToList/ToArray（可 IEnumerable 直传）；补 CancellationToken 形参并透传 | 评估·P2 | LINQ 物化、缺少取消令牌 |
| src\Desktop\BiliCopilot.UI\ViewModels\Items\MomentUperSectionViewModel\MomentUperSectionViewModel.cs | ViewModel | 已扫·命中3 | ASYNC_LAMBDA_VOID×1, LINQ_IN_LOOP×2 | async lambda → 显式 async Task 方法；循环内改索引/Span 遍历，或预先物化 | 评估·P2 | async void lambda、循环内 LINQ 分配 |
| src\Desktop\BiliCopilot.UI\ViewModels\Items\TimelineItemViewModel.cs | ViewModel | 已扫·命中1 | LINQ_MATERIALIZE×1 | 评估是否必须 ToList/ToArray（可 IEnumerable 直传） | 否·P3 | LINQ 物化 |
| src\Desktop\BiliCopilot.UI\ViewModels\View\FansPageViewModel\FansPageViewModel.cs | ViewModel | 已扫·命中1 | ASYNC_LAMBDA_VOID×1 | async lambda → 显式 async Task 方法 | 评估·P2 | async void lambda |
| src\Desktop\BiliCopilot.UI\ViewModels\View\FavoritesPageViewModel\FavoritesPageViewModel.cs | ViewModel | 已扫·命中2 | ASYNC_LAMBDA_VOID×1, LINQ_MATERIALIZE×1 | async lambda → 显式 async Task 方法；评估是否必须 ToList/ToArray（可 IEnumerable 直传） | 评估·P2 | async void lambda、LINQ 物化 |
| src\Desktop\BiliCopilot.UI\ViewModels\View\FollowsPageViewModel\FollowsPageViewModel.cs | ViewModel | 已扫·命中3 | ASYNC_LAMBDA_VOID×2, LINQ_MATERIALIZE×1 | async lambda → 显式 async Task 方法；评估是否必须 ToList/ToArray（可 IEnumerable 直传） | 评估·P2 | async void lambda、LINQ 物化 |
| src\Desktop\BiliCopilot.UI\ViewModels\View\GalleryPageViewModel\GalleryPageViewModel.cs | ViewModel | 已扫·命中2 | NEW_HTTPCLIENT×2 | 复用静态 HttpClient（SocketsHttpHandler） | 是·P1 | 热路径新建 HttpClient |
| src\Desktop\BiliCopilot.UI\ViewModels\View\HistoryPageViewModel\HistoryPageViewModel.cs | ViewModel | 已扫·命中1 | LINQ_MATERIALIZE×1 | 评估是否必须 ToList/ToArray（可 IEnumerable 直传） | 否·P3 | LINQ 物化 |
| src\Desktop\BiliCopilot.UI\ViewModels\View\LivePartitionPageViewModel\LivePartitionPageViewModel.cs | ViewModel | 已扫·命中3 | ASYNC_LAMBDA_VOID×2, LINQ_IN_LOOP×1 | async lambda → 显式 async Task 方法；循环内改索引/Span 遍历，或预先物化 | 评估·P2 | async void lambda、循环内 LINQ 分配 |
| src\Desktop\BiliCopilot.UI\ViewModels\View\MessagePageViewModel\MessagePageViewModel.cs | ViewModel | 已扫·命中1 | LINQ_IN_LOOP×1 | 循环内改索引/Span 遍历，或预先物化 | 评估·P2 | 循环内 LINQ 分配 |
| src\Desktop\BiliCopilot.UI\ViewModels\View\PopularPageViewModel\PopularPageViewModel.cs | ViewModel | 已扫·命中5 | ASYNC_LAMBDA_VOID×1, LINQ_IN_LOOP×2, LINQ_MATERIALIZE×2 | async lambda → 显式 async Task 方法；循环内改索引/Span 遍历，或预先物化；评估是否必须 ToList/ToArray（可 IEnumerable 直传） | 评估·P2 | async void lambda、循环内 LINQ 分配、LINQ 物化 |
| src\Desktop\BiliCopilot.UI\ViewModels\View\PopularPageViewModel\PopularPageViewModel.Requests.cs | ViewModel | 已扫·命中1 | LINQ_IN_LOOP×1 | 循环内改索引/Span 遍历，或预先物化 | 评估·P2 | 循环内 LINQ 分配 |
| src\Desktop\BiliCopilot.UI\ViewModels\View\SettingsPageViewModel\SettingsPageViewModel.AI.cs | ViewModel | 已扫·命中3 | LINQ_IN_LOOP×1, LINQ_MATERIALIZE×1, MISSING_CT×1 | 循环内改索引/Span 遍历，或预先物化；评估是否必须 ToList/ToArray（可 IEnumerable 直传）；补 CancellationToken 形参并透传 | 评估·P2 | 循环内 LINQ 分配、LINQ 物化、缺少取消令牌 |
| src\Desktop\BiliCopilot.UI\ViewModels\View\SettingsPageViewModel\SettingsPageViewModel.cs | ViewModel | 已扫·命中10 | LINQ_IN_LOOP×2, LINQ_MATERIALIZE×8 | 循环内改索引/Span 遍历，或预先物化；评估是否必须 ToList/ToArray（可 IEnumerable 直传） | 评估·P2 | 循环内 LINQ 分配、LINQ 物化 |
| src\Desktop\BiliCopilot.UI\ViewModels\View\StartupPageViewModel\StartupPageViewModel.cs | ViewModel | 已扫·命中4 | ASYNC_LAMBDA_VOID×1, CONFIGUREAWAIT×1, IMAGE_ALLOC×1, IMAGE_IO×1 | async lambda → 显式 async Task 方法；UI 层可不用；库/后台层保留（现状多为正确用法）；先设 DecodePixelWidth 再取源；走 SmallImageCache | 评估·P2 | async void lambda、ConfigureAwait 使用、图片解码分配、图片 IO/解码 |
| src\Desktop\BiliCopilot.UI\ViewModels\View\ViewLaterPageViewModel\ViewLaterPageViewModel.cs | ViewModel | 已扫·命中2 | LINQ_IN_LOOP×1, LINQ_MATERIALIZE×1 | 循环内改索引/Span 遍历，或预先物化；评估是否必须 ToList/ToArray（可 IEnumerable 直传） | 评估·P2 | 循环内 LINQ 分配、LINQ 物化 |

#### BiliCopilot.Visor

| 路径 | 类型 | 扫描状态 | 问题 | 适用优化 | 需改 | 原因 |
|---|---|---|---|---|---|---|
| src\Desktop\BiliCopilot.Visor\VisorPipeServer.cs | 其他 | 已扫·命中8 | BLOCKING_SLEEP×1, DISCARDED_TASK×1, JSON_REFLECTION×2, LOOP_DELAY×1, SYNC_OVER_ASYNC×1, TASK_RUN×2 | Thread.Sleep → await Task.Delay(token)；Task 不要丢弃：await 或显式 _ = + 异常处理；扩 GlobalSerializeContext 源生成，消掉反射序列化 | 是·P0 | 阻塞线程、丢弃 Task、JSON 走反射（与裁剪开关冲突）、循环轮询、UI 线程同步等 Task、多余 Task.Run |
| src\Desktop\BiliCopilot.Visor\WheelScrollManager.cs | 其他 | 已扫·命中1 | LINQ_MATERIALIZE×1 | 评估是否必须 ToList/ToArray（可 IEnumerable 直传） | 否·P3 | LINQ 物化 |

### 6.2 无命中的手写文件（逐文件枚举）

按目录聚合，每个文件都列出；状态一律 `已扫·无命中`（20 类语义检查均未命中，仅代表"没有本文关心的问题"，不代表无 bug）。

- src\Desktop\BiliCopilot.UI/（3）：GlobalDependencies.cs、GlobalSerializeContext.cs、GlobalUsing.cs
- src\Desktop\BiliCopilot.UI.Models/（7）：MediaSnapshot.cs、PinItem.cs、PlaybackWatchTracker.cs、PlayerInformationUpdatedEventArgs.cs、PlayerNotification.cs、PlaylistInitializedEventArgs.cs、PlaybackWatchTracker.cs
- src\Desktop\BiliCopilot.UI.Models\Constants/（36）：Anime4KMode.cs、ArtCNNMode.cs、ArticleCardStyle.cs、BiliMediaType.cs、CdnReplaceMode.cs、DanmakuRendererType.cs、EpisodeCardStyle.cs、ExternalPlayerType.cs、LiveCardStyle.cs、MomentCardStyle.cs、MomentSectionType.cs、MomentVideoOpenedMarkStyle.cs、MpvBuiltInProfile.cs、MTCBehavior.cs、Nnedi3Mode.cs、PgcSectionType.cs、PinContentType.cs、PlayerDisplayMode.cs、PlayerEngineType.cs、PlayerManipulationType.cs、PlayerPresenterMode.cs、PlayerState.cs、PopularSectionType.cs、PreferCodecType.cs、PreferDecodeType.cs、PreferQualityType.cs、PromptConstants.cs、RavuMode.cs、ScreenshotAction.cs、SearchSectionType.cs、SeasonCardStyle.cs、SettingNames.cs、UnlockQualityPreference.cs、VideoCardStyle.cs、VideoLoopType.cs、VsrScale.cs
- src\Desktop\BiliCopilot.UI.ResourceGenerator/（1）：ResourceGenerator.cs
- src\Desktop\BiliCopilot.UI\Controls/（7）：BasicCoverImage.cs、BlurCoverImage.cs、MainFrame.xaml.cs、NavItemButton.cs、OverlayFrame.xaml.cs、OverlayPanel.xaml.cs、VideoCoverPanel.cs
- src\Desktop\BiliCopilot.UI\Controls\AI/（6）：AIBody.xaml.cs、AIControlBase.cs、AIFooter.xaml.cs、AIHeader.xaml.cs、AIOverlayPanel.xaml.cs、AIPanel.xaml.cs
- src\Desktop\BiliCopilot.UI\Controls\Article/（5）：ArticlePartitionDetailControlBase.cs、ArticlePartitionMainHeader.xaml.cs、ArticlePartitionSideBody.xaml.cs、ArticleReaderPageControlBase.cs、ArticleReaderToolBar.xaml.cs
- src\Desktop\BiliCopilot.UI\Controls\Comment/（5）：CommentItemControl.xaml.cs、CommentOverlayPanel.xaml.cs、CommentVoteEntryControl.xaml.cs、CommentVotePanel.xaml.cs、CommentVotePie.xaml.cs
- src\Desktop\BiliCopilot.UI\Controls\Components/（10）：AccountCard.xaml.cs、AppSearchBox.xaml.cs、CommentLikeButton.xaml.cs、HeaderAccountControl.xaml.cs、IndexFilterItemControl.xaml.cs、PartitionItemControl.xaml.cs、SideBottomLoadingWidget.xaml.cs、TimelineControl.xaml.cs、TopNavigationItem.xaml.cs、UserCardControl.xaml.cs
- src\Desktop\BiliCopilot.UI\Controls\Components\ArticleCardControl/（4）：ArticleCardControl.cs、HistoryArticlePresenter.xaml.cs、PartitionArticlePresenter.xaml.cs、SearchArticlePresenter.xaml.cs
- src\Desktop\BiliCopilot.UI\Controls\Components\ChatModelCardControl/（1）：ChatModelCardControl.cs
- src\Desktop\BiliCopilot.UI\Controls\Components\EpisodeCardControl/（3）：EpisodeCardControl.cs、MomentEpisodePresenter.xaml.cs、PlayerEpisodePresenter.xaml.cs
- src\Desktop\BiliCopilot.UI\Controls\Components\LiveCardControl/（6）：FollowLivePresenter.xaml.cs、HistoryLivePresenter.xaml.cs、LiveCardControl.cs、PartitionLivePresenter.xaml.cs、RecommendLivePresenter.xaml.cs、SearchLivePresenter.xaml.cs
- src\Desktop\BiliCopilot.UI\Controls\Components\MomentCardControl/（7）：ForwardMomentPresenter.xaml.cs、FullMomentPresenter.xaml.cs、MomentCardControl.cs、MomentItemOperationPanel.xaml.cs、PersonalFullMomentPresenter.xaml.cs、PersonalVideoMomentPresenter.xaml.cs、VideoMomentPresenter.xaml.cs
- src\Desktop\BiliCopilot.UI\Controls\Components\SeasonCardControl/（5）：FavoriteSeasonPresenter.xaml.cs、IndexSeasonPresenter.xaml.cs、PlayerSeasonPresenter.xaml.cs、SeasonCardControl.cs、TimelineSeasonPresenter.xaml.cs
- src\Desktop\BiliCopilot.UI\Controls\Components\ShimmerLayout/（1）：ShimmerLayout.cs
- src\Desktop\BiliCopilot.UI\Controls\Components\VideoCardControl/（2）：VideoCardControl.cs、VideoCardReactorHost.cs
- src\Desktop\BiliCopilot.UI\Controls\Core/（5）：CacheBar.xaml.cs、CursorGrid.cs、NextTip.xaml.cs、PlayerControlBase.cs、PlayerNotificationControl.xaml.cs
- src\Desktop\BiliCopilot.UI\Controls\Danmaku/（4）：CommonDanmakuParser.cs、DanmakuControlBase.cs、DanmakuDisplayOptions.xaml.cs、DanmakuSendDialog.xaml.cs
- src\Desktop\BiliCopilot.UI\Controls\Favorites/（15）：FavoritesPageControlBase.cs、FavoritesPageMainBody.xaml.cs、FavoritesPageMainHeader.xaml.cs、FavoritesPageSideBody.xaml.cs、FavoritesPageSideHeader.xaml.cs、PgcFavoriteControlBase.cs、PgcFavoriteSideItem.xaml.cs、SubscribedSeasonBody.xaml.cs、SubscribedSeasonControlBase.cs、UgcFavoriteControlBase.cs、UgcFavoriteHeader.xaml.cs、UgcFavoriteSideItem.xaml.cs、VideoFavoriteControlBase.cs、VideoFavoriteHeader.xaml.cs、VideoFavoriteSideItem.xaml.cs
- src\Desktop\BiliCopilot.UI\Controls\History/（2）：HistoryHeader.xaml.cs、HistoryPageControlBase.cs
- src\Desktop\BiliCopilot.UI\Controls\LivePartition/（2）：FollowedLiveBar.xaml.cs、LivePartitionPageControlBase.cs
- src\Desktop\BiliCopilot.UI\Controls\Markdown/（7）：Extensions.cs、HtmlWriter.cs、ImageProvider.cs、ISVGRenderer.cs、MarkdownConfig.cs、MarkdownTextBlock.xaml.cs、MarkdownThemes.cs
- src\Desktop\BiliCopilot.UI\Controls\Markdown\Renderers/（2）：WinUIObjectRenderer.cs、WinUIRenderer.cs
- src\Desktop\BiliCopilot.UI\Controls\Markdown\Renderers\ObjectRenderers/（7）：CodeBlockRenderer.cs、HeadingRenderer.cs、HtmlBlockRenderer.cs、ListRenderer.cs、ParagraphRenderer.cs、QuoteBlockRenderer.cs、ThematicBreakRenderer.cs
- src\Desktop\BiliCopilot.UI\Controls\Markdown\Renderers\ObjectRenderers\Extensions/（2）：TableRenderer.cs、TaskListRenderer.cs
- src\Desktop\BiliCopilot.UI\Controls\Markdown\Renderers\ObjectRenderers\Inlines/（10）：AutoLinkInlineRenderer.cs、CodeInlineRenderer.cs、ContainerInlineRenderer.cs、DelimiterInlineRenderer.cs、EmphasisInlineRenderer.cs、HtmlEntityInlineRenderer.cs、HtmlInlineRenderer.cs、LineBreakInlineRenderer.cs、LinkInlineRenderer.cs、LiteralInlineRenderer.cs
- src\Desktop\BiliCopilot.UI\Controls\Markdown\TextElements/（22）：HtmlElementType.cs、IAddChild.cs、MyAutolinkInline.cs、MyBlockContainer.cs、MyCodeBlock.cs、MyEmphasisInline.cs、MyFlowDocument.cs、MyHeading.cs、MyHyperlink.cs、MyHyperlinkButton.cs、MyInlineCode.cs、MyInlineText.cs、MyLineBreak.cs、MyList.cs、MyParagraph.cs、MyQuote.cs、MyTable.cs、MyTableCell.cs、MyTableRow.cs、MyTableUIElement.cs、MyTaskListCheckBox.cs、MyThematicBreak.cs
- src\Desktop\BiliCopilot.UI\Controls\Markdown\TextElements\Html/（3）：MyBlock.cs、MyDetails.cs、MyInline.cs
- src\Desktop\BiliCopilot.UI\Controls\Message/（14）：ChatMessageBox.xaml.cs、ChatMessageControlBase.cs、ChatMessageHeader.xaml.cs、ChatMessageItemControl.xaml.cs、ChatSessionBody.xaml.cs、ChatSessionSideItem.xaml.cs、MessagePageControlBase.cs、MessagePageMainBody.xaml.cs、MessagePageMainHeader.xaml.cs、MessagePageSideHeader.xaml.cs、NotifyMessageCardControl.xaml.cs、NotifyMessageControlBase.cs、NotifyMessageHeader.xaml.cs、NotifyMessageSideItem.xaml.cs
- src\Desktop\BiliCopilot.UI\Controls\Moment/（13）：ComprehensiveMainBody.xaml.cs、ComprehensiveMomentSectionDetailControl.xaml.cs、ComprehensiveSectionControlBase.cs、ComprehensiveSideBody.xaml.cs、MomentHeader.xaml.cs、MomentImageGallery.xaml.cs、MomentPresenter.xaml.cs、MomentUperItemControl.xaml.cs、MomentUperSectionControlBase.cs、UserMomentDetailControlBase.cs、UserSpacePageBody.xaml.cs、UserSpacePageControlBase.cs、UserSpacePageHeader.xaml.cs
- src\Desktop\BiliCopilot.UI\Controls\Pgc/（11）：AnimeMainHeader.xaml.cs、AnimePageControlBase.cs、AnimeSideHeader.xaml.cs、AnimeTimelineControlBase.cs、AnimeTimelineMainControl.xaml.cs、AnimeTimelineSideControl.xaml.cs、CinemaMainHeader.xaml.cs、CinemaPageControlBase.cs、CinemaSideHeader.xaml.cs、EntertainmentIndexControlBase.cs、EntertainmentIndexSideControl.xaml.cs
- src\Desktop\BiliCopilot.UI\Controls\Player/（34）：AdSkipIndicatorControl.xaml.cs、CoinButton.xaml.cs、DownloadButton.xaml.cs、FavoriteButton.xaml.cs、LikeButton.xaml.cs、LiveDanmakuOverlay.xaml.cs、LivePanel.xaml.cs、PgcDescriptorControl.xaml.cs、PgcInfoSection.xaml.cs、PgcOperationControl.xaml.cs、PgcPlayerControlBase.cs、PgcPlayerPanel.xaml.cs、PgcPlayerSideBody.xaml.cs、PgcPlayerSideHeader.xaml.cs、PgcPlayerWidget.xaml.cs、PgcSeasonsSection.xaml.cs、PgcStatControl.xaml.cs、PlayerCommentSection.xaml.cs、UpSectionControl.xaml.cs、VideoAISection.xaml.cs、VideoDanmakuSection.xaml.cs、VideoDescriptorControl.xaml.cs、VideoInfoSection.xaml.cs、VideoOperationControl.xaml.cs、VideoPartsSection.xaml.cs、VideoPlayerControlBase.cs、VideoPlayerPanel.xaml.cs、VideoPlayerSideBody.xaml.cs、VideoPlayerSideHeader.xaml.cs、VideoPlayerWidget.xaml.cs、VideoPlaylistSection.xaml.cs、VideoRecommendSection.xaml.cs、VideoSeasonSection.xaml.cs、VideoStatControl.xaml.cs
- src\Desktop\BiliCopilot.UI\Controls\Popular/（6）：PopularMainHeader.xaml.cs、PopularPageControlBase.cs、PopularRankParitionItemControl.xaml.cs、PopularSectionItemControl.xaml.cs、PopularSideBody.xaml.cs、PopularSideHeader.xaml.cs
- src\Desktop\BiliCopilot.UI\Controls\Search/（5）：SearchHeader.xaml.cs、SearchPageControlBase.cs、SearchSectionItemControl.xaml.cs、SearchSideBody.xaml.cs、VideoSearchExtraHeader.xaml.cs
- src\Desktop\BiliCopilot.UI\Controls\Settings/（19）：AboutSettingControl.xaml.cs、AdSkipSettingControl.xaml.cs、AIResponseSettingControl.xaml.cs、CacheSettingControl.xaml.cs、CloseBehaviorSettingControl.xaml.cs、DownloadSettingControl.xaml.cs、LayoutSettingSection.xaml.cs、NavigationSettingControl.xaml.cs、NotificationSettingControl.xaml.cs、PlayerBehaviorSettingControl.xaml.cs、PlayerControlSettingControl.xaml.cs、PromptTemplateSettingControl.xaml.cs、ProxySettingControl.xaml.cs、QualityUnlockSettingControl.xaml.cs、ScreenshotSettingControl.xaml.cs、ScrollSettingControl.xaml.cs、SearchRecommendSettingSection.xaml.cs、SettingsPageControlBase.cs、ThemeSettingControl.xaml.cs
- src\Desktop\BiliCopilot.UI\Controls\Users/（5）：FansHeader.xaml.cs、FansPageControlBase.cs、FollowsMainHeader.xaml.cs、FollowsPageControlBase.cs、FollowsSideBody.xaml.cs
- src\Desktop\BiliCopilot.UI\Controls\VideoPartition/（4）：VideoPartitionDetailControlBase.cs、VideoPartitionMainHeader.xaml.cs、VideoPartitionSideBody.xaml.cs、VideoPartitionSideHeader.xaml.cs
- src\Desktop\BiliCopilot.UI\Controls\ViewLater/（2）：ViewLaterHeader.xaml.cs、ViewLaterPageControlBase.cs
- src\Desktop\BiliCopilot.UI\Converters/（33）：ChildCommentTextConverter.cs、ColorTextConverter.cs、ColorToBrushConverter.cs、CompactOverlayConverter.cs、CountConverter.cs、DanmakuLocationConverter.cs、DisplayModeConverter.cs、DurationConverter.cs、ExternalPlayerTypeConverter.cs、FullScreenConverter.cs、FullWindowConverter.cs、GridLengthConverter.cs、LoopTypeConverter.cs、MomentVideoOpenedMarkStyleConverter.cs、MTCBehaviorConverter.cs、NotifyMessageIconConverter.cs、NotifyMessageTextConverter.cs、PgcFavoriteIconConverter.cs、PgcFavoriteStatusConverter.cs、PgcFavoriteTextConverter.cs、PgcSectionIconConverter.cs、PgcSectionTextConverter.cs、PinIconConverter.cs、PlayerPositionConverter.cs、PreferCodecTypeConverter.cs、PreferDecodeTypeConverter.cs、PreferQualityTypeConverter.cs、SearchSectionIconConverter.cs、SearchSectionTextConverter.cs、SearchSuggestRegionIconConverter.cs、SortTypeTextConverter.cs、TruncateTextConverter.cs、UnlockQualityPreferenceConverter.cs
- src\Desktop\BiliCopilot.UI\Extensions/（19）：AnimePageControlTemplateSelector.cs、BasicScaleEffect.cs、BlurCanvasEffect.cs、ChatConfigManager.cs、ColorToolkit.cs、FavoriteSectionTemplateSelector.cs、HistorySectionTemplateSelector.cs、ImageUrlToolkit.cs、LocaleExtension.cs、MessageSectionTemplateSelector.cs、MomentDataTemplateSelector.cs、MomentSectionTemplateSelector.cs、PlayerComponentTemplateSelector.cs、PlayerSectionTemplateSelector.cs、PopularSectionTemplateSelector.cs、SearchSectionTemplateSelector.cs、SearchSuggestTemplateSelector.cs、VideoCardTemplateSelector.cs、XamlRootProvider.cs
- src\Desktop\BiliCopilot.UI\Forms/（2）：ITipWindow.cs、StartupWindow.xaml.cs
- src\Desktop\BiliCopilot.UI\Models/（2）：PlayerStatisticItem.cs、PlayerSurface.cs
- src\Desktop\BiliCopilot.UI\Models\Constants/（1）：LiveFilterType.cs
- src\Desktop\BiliCopilot.UI\Pages/（12）：AnimePage.xaml.cs、ArticlePartitionPage.xaml.cs、CinemaPage.xaml.cs、DownloadPage.xaml.cs、GalleryPage.xaml.cs、IInitializePage.cs、IParameterPage.cs、LivePartitionPage.xaml.cs、MessagePage.xaml.cs、MomentPage.xaml.cs、PopularPage.xaml.cs、VideoPartitionPage.xaml.cs
- src\Desktop\BiliCopilot.UI\Pages\Overlay/（8）：FansPage.xaml.cs、FavoritesPage.xaml.cs、FollowsPage.xaml.cs、HistoryPage.xaml.cs、SearchPage.xaml.cs、SeasonDetailPage.xaml.cs、UserSpacePage.xaml.cs、ViewLaterPage.xaml.cs
- src\Desktop\BiliCopilot.UI\Reactor/（12）：DanmakuPanel.cs、LiveInteractionList.cs、MainShell.cs、MessageShell.cs、MomentShell.cs、MomentVideoCard.cs、PageShells.cs、PartitionShells.cs、SettingCards.cs、SettingsShell.cs、VideoCardBody.cs、VideoSidePanels.cs
- src\Desktop\BiliCopilot.UI\Resolvers/（1）：MediaSourceResolverBase.cs
- src\Desktop\BiliCopilot.UI\Services/（9）：BiliAccessTokenService.cs、CdnStatusBus.cs、DanmakuJumpDetector.cs、MpvLogWatcher.cs、MpvSurfaceService.cs、PerfWidgetMountTrace.cs、PlayerLifecycle.cs、StartupTrace.cs、UserLevelFilter.cs
- src\Desktop\BiliCopilot.UI\Services\CommentVote/（10）：CommentVoteExceptions.cs、CommentVoteGeometry.cs、CommentVoteModels.cs、CommentVoteParser.cs、CommentVoteText.cs、CommentVoteExceptions.cs、CommentVoteGeometry.cs、CommentVoteModels.cs、CommentVoteParser.cs、CommentVoteText.cs
- src\Desktop\BiliCopilot.UI\Services\Diagnostics/（8）：DanmakuRebuildTiming.cs、PlaybackStallDumpBudget.cs、PlaybackStallPolicy.cs、PlaybackStallProbe.cs、DanmakuRebuildTiming.cs、PlaybackStallDumpBudget.cs、PlaybackStallPolicy.cs、PlaybackStallProbe.cs
- src\Desktop\BiliCopilot.UI\Services\Live/（3）：LiveEmoteItem.cs、LiveMessageParser.cs、LiveProtobufHelper.cs
- src\Desktop\BiliCopilot.UI\Toolkits/（7）：FileToolkit.cs、MomentSentinelPager.cs、MomentTimelineToolkit.cs、MomentWatchHistoryToolkit.cs、ResourceToolkit.cs、SettingsToolkit.cs、MomentTimelineToolkit.cs
- src\Desktop\BiliCopilot.UI\ViewModels/（1）：LayoutPageViewModelBase.cs
- src\Desktop\BiliCopilot.UI\ViewModels\Components/（9）：EmoteModuleViewModel.cs、PgcPlayerEpisodeSectionDetailViewModel.cs、PgcPlayerInfoSectionDetailViewModel.cs、PgcPlayerSeasonSectionDetailViewModel.cs、VideoPlayerAISectionDetailViewModel.cs、VideoPlayerInfoSectionDetailViewModel.cs、VideoPlayerPlaylistSectionDetailViewModel.cs、VideoPlayerRecommendSectionDetailViewModel.cs、VideoPlayerSeasonSectionDetailViewModel.cs
- src\Desktop\BiliCopilot.UI\ViewModels\Components\AccountViewModel/（1）：AccountViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\Components\AnimeTimelineViewModel/（1）：AnimeTimelineViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\Components\ArticleHistorySectionDetailViewModel/（2）：ArticleHistorySectionDetailViewModel.cs、ArticleHistorySectionDetailViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\Components\ArticlePartitionDetailViewModel/（1）：ArticlePartitionDetailViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\Components\ArticleSearchSectionDetailViewModel/（2）：ArticleSearchSectionDetailViewModel.cs、ArticleSearchSectionDetailViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\Components\ChatMessageSectionDetailViewModel/（2）：ChatMessageSectionDetailViewModel.cs、ChatMessageSectionDetailViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\Components\CommentDetailViewModel/（1）：CommentDetailViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\Components\CommentMainViewModel/（1）：CommentMainViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\Components\ComprehensiveMomentSectionDetailViewModel/（1）：ComprehensiveMomentSectionDetailViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\Components\EntertainmentIndexViewModel/（1）：EntertainmentIndexViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\Components\LiveHistorySectionDetailViewModel/（1）：LiveHistorySectionDetailViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\Components\LivePartitionDetailViewModel/（1）：LivePartitionDetailViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\Components\LiveSearchSectionDetailViewModel/（1）：LiveSearchSectionDetailViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\Components\NotifyMessageSectionDetailViewModel/（2）：NotifyMessageSectionDetailViewModel.cs、NotifyMessageSectionDetailViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\Components\PgcFavoriteSectionDetailViewModel/（1）：PgcFavoriteSectionDetailViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\Components\PgcSearchSectionDetailViewModel/（1）：PgcSearchSectionDetailViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\Components\SearchBoxViewModel/（2）：SearchBoxViewModel.cs、SearchBoxViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\Components\UgcSeasonFavoriteSectionDetailViewModel/（2）：UgcSeasonFavoriteSectionDetailViewModel.cs、UgcSeasonFavoriteSectionDetailViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\Components\UserMomentDetailViewModel/（1）：UserMomentDetailViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\Components\UserSearchSectionDetailViewModel/（1）：UserSearchSectionDetailViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\Components\VideoFavoriteSectionDetailViewModel/（1）：VideoFavoriteSectionDetailViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\Components\VideoHistorySectionDetailViewModel/（2）：VideoHistorySectionDetailViewModel.cs、VideoHistorySectionDetailViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\Components\VideoMomentSectionDetailViewModel/（1）：VideoMomentSectionDetailViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\Components\VideoPartitionDetailViewModel/（1）：VideoPartitionDetailViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\Components\VideoSearchSectionDetailViewModel/（1）：VideoSearchSectionDetailViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\Core/（4）：IPlayerConnectorViewModel.cs、NavigationViewModel.cs、NotificationViewModel.cs、SubtitleViewModel.cs
- src\Desktop\BiliCopilot.UI\ViewModels\Core\AIViewModel/（2）：AIViewModel.Prompts.cs、AIViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\Core\AppViewModel/（1）：AppViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\Core\DanmakuRenderViewModel/（2）：DanmakuRenderViewModel.cs、DanmakuRenderViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\Core\DanmakuSendViewModel/（1）：DanmakuSendViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\Core\DownloadViewModel/（1）：DownloadViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\Core\NotificationViewModel/（2）：NotificationViewModel.cs、NotificationViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\Core\PgcConnectorViewModel/（2）：PgcConnectorViewModel.Methods.cs、PgcConnectorViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\Core\PlayerViewModel/（4）：PlayerViewModel.AdSkipLogs.cs、PlayerViewModel.CdnStatus.cs、PlayerViewModel.Diagnostics.cs、PlayerViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\Core\VideoConnectorViewModel/（1）：VideoConnectorViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\Items/（29）：AdSkipLogItem.cs、AIQuickItemViewModel.cs、CdnLatencyItem.cs、ChapterItemViewModel.cs、CommentVoteOptionViewModel.cs、CommentVoteViewModel.cs、DanmakuListItemViewModel.cs、IFavoriteSectionDetailViewModel.cs、IHistorySectionDetailViewModel.cs、IMessageSectionDetailViewModel.cs、IMomentSectionDetailViewModel.cs、IndexFilterViewModel.cs、IPgcSectionDetailViewModel.cs、IPlayerSectionDetailViewModel.cs、IPopularSectionItemViewModel.cs、ISearchSectionDetailViewModel.cs、LiveInteractionViewModel.cs、LiveSectionItemViewModel.cs、PartitionViewModel.cs、PlayerFavoriteFolderViewModel.cs、PlayerFormatItemViewModel.cs、PlayerNotificationItemViewModel.cs、PlaylistMediaViewModel.cs、PopularRankPartitionViewModel.cs、PopularSectionItemViewModel.cs、SearchSuggestItemViewModel.cs、SeasonSubscriptionItemViewModel.cs、SourceItemViewModel.cs、SubtitleItemViewModel.cs
- src\Desktop\BiliCopilot.UI\ViewModels\Items\ArticleItemViewModel/（2）：ArticleItemViewModel.cs、ArticleItemViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\Items\ChatMessageItemViewModel/（2）：ChatMessageItemViewModel.cs、ChatMessageItemViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\Items\CommentItemViewModel/（1）：CommentItemViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\Items\EpisodeItemViewModel/（2）：EpisodeItemViewModel.cs、EpisodeItemViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\Items\LiveItemViewModel/（2）：LiveItemViewModel.cs、LiveItemViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\Items\MomentItemViewModel/（2）：MomentItemViewModel.cs、MomentItemViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\Items\MomentUperSectionViewModel/（1）：MomentUperSectionViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\Items\NotifyMessageItemViewModel/（2）：NotifyMessageItemViewModel.cs、NotifyMessageItemViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\Items\SeasonItemViewModel/（2）：SeasonItemViewModel.cs、SeasonItemViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\Items\UserItemViewModel/（2）：UserItemViewModel.cs、UserItemViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\Items\VideoItemViewModel/（2）：VideoItemViewModel.cs、VideoItemViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\View/（1）：ICancelPageViewModel.cs
- src\Desktop\BiliCopilot.UI\ViewModels\View\AnimePageViewModel/（2）：AnimePageViewModel.cs、AnimePageViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\View\ArticlePartitionPageViewModel/（2）：ArticlePartitionPageViewModel.cs、ArticlePartitionPageViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\View\ArticleReaderPageViewModel/（2）：ArticleReaderPageViewModel.cs、ArticleReaderPageViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\View\CinemaPageViewModel/（2）：CinemaPageViewModel.cs、CinemaPageViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\View\FansPageViewModel/（1）：FansPageViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\View\FavoritesPageViewModel/（1）：FavoritesPageViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\View\FollowsPageViewModel/（1）：FollowsPageViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\View\GalleryPageViewModel/（1）：GalleryPageViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\View\HistoryPageViewModel/（1）：HistoryPageViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\View\LivePartitionPageViewModel/（1）：LivePartitionPageViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\View\MessagePageViewModel/（1）：MessagePageViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\View\MomentPageViewModel/（2）：MomentPageViewModel.cs、MomentPageViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\View\PopularPageViewModel/（1）：PopularPageViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\View\SearchPageViewModel/（2）：SearchPageViewModel.cs、SearchPageViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\View\SettingsPageViewModel/（1）：SettingsPageViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\View\StartupPageViewModel/（1）：StartupPageViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\View\UserSpacePageViewModel/（2）：UserSpacePageViewModel.cs、UserSpacePageViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\View\VideoPartitionPageViewModel/（2）：VideoPartitionPageViewModel.cs、VideoPartitionPageViewModel.Properties.cs
- src\Desktop\BiliCopilot.UI\ViewModels\View\ViewLaterPageViewModel/（1）：ViewLaterPageViewModel.Properties.cs
- src\Desktop\BiliCopilot.Visor/（3）：JsonGenContext.cs、Program.cs、SingleInstanceLock.cs
- src\Desktop\BiliCopilot.Visor.Models/（1）：VisorModels.cs
- tests\BiliCopilot.CommentPoll.Tests/（3）：CommentVoteParserTests.cs、CommentVoteTextTests.cs、VoteJson.cs
- tests\BiliCopilot.Diagnostics.Tests/（3）：DanmakuRebuildTimingTests.cs、PlaybackStallDiagnosticsTests.cs、TestDoubles.cs
- tests\BiliCopilot.Moment.Tests/（1）：Program.cs

### 6.3 生成代码（单列，不修改）

| 目录 | 文件数 | 行数 | 内容 |
|---|---|---|---|
| src\Desktop\BiliCopilot.UI.Models\obj\Debug\netstandard2.0/ | 3 | 37 | 例：BiliCopilot.UI.Models.GlobalUsings.g.cs、.NETStandard,Version=v2.0.AssemblyAttributes.cs、BiliCopilot.UI.Models.AssemblyInfo.cs |
| src\Desktop\BiliCopilot.UI.ResourceGenerator\obj\Debug\netstandard2.0/ | 3 | 37 | 例：BiliCopilot.UI.ResourceGenerator.GlobalUsings.g.cs、.NETStandard,Version=v2.0.AssemblyAttributes.cs、BiliCopilot.UI.ResourceGenerator.AssemblyInfo.cs |
| src\Desktop\BiliCopilot.UI\obj\x64\Debug\net11.0-windows10.0.22621.0\win-x64/ | 5 | 115 | 例：BiliCopilot.UI.GlobalUsings.g.cs、App.g.i.cs、App.g.cs |
| src\Desktop\BiliCopilot.UI\obj\x64\Debug\net11.0-windows10.0.22621.0\win-x64\Controls/ | 10 | 308 | 例：IncrementVerticalScrollControl.g.i.cs、IncrementVerticalScrollControl.g.cs、MainFrame.g.i.cs |
| src\Desktop\BiliCopilot.UI\obj\x64\Debug\net11.0-windows10.0.22621.0\win-x64\Controls\AI/ | 12 | 405 | 例：AIBody.g.i.cs、AIBody.g.cs、AIFooter.g.i.cs |
| src\Desktop\BiliCopilot.UI\obj\x64\Debug\net11.0-windows10.0.22621.0\win-x64\Controls\Article/ | 10 | 333 | 例：ArticlePartitionMainBody.g.i.cs、ArticlePartitionMainBody.g.cs、ArticlePartitionMainHeader.g.i.cs |
| src\Desktop\BiliCopilot.UI\obj\x64\Debug\net11.0-windows10.0.22621.0\win-x64\Controls\Comment/ | 16 | 575 | 例：CommentDetailPanel.g.i.cs、CommentDetailPanel.g.cs、CommentItemControl.g.i.cs |
| src\Desktop\BiliCopilot.UI\obj\x64\Debug\net11.0-windows10.0.22621.0\win-x64\Controls\Common/ | 2 | 53 | 例：AiInterestBanner.g.i.cs、AiInterestBanner.g.cs |
| src\Desktop\BiliCopilot.UI\obj\x64\Debug\net11.0-windows10.0.22621.0\win-x64\Controls\Components/ | 24 | 856 | 例：AccountCard.g.i.cs、AccountCard.g.cs、AppSearchBox.g.i.cs |
| src\Desktop\BiliCopilot.UI\obj\x64\Debug\net11.0-windows10.0.22621.0\win-x64\Controls\Components\ArticleCardControl/ | 6 | 206 | 例：HistoryArticlePresenter.g.i.cs、HistoryArticlePresenter.g.cs、PartitionArticlePresenter.g.i.cs |
| src\Desktop\BiliCopilot.UI\obj\x64\Debug\net11.0-windows10.0.22621.0\win-x64\Controls\Components\EpisodeCardControl/ | 4 | 136 | 例：MomentEpisodePresenter.g.i.cs、MomentEpisodePresenter.g.cs、PlayerEpisodePresenter.g.i.cs |
| src\Desktop\BiliCopilot.UI\obj\x64\Debug\net11.0-windows10.0.22621.0\win-x64\Controls\Components\LiveCardControl/ | 10 | 340 | 例：FollowLivePresenter.g.i.cs、FollowLivePresenter.g.cs、HistoryLivePresenter.g.i.cs |
| src\Desktop\BiliCopilot.UI\obj\x64\Debug\net11.0-windows10.0.22621.0\win-x64\Controls\Components\MomentCardControl/ | 12 | 401 | 例：ForwardMomentPresenter.g.i.cs、ForwardMomentPresenter.g.cs、FullMomentPresenter.g.i.cs |
| src\Desktop\BiliCopilot.UI\obj\x64\Debug\net11.0-windows10.0.22621.0\win-x64\Controls\Components\SeasonCardControl/ | 8 | 272 | 例：FavoriteSeasonPresenter.g.i.cs、FavoriteSeasonPresenter.g.cs、IndexSeasonPresenter.g.i.cs |
| src\Desktop\BiliCopilot.UI\obj\x64\Debug\net11.0-windows10.0.22621.0\win-x64\Controls\Components\VideoCardControl/ | 2 | 49 | 例：VideoCardReactorHost.g.i.cs、VideoCardReactorHost.g.cs |
| src\Desktop\BiliCopilot.UI\obj\x64\Debug\net11.0-windows10.0.22621.0\win-x64\Controls\Core/ | 16 | 688 | 例：CacheBar.g.i.cs、CacheBar.g.cs、ChapterPanel.g.i.cs |
| src\Desktop\BiliCopilot.UI\obj\x64\Debug\net11.0-windows10.0.22621.0\win-x64\Controls\Danmaku/ | 6 | 230 | 例：DanmakuDisplayOptions.g.i.cs、DanmakuDisplayOptions.g.cs、DanmakuSendDialog.g.i.cs |
| src\Desktop\BiliCopilot.UI\obj\x64\Debug\net11.0-windows10.0.22621.0\win-x64\Controls\Favorites/ | 28 | 970 | 例：FavoritesPageMainBody.g.i.cs、FavoritesPageMainBody.g.cs、FavoritesPageMainHeader.g.i.cs |
| src\Desktop\BiliCopilot.UI\obj\x64\Debug\net11.0-windows10.0.22621.0\win-x64\Controls\History/ | 10 | 362 | 例：ArticleHistorySection.g.i.cs、ArticleHistorySection.g.cs、HistoryHeader.g.i.cs |
| src\Desktop\BiliCopilot.UI\obj\x64\Debug\net11.0-windows10.0.22621.0\win-x64\Controls\LivePartition/ | 6 | 214 | 例：FollowedLiveBar.g.i.cs、FollowedLiveBar.g.cs、LiveRecommendMainBody.g.i.cs |
| src\Desktop\BiliCopilot.UI\obj\x64\Debug\net11.0-windows10.0.22621.0\win-x64\Controls\Message/ | 26 | 928 | 例：ChatMessageBox.g.i.cs、ChatMessageBox.g.cs、ChatMessageHeader.g.i.cs |
| src\Desktop\BiliCopilot.UI\obj\x64\Debug\net11.0-windows10.0.22621.0\win-x64\Controls\Moment/ | 28 | 997 | 例：ComprehensiveMainBody.g.i.cs、ComprehensiveMainBody.g.cs、ComprehensiveMomentSectionDetailControl.g.i.cs |
| src\Desktop\BiliCopilot.UI\obj\x64\Debug\net11.0-windows10.0.22621.0\win-x64\Controls\Pgc/ | 16 | 564 | 例：AnimeMainHeader.g.i.cs、AnimeMainHeader.g.cs、AnimeSideHeader.g.i.cs |
| src\Desktop\BiliCopilot.UI\obj\x64\Debug\net11.0-windows10.0.22621.0\win-x64\Controls\Player/ | 72 | 2,343 | 例：AdSkipIndicatorControl.g.i.cs、AdSkipIndicatorControl.g.cs、CdnIndicatorControl.g.i.cs |
| src\Desktop\BiliCopilot.UI\obj\x64\Debug\net11.0-windows10.0.22621.0\win-x64\Controls\Popular/ | 12 | 414 | 例：PopularMainBody.g.i.cs、PopularMainBody.g.cs、PopularMainHeader.g.i.cs |
| src\Desktop\BiliCopilot.UI\obj\x64\Debug\net11.0-windows10.0.22621.0\win-x64\Controls\Search/ | 18 | 636 | 例：ArticleSectionDetailControl.g.i.cs、ArticleSectionDetailControl.g.cs、LiveSectionDetailControl.g.i.cs |
| src\Desktop\BiliCopilot.UI\obj\x64\Debug\net11.0-windows10.0.22621.0\win-x64\Controls\Settings/ | 40 | 1,421 | 例：AboutSettingControl.g.i.cs、AboutSettingControl.g.cs、AdSkipSettingControl.g.i.cs |
| src\Desktop\BiliCopilot.UI\obj\x64\Debug\net11.0-windows10.0.22621.0\win-x64\Controls\Users/ | 10 | 350 | 例：FansBody.g.i.cs、FansBody.g.cs、FansHeader.g.i.cs |
| src\Desktop\BiliCopilot.UI\obj\x64\Debug\net11.0-windows10.0.22621.0\win-x64\Controls\VideoPartition/ | 8 | 259 | 例：VideoPartitionMainBody.g.i.cs、VideoPartitionMainBody.g.cs、VideoPartitionMainHeader.g.i.cs |
| src\Desktop\BiliCopilot.UI\obj\x64\Debug\net11.0-windows10.0.22621.0\win-x64\Controls\ViewLater/ | 4 | 144 | 例：ViewLaterBody.g.i.cs、ViewLaterBody.g.cs、ViewLaterHeader.g.i.cs |
| src\Desktop\BiliCopilot.UI\obj\x64\Debug\net11.0-windows10.0.22621.0\win-x64\Forms/ | 8 | 206 | 例：GalleryWindow.g.i.cs、GalleryWindow.g.cs、MainWindow.g.i.cs |
| src\Desktop\BiliCopilot.UI\obj\x64\Debug\net11.0-windows10.0.22621.0\win-x64\Pages/ | 26 | 740 | 例：AnimePage.g.i.cs、AnimePage.g.cs、ArticlePartitionPage.g.i.cs |
| src\Desktop\BiliCopilot.UI\obj\x64\Debug\net11.0-windows10.0.22621.0\win-x64\Pages\Overlay/ | 18 | 500 | 例：ArticleReaderPage.g.i.cs、ArticleReaderPage.g.cs、FansPage.g.i.cs |
| src\Desktop\BiliCopilot.Visor.Models\obj\Debug\net11.0/ | 3 | 38 | 例：BiliCopilot.Visor.Models.GlobalUsings.g.cs、.NETCoreApp,Version=v11.0.AssemblyAttributes.cs、BiliCopilot.Visor.Models.AssemblyInfo.cs |
| src\Desktop\BiliCopilot.Visor\obj\Debug\net11.0/ | 3 | 40 | 例：BiliCopilot.Visor.GlobalUsings.g.cs、.NETCoreApp,Version=v11.0.AssemblyAttributes.cs、BiliCopilot.Visor.AssemblyInfo.cs |
| tests\BiliCopilot.CommentPoll.Tests\obj\Debug\net11.0/ | 3 | 38 | 例：BiliCopilot.CommentPoll.Tests.GlobalUsings.g.cs、.NETCoreApp,Version=v11.0.AssemblyAttributes.cs、BiliCopilot.CommentPoll.Tests.AssemblyInfo.cs |
| tests\BiliCopilot.Diagnostics.Tests\obj\Debug\net11.0/ | 3 | 38 | 例：BiliCopilot.Diagnostics.Tests.GlobalUsings.g.cs、.NETCoreApp,Version=v11.0.AssemblyAttributes.cs、BiliCopilot.Diagnostics.Tests.AssemblyInfo.cs |
| tests\BiliCopilot.Moment.Tests\obj\Debug\net11.0/ | 3 | 38 | 例：BiliCopilot.Moment.Tests.GlobalUsings.g.cs、.NETCoreApp,Version=v11.0.AssemblyAttributes.cs、BiliCopilot.Moment.Tests.AssemblyInfo.cs |

生成代码一律不动：XAML 代码隐藏（`*.g.i.cs`）、XamlTypeInfo、CommunityToolkit MVVM 的 `[ObservableProperty]`/`[RelayCommand]` 产物、Reactor 源映射、CsWin32/ComputeSharp 生成物。

### 6.4 编译单元里的包内/外部源码（不属于本仓库，只记录）

| 路径 | 行数 | 来源 |
|---|---|---|
| C:\Users\黎翱白Leobai\.nuget\packages\microsoft.net.test.sdk\17.10.0\build\netcoreapp3.1\Microsoft.NET.Test.Sdk.Program.cs | 4 | NuGet: microsoft.net.test.sdk |
| C:\Users\黎翱白Leobai\.nuget\packages\microsoft.net.test.sdk\17.10.0\build\netcoreapp3.1\Microsoft.NET.Test.Sdk.Program.cs | 4 | NuGet: microsoft.net.test.sdk |
| C:\Users\黎翱白Leobai\.nuget\packages\microsoft.windowsappsdk.foundation\2.6.5\buildTransitive\..\include\DeploymentManagerAutoInitializer.cs | 61 | NuGet: microsoft.windowsappsdk.foundation |
| C:\Users\黎翱白Leobai\.nuget\packages\microsoft.windowsappsdk.foundation\2.6.5\buildTransitive\..\include\WindowsAppRuntimeAutoInitializer.cs | 36 | NuGet: microsoft.windowsappsdk.foundation |
| C:\Users\黎翱白Leobai\.nuget\packages\microsoft.windowsappsdk.runtime\2.6.5\buildTransitive\..\include\WindowsAppSDK-VersionInfo.cs | 113 | NuGet: microsoft.windowsappsdk.runtime |

## 7. XAML 标记清点（246 个）

| 目录 | 文件数 | 行数 |
|---|---|---|
| / | 1 | 165 |
| Controls/ | 5 | 182 |
| Controls\AI/ | 6 | 581 |
| Controls\Article/ | 5 | 405 |
| Controls\Comment/ | 8 | 897 |
| Controls\Common/ | 1 | 34 |
| Controls\Components/ | 12 | 814 |
| Controls\Components\ArticleCardControl/ | 4 | 239 |
| Controls\Components\ChatModelCardControl/ | 1 | 102 |
| Controls\Components\EpisodeCardControl/ | 3 | 239 |
| Controls\Components\LiveCardControl/ | 6 | 456 |
| Controls\Components\MomentCardControl/ | 7 | 344 |
| Controls\Components\SeasonCardControl/ | 5 | 257 |
| Controls\Components\ShimmerLayout/ | 1 | 185 |
| Controls\Components\VideoCardControl/ | 2 | 1,212 |
| Controls\Core/ | 8 | 2,359 |
| Controls\Danmaku/ | 3 | 253 |
| Controls\Favorites/ | 14 | 687 |
| Controls\History/ | 5 | 261 |
| Controls\LivePartition/ | 3 | 143 |
| Controls\Markdown/ | 1 | 23 |
| Controls\Message/ | 13 | 716 |
| Controls\Moment/ | 14 | 1,270 |
| Controls\Pgc/ | 8 | 447 |
| Controls\Player/ | 36 | 2,063 |
| Controls\Popular/ | 6 | 316 |
| Controls\Search/ | 9 | 380 |
| Controls\Settings/ | 20 | 1,470 |
| Controls\Users/ | 5 | 239 |
| Controls\VideoPartition/ | 4 | 274 |
| Controls\ViewLater/ | 2 | 127 |
| Forms/ | 4 | 82 |
| Pages/ | 13 | 685 |
| Pages\Overlay/ | 9 | 203 |
| Styles/ | 2 | 1,542 |

XAML 只清点不语义分析：仓库把 `WMC1510` 长期抑制（XAML 两遍编译必需的告警），且 XAML 编译器的类型解析发生在 C# 编译之前的中间程序集上——单独对 .xaml 做静态分析拿不到 `x:Bind` 的真实类型信息。**code-behind 侧（`.xaml.cs`）已全部纳入 §6 的语义扫描**。

## 8. 已知局限

1. **只在 x64 / Debug 一份编译单元上扫描**：`/define:` 用的是 `TRACE;LOCAL_DEV;ARCH_X64;...;DEBUG`。Release 下 `#if DEBUG` 的代码（6 个诊断源文件、部分诊断探针）不会被扫到；ARM64 分支同理。
2. **测试工程的 TFM 已在扫描期间变化**：扫描时按当前树取到的是 `net11.0`（`5fe32e5`），所以"同一文件被 UI 与测试工程各编一次、但两边 TFM 不同"这个旧约束**当前已不存在**——两侧都是 net11，链接文件可以用 net11 专有 API 了。
3. **nullable 上下文按 Disable 重建**：只为让语义解析不受 `#nullable` 影响，不影响本文任何判定（本文不查可空性）。
4. **检查项是"形"而不是"罪"**：例如 `CONFIGUREAWAIT` 199 处多数是正确用法（库层/后台层），`LINQ_MATERIALIZE` 117 处多数无害——判定顺序在 hotspots 文档里，不在清单里。
5. **`MISSING_CT` 只查"public/internal 异步方法无 CancellationToken 形参且有 await"**，不判断该调用是否真的可取消；清单里保留的 90 条需要人工分级（原始 114 条已剔除 24 条测试工程误报，见 §1）。
6. **不覆盖**：IL/反射调用（`MethodInfo.Invoke`）、动态类型（`dynamic`）、COM 互操作边界内的性能特征、以及 `.xaml` 标记本身的热路径（如 `ItemsRepeater` 模板复杂度）。
7. **测试工程与 UI 的双宿主编译**：`Diagnostics.Tests`/`CommentPoll.Tests`/`Moment.Tests` 用 `<Compile Include>` 链接了 UI 的 8 个源文件，同一文件在清单里会出现两次（分别挂在测试工程与 UI 下），这是刻意的，不是重复计数。


