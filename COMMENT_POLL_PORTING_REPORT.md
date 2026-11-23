# 评论内嵌投票移植报告（WinUI 3 / Bili.Copilot）

日期：2026-11-23
分支：`feat/comment-poll`（从 `master` 的 `dd01a7b` 拉出）
范围：把 BiliFluent 的「评论内嵌投票」搬到本仓，**只改评论列表相关的数据、服务、视图模型与一个自建控件**，不动播放器、不动评论列表架构、不动任何依赖版本。

---

## 1. 结论先说

- 功能已完整移植：占位符不再裸露、正文下方有投票入口、点开才拉详情、可单选/多选投票、已投/已结束/不可用/加载失败各有状态。
- **投票服务层**（解析 / 查询 / 提交 / 缓存 / 并发 / 错误分类）全部用可注入 `HttpMessageHandler` 的单元测试覆盖，59 个用例全绿。
- **真实只读验证**通过：对 `vote_id=20000000`（进行中）与 `vote_id=123456789`（已删除）各发了一次真实 GET，服务层解析正确。
- **真实提交投票没有做**（也不该由我替账号投），提交路径只有单测里的假 handler 走过。见 `COMMENT_POLL_VALIDATION.md` 的「未验证清单」。

---

## 2. 上游是怎么做的（调研结论，未重复劳动）

上游 BiliFluent 两个提交：

| 提交 | 干了什么 |
|---|---|
| `be76e0a` feat(kernel): carry comment polls and add poll detail and voting | 内核 `CommentInformation` 增加 `Vote`，新增 `CommentVoteParser` / `CommentVoteClient` / 模型 / `CommentVoteRejectedException` |
| `39eb7ec` feat(comment): show polls in comments and vote in the app | 卡片下方入口 + `CommentVotePanel` + `CommentVoteViewModel` / `CommentVoteOptionViewModel` + 22 条 resw 文案 |

协议（匿名实测确认）：

- 详情 `GET https://api.bilibili.com/x/vote/vote_info?vote_id=<正整数>`，可匿名；带 Cookie 才回 `my_votes`。
- 提交 `POST https://api.bilibili.com/x/vote/do_vote?csrf=<bili_jct>`，body 是 JSON `{"vote_id":N,"votes":[...],"voter_uid":UID,"status":0|1,"op_bit":0,"dynamic_id":0}`，另加 `Origin: https://t.bilibili.com`、`Referer: https://t.bilibili.com/vote/h5/index/`。
- 解析要点：`vote_id<=0` / `title` 空白 / `options` 空 ⇒ 不可用；`choice_cnt` 钳到 `[1, options.Count]`；`my_votes` 先看 `data` 再看 `vote_info`，去重只留正数；`end_time` 为 0 或 > 253402300799 视为没有；`only_fans_level >= 10` = 仅充电/舰长可投；0 票的选项没有 `cnt` 字段；已删除的投票 `code` 仍是 0、`data` 里只剩 `{"status":4}`。
- 提交后的结果处理：响应带 `options` → 用新票数合并（标题等保留旧的）；不带 → 等 600ms 重查一次；重查也失败 → 本地 +1（票已投出，不能报失败）；`code` -101/100 → 未登录；其它非 0 → 抛「被拒绝」并带 code+reason；JSON 解析失败按成功算。

互动弹幕投票（`5c8358a` / `72bba20` / `e19090b`）是另一条链路，本仓也没有那套代码，没有混进来。

---

## 3. 本仓的硬约束：内核是 NuGet，拿不到 `Content.vote`

`Richasy.BiliKernel.Services.Comment` 是 `0.0.2-preview2` 的 NuGet 包，仓库里没有源码、proto 也不在手上。用反射确认过内核模型的形状：

```
Richasy.BiliKernel.Models.Comment.CommentInformation
  String Id / EmoteText Content / String RootId / Boolean IsTop / UserDetailProfile User
  DateTimeOffset PublishTime / CommentCommunityInformation CommunityInformation
  CommentTargetType CommentType / String CommentId      ← 没有 Vote
Richasy.BiliKernel.Models.Appearance.EmoteText
  String Text / Dictionary`2 Emotes / IList`1 Pictures
```

所以照上游的**回退路径**实现：从正文里抠 `{vote:ID}`，再打 `vote_info` 把详情补全。也就是说：

- 拿不到「评论列表接口顺带给的投票标题/人数」（那需要内核模型带 `Vote`），入口在第一次打开之前只能显示「投票」；点开一次之后标题与人数会留在入口上。
- 不发额外请求去「猜标题」：那会让列表变成 N+1 请求。

---

## 4. 本仓实现

### 4.1 文件与职责

数据/服务层（纯 BCL，不引用 WinUI，所以能被测试工程直接编进去）：

| 文件 | 职责 |
|---|---|
| `Services/CommentVote/CommentVoteText.cs` | `TryExtractVoteId` / `StripVotePlaceholders` / `HasVotePlaceholder`；严格正则抠 id，宽松正则剥占位符 |
| `Services/CommentVote/CommentVoteParser.cs` | 读 `vote_info` / `do_vote` 的 JSON：选项、票数、`my_votes`、时间、充电专属、图片地址升 https |
| `Services/CommentVote/CommentVoteModels.cs` | `CommentVoteRef` / `CommentVoteOption` / `CommentVoteDetail`（含 `TotalVotes`、`HasEnded`） |
| `Services/CommentVote/CommentVoteExceptions.cs` | `CommentVoteRejectedException(code, reason, IsSupporterOnly)`、`CommentVoteForbiddenException`（HTTP 412） |
| `Services/CommentVote/CommentVoteService.cs` | 详情 GET（缓存 + 三路并发闸门 + 同 id 合流）、提交 POST（合并/重查/本地 +1）、错误分类、缓存失效 |

视图模型：

| 文件 | 职责 |
|---|---|
| `ViewModels/Items/CommentVoteViewModel.cs` | 入口文案、面板状态机（`CommentVotePhase`：Idle/Loading/Ready/Failed/Unavailable）、勾选规则、提交状态、代际号、`Detach`/`Reattach` |
| `ViewModels/Items/CommentVoteOptionViewModel.cs` | 一行选项：记号（圆点/方框）、票数、占比、是否自己选的、能不能勾 |

界面（自建 XAML 控件，用 `XamlHostElement` 挂进 Reactor 卡片）：

| 文件 | 职责 |
|---|---|
| `Controls/Comment/CommentVoteEntryControl.xaml(.cs)` | 正文下面那一行按钮：图标 + 标题 + 「N 人参与」，`Button.Flyout`（宽 340、`ShouldConstrainToRootBounds=False`），`Flyout.Opening` 时才拉详情 |
| `Controls/Comment/CommentVotePanel.xaml(.cs)` | 面板：加载中 / 失败带重试 / 不可用 / 饼图 + 图例 / 状态行 / 匿名 + 投票按钮；读屏播报与焦点转移 |
| `Controls/Comment/CommentVotePie.xaml(.cs)` | 投票饼图（甜甜圈）：按票数分片、0 票留一条能点的缝、点一片就是选这一项；中间显示总票数 |
| `Services/CommentVote/CommentVoteGeometry.cs` | 饼图角度与点击命中的纯算术（有单测） |
| `Toolkits/CommentVotePalette.cs` | 饼图/图例的固定配色（深浅主题都能看清） |

改动到的既有文件：

| 文件 | 改了什么 |
|---|---|
| `ViewModels/Items/CommentItemViewModel/CommentItemViewModel.cs` / `.Properties.cs` | 新增 `DisplayContent`（剥掉占位符的正文）、`Vote`、`HasVote`、`Cookie`；构造时抠 id |
| `ViewModels/Components/CommentMainViewModel/CommentMainViewModel.cs` | 把已有的 cookie 传进评论项 |
| `ViewModels/Components/CommentDetailViewModel/CommentDetailViewModel.cs` | 楼中楼同样带 cookie（楼中楼的评论也可能带投票） |
| `Reactor/CommentPanel.cs` | 正文改用 `DisplayContent`；卡片 body 加一行投票入口（没有投票时是零高度占位，行高不变） |
| `Controls/Comment/CommentItemControl.xaml` | XAML 版卡片（PGC 播放器侧栏还在用 `CommentMainPanel` → `CommentItemControl`）正文改绑 `DisplayContent`，正文下方加同一个入口控件 |

### 4.2 关键设计决策

**占位符解析**：严格正则 `\{\s*vote\s*:\s*([0-9]+)\s*\}`（大小写不敏感）用来抠 id，只认正整数，超长数字 `long.TryParse` 失败就按不可用、绝不抛异常；宽松正则 `\{\s*vote\s*:[^}]*\}` 用来剥正文，`{vote:}` / `{vote:abc}` / 超大 id 都会被剥掉但不显示入口。**没有占位符时原样返回同一个字符串实例**（不 Trim），只有真剥掉了才整理首尾空白。

**加载失败 ≠ 投票不存在**：`GetDetailAsync` 用 `null` 表示不可用（已删除、id 非法、`code 260010`），用异常表示请求失败（HTTP 非 2xx、服务端非 0 code）。面板因此能分开显示「已被删除」与「没有加载出来 + 重试」。

**412 单独识别、绝不重试**：GET 与 POST 都先判 `HttpStatusCode.PreconditionFailed`，抛 `CommentVoteForbiddenException`，请求就此结束（单测断言 handler 只被调用一次）。

**缓存与并发**：`ConcurrentDictionary` 存详情，TTL 3 分钟、上限 64 条（超了丢最旧），提交成功后主动失效该 id；同一时刻最多 3 个详情请求（`SemaphoreSlim`），同一个 id 的在途请求用 `TaskCompletionSource` 合流——两条评论挂同一个投票时只发一次请求。缓存/合流的结果用 `WaitAsync(ct)` 交付给各个调用方，所以关闭面板只会让「等结果的人」放弃，不会打断别人的请求。

**提交后的结果**：响应带 `vote_info.options` → 用新票数合并（标题/说明/截止/发起人仍用旧的，`my_votes` 用响应里的、没有就用自己选的，人数取响应的 `join_num`，没有就 +1）；响应不带 → 等 600ms 重查一次详情（测试里把这个延迟注入成 0）；重查失败或响应不是 JSON → 本地给选中的选项 +1、人数 +1。`code` -101/100 → `UnauthorizedAccessException`，其它非 0 → `CommentVoteRejectedException`（带 code 与 reason，`5200011` 识别为充电专属）。

**生命周期**：VM 有 `_loadGeneration`，旧请求回来发现代际变了就丢弃；`LoadAsync` 自己持有 `CancellationTokenSource`，`Detach()`（面板卸载 / 卡片被回收）时取消在途详情请求并把代际 +1；**提交中的请求故意不取消**（票可能已经发出去了，掐断只会让结果无从知晓），提交完成照常写回 VM（此时 UI 已不在，写回是安全的：控件在 `Unloaded` 里已经退订 `PropertyChanged`）。

**不阻塞 UI**：服务层全是 `async` + `ConfigureAwait(false)`，没有同步等待；没有无界 `Task.Run`；缓存与合流都在内存里完成。

**无障碍/触控/键盘**：入口是标准 `Button`（Tab 可达、Enter/Space 触发、有 `AutomationProperties.Name`，内容里带标题与人数）；面板里选项是 `ToggleButton`（Tab + Space），结果行的 `ProgressBar` 带 `AutomationProperties.Name`（「选项 X，N 票，占 M%」），加载失败/不可用/状态行设了 `LiveSetting=Polite` 并在变化时 `RaiseAutomationEvent(LiveRegionChanged)`；提交中把焦点挪到列表、失败后把焦点送回投票按钮，避免按钮消失导致焦点丢失。全部颜色走 `ThemeResource`（`ControlFillColorDefaultBrush` / `ControlStrokeColorDefaultBrush` / `AccentTextFillColorPrimaryBrush` / `TextFillColorSecondaryBrush` / `SystemFillColorCriticalBrush` / `AccentButtonStyle` / `CaptionTextBlockStyle` / `BodyStrongTextBlockStyle`），浅深色自动跟随。

**没做的取舍（有意）**：文案是中文硬编码（本仓 `Resources` 只有 `zh-Hans-CN` 一份 resw，且 Reactor 版评论面板本来就是硬编码中文）；没有为投票单独加 resw 键，也就没有接入 `ext:Locale`。要本地化时把 `CommentVoteViewModel` 里的字符串与面板里的静态文本换成 resw 键即可，逻辑不用动。

---

## 5. 哪些没法直接复用

1. **内核源码**：`Richasy.BiliKernel.Services.Comment 0.0.2-preview2` 是 NuGet 包，`CommentInformation` 没有 `Vote`，`ICommentService` 也没有 `GetVoteDetailAsync` / `SubmitVoteAsync`。所以上游那 756 行内核改动**一行都用不上**，详情/提交只能在本仓自己发 HTTP。也因此**不能 cherry-pick** `be76e0a`：它改的是内核仓库。
2. **`39eb7ec` 的界面改动**：它往 `CommentItemControl.xaml` 里塞了一个直接绑 `ViewModel.Vote` 的入口（数据来自内核）。本仓的 Reactor 卡片没有 `Vote` 数据源，所以入口改成自建控件 + `XamlHostElement`，XAML 版卡片复用同一个控件。
3. **resw 文案**：上游加了 22 条键；本仓没有对应的 `StringNames` 成员（生成器从 resw 生成枚举，加键可行但会牵动资源文件），这次选择硬编码。

代码是**照结构重写**的：解析语义、状态机、提交后的合并/重查/本地 +1 都按上游行为对齐，但类型、命名、分层、以及「从正文抠 id」这条数据来源都是本仓自己的写法。

---

## 6. 许可与署名

- 本仓 `LICENSE` 与上游 BiliFluent `LICENSE` 都是 **GNU GPL v3.0**，两者兼容；本仓继续以 GPL-3.0 分发。
- 上游仓库里 `src/Libs/BiliKernel` 的源文件头写的是 **MIT**（与仓库根 LICENSE 不一致的混合状态）。本仓**没有复制内核代码**，不存在把 MIT 代码并进 GPL 文件的问题；但如果你以后把那段内核代码搬进来，需要按它自己的头注处理。
- 本次是**按行为与结构重写**，没有 cherry-pick、没有逐行搬运；设计来源是上游两个提交（作者 jams347），协议细节来自匿名实测。建议在提交信息或发布说明里注明「评论内嵌投票的行为与文案参考 BiliFluent 的实现」。
- 中文文案（「投票」「N 人参与」「已结束」「这个投票已被删除，或者暂时无法查看」等）沿用上游同一套说法，属于短语级复用。

---

## 7. 饼图这一版（后续改动）

面板的第一版是「每行一个选项/一条进度条」，之后按需求改成**点击就能投票的饼图**：

- 左边是甜甜圈：还没人投时平均分片，有票之后按票数分；0 票的选项保留 6 度的缝，仍然点得到（`CommentVoteGeometry.MinimumSweep`）。中间写总票数。
- 点一片 = 选这一项（单选替换、多选累计到上限为止），和图例整行是同一个入口（都走 `CommentVoteViewModel.ToggleOption`）。
- **发请求仍然只发生在按「投票」按钮时**：扇形只做选择，不做提交。这样误触不会替真实账号投票。
- 右边图例保留文字、票数、占比、自己选的那项（✓），键盘与读屏走这里；饼图本身只接指针，避免两套焦点打架。

---

## 8. 后续可做

1. 内核（`Richasy.BiliKernel`）如果以后把 `Content.vote` 带出来，入口就能在列表阶段显示真实标题/人数，省掉「点开才知道标题」这一步；本仓的 `CommentVoteRef` 已经把 `Title` / `ParticipantCount` 留成可选字段，接上就行。
2. 文案接回 resw（`StringNames` 生成器加键即可）。
3. 图片投票（`img_url`）的界面已经支持（`BasicCoverImage` + `HasImage`），但没有拿到真实图片投票数据验证过。
4. 饼图的扇区目前不接受键盘焦点（键盘走图例）；如果希望「Tab 进饼图逐片选」，要另外给每个扇区加 AutomationPeer，代价不小。
