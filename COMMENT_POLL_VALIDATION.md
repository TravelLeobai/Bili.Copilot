# 评论内嵌投票：验证记录

日期：2026-11-23
分支：`feat/comment-poll`
所有命令都在本机（Windows，.NET SDK 11.0.100 在 `D:\dotnet`）实跑过；输出为实际结果，不是预期值。

---

## 1. 单元测试

新建 `tests/BiliCopilot.CommentPoll.Tests`（xunit 2.7.0 / xunit.runner.visualstudio 2.5.7 / Microsoft.NET.Test.Sdk 17.10.0 / Microsoft.Extensions.Logging.Abstractions 9.0.2，全部与 `src/Directory.Packages.props` 里钉的版本一致，没有升级或新增版本）。

测试工程把服务层的 5 个源文件直接 `Compile Include` 进来（和 `tests/BiliCopilot.Diagnostics.Tests` 的做法一致），**所有 HTTP 都走可注入的 `FakeHttpHandler`**：不发真实请求，`do_vote` 只出现在假 handler 里。

命令：

```
cd tests\BiliCopilot.CommentPoll.Tests
D:\dotnet\dotnet.exe test
```

实际结果：

```
已通过! - 失败: 0，通过: 59，已跳过: 0，总计: 59，持续时间: 61 ms
```

覆盖到的用例（59 个）：

| 分组 | 用例 |
|---|---|
| 正文解析 | 普通文本（原样返回、不 Trim）、`{vote:123}你好`、`你好{vote:123}`、中间夹占位符、投票+表情混排、多个标记（取第一个 id、全部剥掉）、空 id、非数字 id、超大 id（不抛异常）、`{vote:0}`、大小写变体、花括号里带空格、前导零正规化、null/空串 |
| 详情解析 | 真实响应样本（标题/人数/单选/发起人/0 票选项补 0/总票数）、`options` 缺失、`data` 为 null、只有 `{"status":4}`、`my_votes` 在 `data` 层、在 `vote_info` 层、重复与负值丢弃、`only_fans_level=10`、已结束（`end_time` 过去）、`end_time` 脏数据 → null、`choice_cnt` 钳位（0→1、9→选项数）、数字给成字符串、`img_url` 升 https、标题空白 → 不可用 |
| 详情请求 | 真实响应解析成功、已删除 → null、`code 260010` → null、服务端 code → 拒绝、HTTP 500 → 抛异常（**不是**不可用）、HTTP 412 → 专用异常且只发一次、非法 id（`0`/`-1`/`abc`/空）→ null 且不发请求、二次调用走缓存（只发一次）、同一投票并发查询只发一次（`TaskCompletionSource` 卡住第一条）、缓存上限 64 条（65 条后最旧的被挤掉，再问会重新请求） |
| 提交 | 带新票数 → 合并（保留旧标题、票数/人数/my_votes 用新的）、请求体/`csrf`/`Origin`/`Referer`/`Cookie` 逐项断言、匿名 → `"status":1`、不带票数 → 重查（断言 POST 后跟一个 GET）、重查失败 → 本地 +1、响应不是 JSON → 按成功处理并本地 +1、`-101`/`100` → `UnauthorizedAccessException`（只发一次）、其它 code → `CommentVoteRejectedException`（带 code/reason）、412 → `CommentVoteForbiddenException` 且 handler 只被调用一次、没有 `bili_jct` → 未登录且不发请求、没有 uid → 未登录且不发请求、选项不属于该投票 → `ArgumentException` 且不发请求、单选投两项 → `ArgumentException` 且不发请求、提交成功后缓存失效（下一次必重新请求） |

没测到的：TTL 到期（3 分钟）没有单测（`DateTimeOffset.UtcNow` 没做可注入时钟），只有代码阅读与缓存可用的实证。

---

## 2. 构建

命令（TMP/TEMP 指到 E 盘，C 盘空间不够）：

```
$env:TMP="E:\build-temp-dsh"; $env:TEMP="E:\build-temp-dsh"
cd src
D:\dotnet\dotnet.exe build Desktop\BiliCopilot.UI\BiliCopilot.UI.csproj -c Debug -p:Platform=x64 ^
  "-p:SolutionDir=E:\...\bili-copilot-v2\src\"
```

结果：**成功**（多次；最终一次耗时 1 分 38 秒，输出里没有 error；warning 我没有逐条统计）。

中途真实撞到并解决的三个问题（不是编的）：

1. `CS0104 Symbol 不明确`：`FluentIcons.Common.Symbol` 与 `Microsoft.UI.Xaml.Controls.Symbol` 冲突，加 `using Symbol = FluentIcons.Common.Symbol;`。
2. `RCS1194`（异常类要实现标准构造）与 `VSTHRD103`（async 里同步 `Cancel()`）：补三个标准构造；把 `Cancel()` 挪进同步的 `CancelPendingLoads()` 辅助方法。
3. `PRI210 / 0x800704c8 File move failed`：`resources.pri` 被正在跑的应用锁住。按仓库已知做法：`taskkill` 掉 `BiliCopilot.UI`，把 `src\_build\x64\Debug\BiliCopilot.UI\bin\resources.pri` 改名 `.old`，重跑构建。

Release 没有构建（见未验证清单）。

---

## 3. 真实只读 API 验证

两条请求都是**匿名 GET**，不带 Cookie，不写任何东西。

原始响应（`Invoke-WebRequest`，2026-11-23）：

```
GET https://api.bilibili.com/x/vote/vote_info?vote_id=20000000   → HTTP 200, 452 bytes
{"code":0,"message":"OK","ttl":1,"data":{"vote_info":{"vote_id":20000000,"title":"刚学的都还给老师了吗","join_num":4,"choice_cnt":1,"end_time":3929006537,"status":1,"vote_publisher":85642211,"default_share":1,"ctime":1781522890,"biz_type":2,"options":[{"opt_idx":1,"opt_desc":"全还回去了","cnt":4},{"opt_idx":2,"opt_desc":"我能全文背诵"}],"options_cnt":2,"voter_level":6,"face":"https://i2.hdslb.com/bfs/face/...jpg","name":"燕子不会离开"}}}

GET https://api.bilibili.com/x/vote/vote_info?vote_id=123456789  → HTTP 200, 67 bytes
{"code":0,"message":"OK","ttl":1,"data":{"vote_info":{"status":4}}}
```

再用一个**临时探针工程**（`E:\build-temp-dsh\poll-probe`，把服务层源码编进去，跑完已删除）验证「服务层能不能解析」，实际输出：

```
[20000000] title=刚学的都还给老师了吗
       participants=4 maxChoice=1 ended=False endTime=2094-07-03T14:42:17.0000000+00:00
       publisher=燕子不会离开(85642211) supporterOnly=False myVotes=[] total=4
       #1 全还回去了 => 4
       #2 我能全文背诵 => 0
[123456789] -> null (不可用/已删除)
[0] -> null (不可用/已删除)
正文抠出 id=20000000，剥完=[刚学的都还给老师了吗]
```

要点：0 票的选项补成 0（不是丢掉）、`end_time` 到 2094 年照实解析、已删除的返回 `null` 而不是抛异常、`vote_id=0` 连请求都不发。

---

## 4. UI 验证：做了什么，怎么做的

### 4.1 真机（注册运行真实应用）

Debug 构建 → 改 `bin\AppxManifest.xml` 版本号（1.0.29 → 1.0.33，比已装的 1.0.28 高）→ `Add-AppxPackage -Register` → `Start-Process 'shell:AppsFolder\...!App'`。

在真机上确认过的（截图逐张看过）：

1. **评论列表正常**：动态页打开评论浮层，头像、正文、表情图、附图、点赞数、回复数、转发、回复按钮、输入框都在，卡片行高与间距和改之前一致（正文下方没有多余空白，因为没投票时入口是零高度占位）。
2. **楼中楼正常**：点「7 条回复」进入详情，主评论 + 回复列表（含「回复 @某人：」）+ 返回箭头 + 输入框都正常。
3. **占位符不再裸露**：给评论强行挂上 `{vote:20000000}`、`{vote:probe-multi}`、`{vote:probe-mine}` 等占位符后，正文里**看不到 `{vote:`**，正文下方出现投票入口（图标 + 「投票」，点开过一次后变成「标题 + 投票 · N 人参与」）。
4. **真实数据的面板**：点开 `vote_id=20000000` 的入口，弹出的面板是**真实接口数据**：标题「刚学的都还给老师了吗」、「单选 · 4 人参与」、两个选项（圆点未选）、匿名投票、投票按钮（灰的，因为没选任何项——`CanExecute` 生效）。整个弹出是 `Button` + `Flyout`，宽 340，出现在入口下方。
5. **各状态**：另外四种状态用临时注入的假数据在同一个真机界面里看过（见 4.2）。
6. **主题**：面板在深色下正常；把组件自身 `RequestedTheme` 设成 `Light` 后重新截图，面板变浅色（白底、深色字、边框/进度条颜色跟着换），说明面板只依赖 `ThemeResource`（见 4.2 的说明——这里不是把整个应用切到浅色主题）。

### 4.2 临时探针（不进仓库，已删除）

真机上找不到「带投票的评论」（扫了 8 个热门视频共 24 页评论，`{vote:` 零命中；用搜索接口找同题视频被风控 412 挡回，没有继续硬碰），所以按任务允许的方式用了临时探针：

- 在 `CommentItemViewModel` 构造末尾加了一段临时代码：读 `LocalState\poll-probe.txt` 里的清单，依次给前几条评论的正文尾部追加 `{vote:<id>}` 并挂上对应的 `CommentVoteViewModel`（走的还是**真实的** `BuildDisplayContent` 剥离逻辑）；
- 在 `CommentVoteService.GetDetailAsync` 顶部加了一段：命中 `probe-multi` / `probe-mine` / `probe-ended` / `probe-fail` 时返回预设详情或抛异常，用来覆盖真机上拿不到的状态；
- 深色/浅色各截了一轮，验证完**两处临时代码与临时文件全部删除**，重新构建、重新注册、`git status` 里没有任何 `POLLPROBE` 痕迹（已 grep 确认）。

用这套办法看过的状态（每张都看过像素）：

| 状态 | 看到的样子 |
|---|---|
| 单选未投（真实数据） | 标题 + 「单选 · 4 人参与」+ 两个圆点选项 + 匿名投票 + 灰的投票按钮 |
| 多选未投 | 「最多选 2 项 · 10 人参与 · 2031/11/23 18:15 截止」+ 三个方框选项 + 状态「还可以选 2 项」+ 灰的投票按钮 |
| 已投票 | 结果行：票数 + 占比 + 进度条，自己投的那项右侧有勾选标记（「一般 3 票 · 25%」带 ✓） |
| 已结束 | 「单选 · 8 人参与 · 已结束」+ 结果行，没有投票按钮 |
| 加载失败 | 标题回退成「投票」+ ⚠「投票没有加载出来」+ 重试按钮 |
| 不可用（真实 `123456789`） | 「这个投票已被删除，或者暂时无法查看」，没有选项区 |
| 浅色主题 | 上面那张单选面板在 `RequestedTheme=Light` 下的样子（白底/深字/浅色边框） |

**注意**：`probe-fail` 这条只验证了「UI 怎么显示失败」，它的异常是注入的，不是真实网络失败；真实网络失败的错误分类由单测（HTTP 500 → 抛异常）覆盖。

### 4.3 UI 自动化的坑（记录一下）

- Reactor 评论浮层里的 `SelectionItemPattern.Select()` 会抛 `RPC_E_SERVERFAULT`（和已知结论一致）。
- 但 `Button` 的 **`InvokePattern.Invoke()`** 和 `Hyperlink` 的 `Invoke()` 都正常，可以在**不抢焦点、不动鼠标**的情况下点开评论浮层、投票入口、「N 条回复」。
- 弹窗（`Flyout`）是**独立的窗口**（类名 `Microsoft.UI.Content.PopupWindowSiteBridge`），要单独 `PrintWindow` 才能截到；窗口被别的程序挡住时用 `PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT)` 仍能抓到内容，不需要把应用拉到前台（这点很重要：验证时机器上还有别人的会议窗口）。
- 合成鼠标点击在窗口被遮挡时不可靠（会点到底下的程序），所以整个过程尽量走 UIA 的 Invoke。

---

## 5. 回归检查（做了哪些、没做到哪些）

做到：

- 评论列表渲染（正文/表情/附图/头像/相对时间/置顶逻辑未动）、点赞数显示、回复数链接、转发按钮、回复按钮、评论输入框都在（截图确认）。
- 楼中楼进入/退出（点「N 条回复」→ 详情列表 + 返回箭头）正常。
- 卡片布局没有变化：无投票时投票入口高度为 0，动作行紧贴正文下方（改前改后截图的间距一致）。
- 滚动列表时的虚拟化没有报错（日志里没有新的异常；不过**没有**逐帧验证「回收后旧任务覆盖新数据」这个场景，只靠代际号代码保证 + 单测覆盖不到 VM）。

没做到（如实写）：

- **没有点过点赞按钮**（那是对真实账号的写操作，不该为了验证去点别人的评论）。
- 没有发真实评论/回复（同上）。
- 没有在播放器页（`PlayerCommentSection`）里验证过——只在动态页的评论浮层里验证；两处都 `Mount(new CommentPanel(...))`，代码路径相同，但没有实机看。
- PGC 播放器侧栏那条 XAML 路径（`CommentMainPanel` → `CommentItemControl`）**没有实机验证**：改动只是把正文绑到 `DisplayContent` 并在正文下方加了同一个入口控件，构建通过但没运行到。

---

## 6. 未验证清单（明确没做/没验的）

1. **真实提交投票**：完全没有对真实账号发过 `do_vote`。提交路径的正确性只由单测（假 handler）保证；真实服务端对 body 字段/风控的实际反应未知。
2. **真实带投票的评论**：没找到。所以「入口出现在真实投票评论上」这件事，是靠临时注入验证的，不是靠真实数据。
3. **浅色主题整应用**：只验证了组件在 `RequestedTheme=Light` 下的渲染，没有把整个应用/系统切到浅色跑一遍。
4. **键盘实操**：Tab/Enter/Space 的行为是照控件默认能力设计的（入口是 Button，选项是 ToggleButton，焦点在提交中/失败后会转移），但**没有用真键盘逐项走过**；读屏播报（`LiveRegionChanged`）也没有用讲述人实测。
5. **Release 构建**：只构建了 Debug。
6. **打包/发布**：本机没有 VS/MSVC，`GenerateAppxPackageOnBuild`（PublishAot）必然失败，没有尝试；注册运行用的是 `Add-AppxPackage -Register`（开发模式注册目录）。
7. **图片投票**：界面支持 `img_url`，但没拿到真实图片投票数据，`BasicCoverImage` 那条分支没跑过。
8. **多选的具体交互边界**：单测覆盖了「单选投两项 → 拒绝」「选项不属于投票 → 拒绝」，但 UI 上「选满 2 项后再点第三项没反应」只看了代码（`CanChoose`），没有逐项点。
9. **TTL 到期**：3 分钟缓存过期没有单测。
10. **提交中途关闭面板**：设计上不取消提交请求（票可能已发出），但没有在真机上故意制造这个场景。
