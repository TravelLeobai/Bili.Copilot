# BiliFluent 评论内嵌投票：上游实现研究

研究对象：`Better-Native-APPs/BiliFluent`，参考版本 `v2.2611.8.2-pre`（`ad74c24 chore(release): 2.2611.8.2 pre-release`）
调查日期：2026-11-23
调查方式：本机克隆仓库（`E:\build-temp-dsh\BiliFluent`）后阅读源码与提交差异；协议部分用匿名 HTTP 请求实测。

**只写读到的和测到的。** 没确认的地方明确标出来。

---

## 1. 相关提交

用 `git log -S"vote"` / `-S"{vote:"` / `-S"VoteId"` 定位到这样一串（从旧到新，只列与本功能直接相关的）：

| SHA | 日期 | 标题 | 与本功能的关系 |
|---|---|---|---|
| `be76e0a` | 2026-11-17 19:52 | `feat(kernel): carry comment polls and add poll detail and voting` | **内核层**：把评论自带投票暴露出来，新增详情与提交接口 |
| `39eb7ec` | 2026-11-17 19:53 | `feat(comment): show polls in comments and vote in the app` | **界面层**：评论下的投票入口 + 弹出面板 + 投票交互 |
| `f3f9cad` | 之后 | `docs(changelog): record the fixes and additions since 2.2611.8.1` | 只在 CHANGELOG 里记录，无代码 |

注意区分：`5c8358a` / `72bba20` / `e19090b` / `7773b6a` / `30c27c0` / `3b5b2cf` 这一串是**互动弹幕（command danmaku）里的投票**，与评论投票不是同一条链路，虽然共用同一套 `CommentVoteRejectedException` 与相同的 `/x/vote` 接口。本报告只谈评论投票。

后续修复：`git log --oneline be76e0a..HEAD -- '*CommentVote*'` 与 `-- '*CommentVoteViewModel*'` 无输出，即这两个提交之后**没有针对评论投票的修补或回退**。它们随 `2.2611.8.1/2.2611.8.2` 一起发版。

上游 CHANGELOG 自己在 `f3f9cad` 里写明了提交投票「尚未经过充分实际使用验证」，与源码里对服务端返回码的兜底处理（见 §5）一致。

---

## 2. 数据来源：占位符 + 结构化字段

评论列表来自评论 gRPC（`bilibili.main.community.reply.v1.Reply`）。带投票的评论，正文（`Content.message`）里是 **`{vote:<数字ID>}` 占位符**，投票本体在 `Content.vote`。

proto 实证（仓库内 `src/Libs/BiliKernel/src/BiliKernel.Grpc/bilibili/main/community/reply/v1.proto`）：

```proto
message Content {
  string message = 1;
  map<string, Member> members = 2;
  map<string, Emote> emotes = 3;
  map<string, Topic> topics = 4;
  map<string, Url> urls = 5;
  Vote vote = 6;          // ← 评论自带的投票
  map<string, int64> at_name_to_mid = 7;
  RichText rich_text = 8;
  repeated Picture pictures = 9;
}

message Vote {
  int64 id = 1;
  string title = 2;
  int64 count = 3;        // 参与人数
}
```

所以评论列表**自带**这三样：投票 ID、标题（可能为空）、参与人数（可能为 0）。**选项、票数、占比、截止时间、我投了谁，评论列表都没有**，必须再请求一次详情。

### 解析方式（`CommentVoteAdapter.cs`）

```csharp
private static readonly Regex _placeholderRegex = new(@"\{vote:([0-9]+)\}", RegexOptions.CultureInvariant);
```

- `RemoveVotePlaceholders(message)`：只在文本里出现 `{vote:` 时才走正则替换，替换后 `Trim()`；没有占位符时**原样返回同一个字符串实例**，不碰其它文字。
- `ToCommentVote(Content content)`：优先用 `content.Vote.Id > 0` 的结构化数据；**没有结构化字段时回退**到从占位符正则里抠 ID（`new CommentVote(match.Groups[1].Value)`，此时标题与人数为空）。
- 占位符剥离的调用点在 `EmoteAdapter.cs:33`（`text = CommentVoteAdapter.RemoveVotePlaceholders(text);`）——即**在富文本切分成 Emoji/文本片段之前**先摘掉占位符，所以表情、@、链接的分段不受影响，正文也不会漏出 `{vote:…}`。

`CommentVote` 只有 `Id` / `Title?` / `ParticipantCount?` 三个属性，`Equals` 按 Id 比较——用于「同一条投票在多处出现时判等」。

---

## 3. 详情接口

`BiliApis.cs` 新增两个常量：

```csharp
public const string VoteInfo = _apiBase + "/x/vote/vote_info";
public const string DoVote   = _apiBase + "/x/vote/do_vote";
```

`CommentVoteClient.GetVoteDetailAsync`：

- `GET https://api.bilibili.com/x/vote/vote_info?vote_id=<id>`
- 鉴权：`RequireCookie = true`、`ForceNoToken = true`、`ApiType = None`。注释写明「不登录也能看，Cookie 只用来补上『我投了哪些』」。
- 返回 `data.vote_info` 交给 `CommentVoteParser.ParseDetail`；无法解析（含 `data` 里只有 `status` 的已删除投票）返回 `null`。
- `code == 260010`（存在但不可展示）也返回 `null`，不抛异常。

### 实测（匿名请求，2026-11-23）

```
GET /x/vote/vote_info?vote_id=20000000
{"code":0,"message":"OK","ttl":1,"data":{"vote_info":{
  "vote_id":20000000,"title":"刚学的都还给老师了吗","join_num":4,"choice_cnt":1,
  "end_time":3929006537,"status":1,"vote_publisher":85642211,"default_share":1,
  "ctime":1781522890,"biz_type":2,
  "options":[{"opt_idx":1,"opt_desc":"全还回去了","cnt":4},{"opt_idx":2,"opt_desc":"我能全文背诵"}],
  "options_cnt":2,"voter_level":6,"face":"https://i2.hdslb.com/...jpg","name":"燕子不会离开"}}}
```

要点（都来自实测响应，不是推测）：

- 选项序号 `opt_idx` **从 1 开始**，提交时用的就是这个值。
- 票数为 0 的选项**没有 `cnt` 字段**（服务端省略零值），解析必须把「缺字段」当 0。
- 匿名请求**没有 `my_votes`**；上游注释说登录后它出现在 `data` 或 `vote_info` 里（两处都读，取第一个非空）。
- 已删除/不存在的 id（如 `vote_id=123456789`）返回 `{"code":0,...,"data":{"vote_info":{"status":4}}}`——**code 依然是 0**，靠字段缺失判断失效，这就是 `ParseDetail` 要求 `title` 非空且 `options` 非空的原因。
- `status`：`1` 活动（`vote_id=5000000`、`20000000` 都是 1），`4` 已结束/关闭（`vote_id=1`、`400000` 是 4）。
- `end_time` 可能是极远的未来（`3929006537` ≈ 2094 年），上游因此约定「超过 3650 天就不显示截止时间」。

### 字段映射（`CommentVoteParser.ParseDetail`）

| 上层属性 | 来源字段 | 缺省处理 |
|---|---|---|
| Id | `vote_id` | ≤0 视为不可用 |
| Title | `title` | 空白则视为不可用 |
| Options | `options[].opt_idx / opt_desc / cnt / img_url` | `opt_idx` 不在 1..int.Max 的丢弃；缺项丢弃 |
| MaxChoiceCount | `choice_cnt` | 钳制到 `[1, options.Count]` |
| ParticipantCount | `join_num` | 负值归 0 |
| MyVotes | `my_votes`（先 `data` 后 `vote_info`） | 去重、只保留正数 |
| EndTime | `end_time` | `0` 或超过 253402300799（9999-12-31）则 null |
| PublisherId / PublisherName | `vote_publisher` / `name` | 0 则为 null |
| Description | `desc` | 空白则 null |
| IsSupporterOnly | `only_fans_level >= 10` | — |

图片选项（`img_url`）：`//` 开头补 `https:`，`http://` 升到 `https://`，非 https 一律丢弃。

---

## 4. 提交接口

`CommentVoteClient.SubmitVoteAsync`：

- `POST https://api.bilibili.com/x/vote/do_vote?csrf=<bili_jct>`
- Body 是**手拼的 JSON 字符串**（不是表单）：
  ```json
  {"vote_id":20000000,"votes":[1],"voter_uid":177800289,"status":0,"op_bit":0,"dynamic_id":0}
  ```
  `status` 是 `anonymous ? 1 : 0`。
- 请求头补 `Origin: https://t.bilibili.com`、`Referer: https://t.bilibili.com/vote/h5/index/`。
- 鉴权：CSRF 令牌取自 `bili_jct` Cookie，**放在 query string 里**，不放 body；Cookie 由 authenticator 统一加。
- 提交前本地校验：选项数量 1..`MaxChoiceCount`、每个序号都存在、`voter_uid` 能解析成正整数，否则抛 `ArgumentException` / `UnauthorizedAccessException`（不浪费请求）。

### 返回处理（这是移植时最容易看漏的一段）

1. 正常返回：读 `data.vote_info.options` 的新票数、`my_votes`、`join_num`，然后 `Merge` 到原对象上——**只取票数，其余描述仍用旧值**，避免服务端只回增量时把标题清空。
2. `data` 里没有可用 options：等 600ms 再 `GetVoteDetailAsync` 重查一次（官方页面就是这么补的），拿到就补上 `MyVotes`。
3. 重查也失败：**不报失败**，在本地把选中项 +1、参与者 +1 后返回——因为「票确实投出去了，只是结果没读到」。
4. `code` 属于未登录（`-101` / `100`）→ `UnauthorizedAccessException`；其它非 0 → `CommentVoteRejectedException(code, reason)`，`IsSupporterOnly` 由 reason 文案判断。
5. 响应不是合法 JSON：按「投成功了、结果没读到」处理（走第 3 步），不误报失败。

### 上游自己承认的未验证点

- 整个提交链路没有真实账号压测；上游 CHANGELOG 明说「尚未经过充分实际使用验证」。
- 重复投票、已结束后投票、风控 412 这些分支在源码里**没有专门处理**，只依赖服务端返回非 0 code 落到 `CommentVoteRejectedException`，然后重新拉一次详情（`CommentVoteViewModel.SubmitAsync` 的 `catch` 里 `if (ex is CommentVoteRejectedException) await LoadAsync();`）。

---

## 5. 界面实现（`39eb7ec`）

**入口**（`CommentItemControl.xaml`）：正文下面一个 `Button`，`MaxWidth=360`，图标 `Symbol="Poll"`，两行文字：标题 + `投票 · N 人参与`；`x:Load` 绑 `HasVote`，没有投票时完全不实例化。`AutomationProperties.Name` 绑 `EntryName`（读屏念「标题，N 人参与」）。

**弹出面板**：`Button.Flyout` 里放 `CommentVotePanel`（`Width=340`），`Placement=BottomEdgeAlignedLeft`、`ShouldConstrainToRootBounds=False`（小窗口里也能完整展开）；`Flyout.Opening` 才触发加载——**列表滚动时不请求详情**，这是避免 N+1 的关键。

**状态机**（`CommentVoteViewModel`，482 行，`CommentVotePhase` 枚举）：`Idle → Loading → Ready | Failed | Unavailable`。要点：

- `_loadGeneration` 计数：每次加载自增，回包时比对，**旧结果直接丢弃**；提交时也自增，防止并发加载覆盖提交结果。
- 已有详情时再刷新不再显示 Loading（避免面板闪烁），失败也保持旧数据。
- 单选/多选：`MaxChoiceCount > 1` 时最多选 N 个，选满后其余选项 `CanChoose=false`；单选则互斥。
- 只有在「登录 + 不是发起人 + 自己没投过 + 没过期」时才进入可选状态（`IsChoosing`），否则一律展示结果。
- 占比按**总票数**算（多选时总票数 > 人数），四舍五入到整数百分比。
- 票数刷新时若选项集合没变，只更新数字，**保留列表与键盘焦点**（`Apply(detail, keepSelection: true)` + `sameOptions` 分支）。
- 失败文案分档：未登录 / 仅舰长可投 / 服务端给的 reason / 通用失败；`IsStatusFailure` 控制配色。
- 投票成功后 `_justVoted = true`，状态行显示「已投票」。

`CommentVoteOptionViewModel`（84 行）承载单行状态：`Text / Count / Percent / Stats / IsChecked / IsMine / CanChoose / IsChoice / ResultName`。

**隐私/安全**：日志只记异常类型（`LogWarning("Failed to load a comment poll: {ErrorType}", ex.GetType().Name)`），不记 URL、不记 Cookie。

---

## 6. 与当前 Bili.Copilot 的差异（决定移植方式）

| 模块 | BiliFluent | Bili.Copilot（HEAD `dd01a7b`） | 移植策略 |
|---|---|---|---|
| 投票数据模型 | 内核源码里新增 `CommentVote` / `CommentVoteDetail` / `CommentVoteOption` | **无**，且内核是 NuGet 包 `Richasy.BiliKernel.Services.Comment 0.0.2-preview2`，**没有源码可改** | 在 app 层新建等价模型 |
| 富文本解析 | 内核 `EmoteAdapter` 里先剥 `{vote:}` | 渲染走 `Reactor/CommentPanel.cs` 的 `BuildCommentContent`（按 `[表情]` 切 RichTextBlock）+ XAML `CommentItemControl` | 在 app 层的评论模型/渲染入口剥离占位符 |
| 投票详情 API | 内核 `CommentVoteClient`（走内核 `BiliHttpClient` + authenticator） | 无。仓内已有可直接抄的样板：`Services/CommentEmoteEnricher.cs`（自建静态 `HttpClient` + 传 Cookie + `JsonDocument` 手解） | 新建 `Services/CommentVoteService.cs`，照 `CommentEmoteEnricher` 的形状 |
| 投票提交 API | 同上，CSRF 走 authenticator | 无；Cookie 可从现有评论 VM 的上下文拿到（`CommentEmoteEnricher` 就在收 cookie） | 同一 service 里实现 POST，CSRF 从 Cookie 的 `bili_jct` 取 |
| 投票 UI | XAML `CommentItemControl` + `CommentVotePanel` + Flyout | 评论卡片是 **Reactor**（`Reactor/CommentPanel.cs` 的 `CreateCard`），正文用 `XamlHostElement` 托 RichTextBlock | 不重写评论列表；用 `XamlHostElement` 把投票控件挂进卡片（与已有 `CommentTextBox` 同样的做法） |
| 状态更新 | VM 内 `_loadGeneration` + `Apply` | 无对应物 | 新 VM 里实现同一套 generation 语义 |

### 实测确认的一条关键差异

`{vote:` 在这个仓库里**零命中**（`.cs/.xaml/.resw` 全搜），`Vote|vote` 在 `*.cs` 里除公共词外没有相关实现——即当前版本会把占位符**原样显示**出来。这正是上游 `39eb7ec` 修掉的那个问题，本项目也还没修。

### 另需确认的一点（留给实现阶段验证）

本仓内核版本较旧，`Content.Vote` 这个结构化字段**是否存在于 0.0.2-preview2 的 proto**，从 NuGet 包无法直接读源码确认。因此实现按上游的**回退路径**走：优先用结构化字段（若可达），拿不到就从 `{vote:ID}` 占位符抠 ID，再走 `/x/vote/vote_info` 补全——这条路不依赖内核是否带 Vote 字段。

---

## 7. 协议小结（可直接照着实现）

| 用途 | 请求 | 关键参数 | 鉴权 |
|---|---|---|---|
| 详情 | `GET https://api.bilibili.com/x/vote/vote_info` | `vote_id`（正整数） | 可匿名；带 Cookie 才回 `my_votes` |
| 提交 | `POST https://api.bilibili.com/x/vote/do_vote?csrf=<bili_jct>` | JSON body：`vote_id`、`votes`（选项序号数组）、`voter_uid`、`status`（匿名=1）、`op_bit=0`、`dynamic_id=0` | 必须登录 + CSRF |

失效/结束/不可参与的判断依据（按可靠性排序）：`title`/`options` 缺失 → 不可用；`end_time <= now` → 已结束；`status == 4` → 已结束；`my_votes` 非空 → 已投；`vote_publisher == 自己` → 不能投自己；`only_fans_level >= 10` → 仅舰长。
