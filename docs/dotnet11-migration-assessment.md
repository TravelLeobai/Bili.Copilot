# Bili.Copilot：.NET 10 → .NET 11 迁移可行性评估

评估对象：`TravelLeobai/Bili.Copilot`，HEAD `413a7fe`（评估时 master 状态）
评估日期：2026-11-23
实验分支：`exp/dotnet11`（已推送到 github，不进 master）
决策口径：只改 .NET 版本配置，不动 Windows App SDK，不动业务代码

---

## 0. 一句话结论

**技术可行，成本极低（改一行 TFM 就能编过、能跑），但现在不值得迁。**
.NET 11 是 STS（2026-11 到 2028-11），比 .NET 10 的 LTS 窗口还短一年；性能与内存实测持平；
同时它把硬件底线抬到 x86-64-v2 / Arm64-LSE，且 Runtime Async 仍有未修的中断性 GC 崩溃。

建议：**暂不迁 master**，保留 `exp/dotnet11` 分支跟踪；等 11.0.1+ 修掉 runtime-async 崩溃、
且 CI 有 VS 18.12 + 原生链接器之后再评估。

---

## 1. 仓库现状（HEAD `413a7fe`）

| 项目 | TargetFramework |
|---|---|
| `BiliCopilot.UI` | `net10.0-windows10.0.22621.0` |
| `BiliCopilot.UI.Models` | `netstandard2.0` |
| `BiliCopilot.UI.ResourceGenerator` | `netstandard2.0` |
| `BiliCopilot.Visor` | `net9.0` |
| `BiliCopilot.Visor.Models` | `net9.0` |
| `BiliCopilot.Moment.Tests` | `net9.0` |

- **`global.json`：不存在**，SDK 选择完全靠机器默认（本机默认 10.0.403）。
- `src/Directory.Build.props`：`LangVersion=preview`、`Nullable=enable`、`TreatWarningsAsErrors=true`、
  `AnalysisMode=AllEnabledByDefault`、`EnableNETAnalyzers=true`。
- `src/Directory.Packages.props` 关键版本：WindowsAppSDK **2.6.5**、Microsoft.UI.Reactor
  **0.1.0-preview.17**、Microsoft.Graphics.Win2D **1.4.0**、Richasy.MpvKernel **0.0.2-preview2**、
  Richasy.BiliKernel **0.0.2-preview2**（Abstractions 0.0.2-preview1 / Core 0.0.1-preview1）、
  Microsoft.Windows.CsWinRT **2.2.0**、Microsoft.Windows.CsWin32 **0.3.257**、
  Windows SDK BuildTools **10.0.26100.6901**。
- `BiliCopilot.UI.csproj`：`EnableMsixTooling=true`、`RuntimeIdentifiers=win-x86;win-x64;win-arm64`、
  `PublishAot=True`、`PublishReadyToRun=False`、`ApplicationIcon=Assets\logo.ico`。
- CI：`.github/workflows/{build-release,build-sideload,build-store,test-*}.yml`，
  复用 `.github/actions/setup-env`，其中 **`dotnet-version: 9.0.x`** —— 与仓库现在的 net10 TFM 已经脱节，
  属于既有问题，不是本次迁移引入的。

---

## 2. .NET 11 官方状态与兼容性要点

- **GA**：SDK `11.0.100` / Runtime `11.0.0`（`releases-index.json` 显示 11.0 通道 `active`）；
  Visual Studio **18.12.0** 起随装。
- **支持策略**：.NET 11 为 **STS**，与 .NET 10（LTS）同样在 **2028-11** 前后到期 —— 迁过来并不延长支持窗口。
- **最低硬件要求（重要）**：x64 基线要求 `x86-64-v2`，Windows Arm64 要求 `LSE`；
  ReadyToRun 编译目标在 Windows x64 抬到 **x86-64-v3**（老旧 CPU 上部分预编译方法退回 JIT，冷启动变慢）。
- **Runtime Async**：.NET 11 的主推特性，但仍是 **preview**，需 `<Features>runtime-async=on</Features>` 显式开启；
  官方 issue `dotnet/runtime#136004` 记录了开启后 **中断性 GC 崩溃**（`0xC0000005` / `Internal CLR error`），
  至今未修。**本项目未开启此特性。**
- 与桌面应用相关的 breaking change：主要落在 ASP.NET Core / EF Core / System.Text.Json 流式序列化 /
  NamedPipeClientStream / NativeAOT 产物命名；本项目未命中任何一条（编译即验证）。

---

## 3. 实验分支做了什么

`exp/dotnet11`（单条提交 `6e7fd3f`）：

```
src/Desktop/BiliCopilot.UI/BiliCopilot.UI.csproj
- <TargetFramework>net10.0-windows10.0.22621.0</TargetFramework>
+ <TargetFramework>net11.0-windows10.0.22621.0</TargetFramework>
```

- Windows 目标平台后缀（`-windows10.0.22621.0`）保留；
- 其余 5 个工程不动（netstandard2.0 / net9.0）；
- 任何包版本不动，无临时补丁。
- SDK 隔离安装：`D:\dotnet11`（`11.0.100`），机器默认 SDK 仍是 `D:\dotnet` 的 `10.0.403`，互不影响。

---

## 4. 构建与打包验证

| 环节 | .NET 10 | .NET 11 | 结论 |
|---|---|---|---|
| Debug 构建（x64） | 0 error（174s 增量） | **0 error** | 通过 |
| Release 构建（x64） | 未单独计时 | **0 error（226s）** | 通过 |
| 包兼容警告（NU1701/NU1608/NETSDK） | 无 | **无** | 无降级取包 |
| 产物 runtimeTarget | `.NETCoreApp,Version=v10.0/win-x64` | **`.NETCoreApp,Version=v11.0/win-x64`** | 目标正确 |
| MSIX 打包（`GenerateAppxPackageOnBuild`） | **失败**：`Platform linker not found`（ILCompiler 10.0.14） | **失败**：`vswhere.exe not found`（ILCompiler 11.0.0） | **两边同样失败 → 环境缺 MSVC Build Tools，不是 .NET 11 回归** |

即：代码层与包层**没有**出现不兼容；真正卡住打包的是本机缺 Visual Studio / MSVC 链接器（`PublishAot=True` 需要）。

---

## 5. 运行时功能验证

| 项 | 结果 | 证据 |
|---|---|---|
| 启动 | 通过 | 3 次冷启动均出窗口，未挂起（`IsHungAppWindow=False`） |
| Reactor 页面 | 通过 | `导航项被点击：BiliCopilot.UI.Pages.VideoPartitionPage` |
| 视频播放 | 通过 | `初始化：创建播放器` → `初始化：播放器就绪`（16:16:07 → 16:16:15），`Load.MpvInit 7383.97ms` |
| 直播 | 通过 | 直播弹幕列表持续构建（`直播弹幕列表构建：0 页 N 条`） |
| 弹幕 | 通过 | 同上；16:16:06 起有列表构建记录 |
| 关闭重启 | 通过 | 评估中反复 kill/relaunch ≥6 次，无残留进程、无启动失败 |
| 崩溃 | 未发生 | `%LOCALAPPDATA%\CrashDumps` 计数 **0**；事件日志无 WER |
| 打包安装 | 未验证 | 见 §4，卡在环境缺链接器（两边一致） |
| 长时间稳定性 | 未验证 | 单次运行 < 30 分钟，属短态验证 |
| ARM64 / x86 | 未验证 | 本机只有 x64 环境 |

日志里的 `Failed to load image from ...` 属于图片下载失败，与运行时版本无关，.NET 10 下同样存在。

---

## 6. 性能对照（同机、同包、同为 Debug x64）

### 启动（进程 → 主窗口可见）

| 轮次 | .NET 10 | .NET 11 |
|---|---|---|
| 1（含冷盘/首次） | 12.98s | 5.42s |
| 2 | 3.48s | 3.22s |
| 3 | 3.20s | 2.95s |

排除首轮（磁盘缓存未热）后：**3.20–3.48s vs 2.95–3.22s**，.NET 11 略快 0.2–0.3s，
但样本各只有 2 次有效值，**不足以当作结论**。

### 稳定态资源（启动后 ~20s）

| 指标 | .NET 10 | .NET 11 |
|---|---|---|
| 工作集 | 226 MB | 229 MB |
| 私有内存 | 138 MB | 138 MB |
| 线程数 | 72 | 72 |
| 句柄 | 1495 | 1502 |

**基本一致，差值在噪声范围内。**

### UI 响应（应用自带 `PERF UiLag` 采样）

| 指标 | .NET 10 | .NET 11 |
|---|---|---|
| p50 | 0.12–0.16 ms | 0.19–0.60 ms |
| p95 | 3.3–4.5 ms | 3.3–30 ms（有一次 30ms 尖峰） |

同级水平；.NET 11 采样噪声更大，未见系统性劣化。

### 播放器

`Load.MpvInit` 在两次 .NET 10 运行里分别是 **622ms** 和 **9319ms**，.NET 11 两次是 **7384ms** 和 **9319ms**
—— 波动完全来自网络/画质解锁，**与运行时无关**，不能当作回归证据。

### GC

**未测**：本次没有跑 `dotnet-counters` / `dotnet-trace`，无法给出 GC 次数、暂停时间、代际分布。
若要拿"GC 更好"当迁移理由，这一项必须先补测。

---

## 7. 兼容性风险清单

| 风险 | 等级 | 说明 |
|---|---|---|
| 支持窗口没有变长 | 高（决策级） | .NET 11 是 STS，2028-11 到期，与 .NET 10 LTS 同年；迁过去只是把下一次迁移提前到 2027 |
| 硬件底线抬高 | 中高 | x64 需要 x86-64-v2、R2R 目标是 x86-64-v3；老 CPU 用户冷启动变慢甚至不可用。项目是分发给 B 站用户的应用，这一条要当产品问题看待 |
| Runtime Async 未稳定 | 中 | `dotnet/runtime#136004`：开启后中断性 GC 崩溃。**不要**在正式构建里打开 `runtime-async=on` |
| 三方预览包未对 net11 承诺 | 中 | Reactor `0.1.0-preview.17`、MpvKernel/BiliKernel `0.0.2-preview2`、WinUIKernel `0.0.12-preview3` 编译与运行都通过，但这些包的上游没有 net11 兼容声明，属"实测可行、无保障" |
| 源生成器（CsWinRT 2.2.0 / CsWin32 0.3.257） | 中低 | 本次在 SDK 11 的 Roslyn 下正常产出；但仓库开了 `TreatWarningsAsErrors` + `AnalysisMode=AllEnabledByDefault`，SDK 补丁升级一旦带来新诊断就会直接构建失败 |
| CI 未升级 | 中 | `setup-env/action.yml` 仍钉 `dotnet-version: 9.0.x`（本来就落后于 net10 TFM）；迁移必须同步升到 11.0.x，并把镜像换成带 VS 18.12 的 Windows 镜像 |
| NativeAOT 打包需要 MSVC | 中 | `PublishAot=True` 决定打包链要 vswhere/linker。本机没有，两个运行时都失败——上线前必须确认 CI 镜像里有 VS Build Tools |
| 无回归测试兜底 | 中 | `BiliCopilot.Moment.Tests` 是 net9.0 的小测试集，覆盖不到 Reactor/播放器/直播；功能验证只能靠手动或 UI 自动化 |

---

## 8. 迁移收益（客观）

1. **成本极低**：一行 TFM，零源码改动，零包升级 —— 这一点已由实验分支证明。
2. **运行时新能力**：Runtime Async（待稳定）、R2R 默认比较器特化（官方称集合操作最多 20×）、
   ExecutionContext 跳过、.NET 11 SDK 的 MSBuild 评估快 10–13%、分配少 16–34%。
3. **不改变现状的部分**：启动、内存、UI 延迟实测持平；AOT/R2R 改良对本项目**暂未观测到可量化收益**（因为 `PublishReadyToRun=False`）。

净判断：**收益主要是"以后能力"和"构建侧小改善"，对终端用户体验没有可见提升。**

---

## 9. 回滚方案

| 场景 | 动作 | 已验证 |
|---|---|---|
| 代码层 | `git checkout master`（`exp/dotnet11` 是独立分支，master 一个字节没动） | ✅ |
| 运行时层 | 机器默认 SDK 一直是 `10.0.403`；`D:\dotnet11` 只用于实验构建，删掉即可 `Remove-Item D:\dotnet11 -Recurse` | ✅ |
| 应用层 | 用 .NET 10 重新构建并注册（本次结束时已恢复为 .NET 10 的 `1.0.24.0`） | ✅ |
| 分支层 | 保留 `exp/dotnet11` 供后续复测；不需要时 `git push github --delete exp/dotnet11` | — |

---

## 10. 建议的下一步（如果将来要迁）

1. 等 **11.0.1 或更高**（确认 `dotnet/runtime#136004` 关闭）；
2. 在 CI 里装 .NET 11 SDK + VS 18.12（含 MSVC），把 `setup-env` 的 `dotnet-version` 一并纠到与 TFM 一致；
3. 迁移前补三项测量：`dotnet-counters`（GC 次数/暂停）、R2R 打开后的冷启动、老 CPU（x86-64-v2）上的启动；
4. 决定是否接受"STS 每年一跳"的节奏；若不接受，正确做法是**留在 .NET 10 LTS，等 .NET 12（LTS）再跳**。
