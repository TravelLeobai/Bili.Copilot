# B 站动态转发协议调查（BiliUWP-Lite 对照）

> 调查对象：`https://github.com/ywmoyue/biliuwp-lite`，本地克隆 HEAD `ca2caddc597b3faf05b4dac6dcb226aa0694365b`
> （2026-10-25，Merge PR #1496）。
> 结论全部来自源码，标注了文件与行号；未在本机验证的会明确写"未验证"。

---

## 一、BiliUWP-Lite 到底怎么转发

### 1. 请求构造

`src/BiliLite.UWP/Models/Requests/Api/User/DynamicAPI.cs:244-255`

```csharp
public ApiModel RepostDynamic(string dynamic_id, string content, string at_uids, string at_control)
{
    var api = new ApiModel()
    {
        method = HttpMethods.Post,
        baseUrl = $"https://api.vc.bilibili.com/dynamic_repost/v1/dynamic_repost/repost",
        parameter = ApiHelper.MustParameter(AppKey, true),
        body = $"uid={SettingService.Account.UserID}&dynamic_id={dynamic_id}&content={Uri.EscapeDataString(content)}" +
               $"&at_uids={...}&at_control={...}&extension=%7B%22emoji_type%22%3A1%7D"
    };
    api.parameter += ApiHelper.GetSign(api.parameter, AppKey);
    return api;
}
```

调用方：`ViewModels/User/SendDynamic/SendDynamicViewModel.cs:115` 与 `SendDynamicV2ViewModel.cs:115`
（`SendRepost()`），由 `Controls/Dialogs/SendDynamicDialog.xaml.cs:146` 触发。

### 2. 协议形态

- **REST**，`POST`，`application/x-www-form-urlencoded`（body 是拼好的 query 串）。
- URL 上带一串 appkey 参数：`appkey / build / mobi_app / platform / ts / access_key`，末尾 `sign`。
- 签名：`Services/ApiHelper.cs:132-145` —— 参数按 key 排序拼 `k=v&`，去掉末尾 `&`，接 `AppSecret`，取 MD5 小写。
- 必选参数：`Services/ApiHelper.cs:152-164`，`needAccesskey = true` 时在最前面加 `access_key=`。
- 版本号：`Models/Common/SettingConstants.cs:1692` `DEFAULT_REQUEST_BUILD = "75900200"`；用户可在 `Controls/Settings/DevSettingsControl.xaml.cs` 改，只影响 appkey 签名那批 vc 接口的 `build=` 值。

### 3. gRPC 覆盖到哪

`src/BiliLite.UWP/Services/GrpcService.cs`：

| 方法 | 行 | gRPC 路径 |
|---|---:|---|
| `GetDynAll` | 59 | `bilibili.app.dynamic.v2.Dynamic/DynAll` |
| `GetDynSpace` | 88 | `bilibili.app.dynamic.v2.Dynamic/DynSpace` |
| `GetDynDetail` | 111 | `bilibili.app.dynamic.v2.Dynamic/DynDetail` |
| `GetDynVideo` | 141 | `bilibili.app.dynamic.v2.Dynamic/DynVideo` |
| `GetDynRepostList` | 168 | `bilibili.app.dynamic.v2.Dynamic/RepostList` |

**全部是读**：列表、详情、视频流、转发列表。整个仓库 887 个 .cs 文件里没有任何"通过 gRPC 发布/转发动态"的写入路径。转发写入只有上面的 vc REST 一条。

### 4. 鉴权

- 写入走 REST：`access_key`（OAuth token）+ appkey 签名。
- **不用 cookie、不用 csrf**——这是它和我们最大的差别。

---

## 二、三方对照

| 维度 | BiliUWP-Lite | Bili.Copilot 当前实现 | 网页端（BewlyCat / bilibili-margin 源码实测） |
|---|---|---|---|
| 端点 | `api.vc.bilibili.com/dynamic_repost/v1/dynamic_repost/repost` | `api.bilibili.com/x/dynamic/feed/create/dyn` | 同左 |
| 方法 | POST | POST | POST |
| Content-Type | `x-www-form-urlencoded` | `application/json` | `application/json` |
| 请求体 | `uid, dynamic_id, content, at_uids, at_control, extension` | `{csrf, dyn_req:{content,scene:4,option,attach_card:null,meta}, web_repost_src:{dyn_id_str}}` | 同左 |
| 原动态 ID 字段 | `dynamic_id`（form） | `web_repost_src.dyn_id_str` | 同左 |
| 鉴权 | `access_key` + appkey 签名 | Cookie `SESSDATA` + `bili_jct` | 同左 |
| CSRF | 无 | query 里参与 wbi 签名 + body 里一份 | query `csrf` |
| 客户端版本 | `build=75900200`, `mobi_app=android`, `platform=android` | `meta.app_meta.from=create.dynamic.web, mobi_app=web` | 同左 |
| 错误码 | 未做专门处理 | 412 直接停、其它码透传 message | 412 停止重试 |

网页端参考实现（两条独立来源，结构一致）：

- `keleus/BewlyCat` → `src/background/messageListeners/api/moment.ts:257-265`：
  `submitRepost`：`body = { dyn_req: {...}, web_repost_src: { dyn_id_str: '' } }`，`params = { csrf, platform: 'web' }`。
- `jiuyang5354/bilibili-margin` → `src/content/api/dyn.ts:175-180`：
  ```js
  dyn_req: { content: { contents: text ? [{ raw_text: text, type: 1, biz_id: '' }] : [] },
             scene: 4, attach_card: null,
             meta: { app_meta: { from: 'create.dynamic.web', mobi_app: 'web' } } },
  web_repost_src: { dyn_id_str: id }
  ```

---

## 三、`4100001` 的根因

之前那版实现把转发当"发一条带链接的文字"：`scene=1`、没有 `web_repost_src`，还试过把 `dyn_type/dyn_id_str` 塞进表单。

对照上面的实现，转发在 opus 接口里有三个硬性差异：

1. `dyn_req.scene` 必须是 **4**（普通动态是 1）；
2. 原动态 id 放在根节点 **`web_repost_src.dyn_id_str`**，不是 `dyn_type/dyn_id_str` 这些 vc 时代字段；
3. `dyn_req.attach_card` 要显式给 `null`（转发没有附加卡）。

`4100001 参数错误` 正是"缺必填结构"的表现：body 里没有任何字段指向被转发的动态，服务端无法确定转发目标。

另外两个错误码也能对上：

- `HTTP 412`：vc 接口不带 appkey 签名直接打，被风控页挡掉（BiliUWP-Lite 之所以能过，就是因为它签了名并带 access_key）。
- `-101 账号未登录`：那次是我们漏了 Cookie 头，不是接口问题。

---

## 四、可移植方案与最小改动

**不做的事**（按任务要求）：不复用 BiliUWP-Lite 的 AppKey/AppSecret，不伪造 Android 客户端身份，不靠 `build` 版本号绕过风控。

**做的事**：把转发改成真正的网页端结构（`scene=4` + `web_repost_src`），与文本/视频动态共用同一个发送函数。

改动只在一个文件：`src/Desktop/BiliCopilot.UI/Services/DynamicShareService.cs`

- 拆出 `BuildPayload(csrf, content, scene, allowInteract, repostSourceId)`，便于离线核对结构；
- `RepostAsync` 走 `scene = 4` + `web_repost_src.dyn_id_str = 原动态 id`；
- 转发且没写附言时，`contents` 发空数组（与网页端一致）；
- 412 直接返回"被风控拦下，别连续重试"，不再重试。

生成的报文（离线预览，csrf 已替换）：

```json
{"csrf":"CSRF","dyn_req":{"content":{"contents":[{"raw_text":"[甜蜜时光表情包_全部]","type":1,"biz_id":""}]},
"scene":4,"option":{"close_comment":0,"close_repost":0},"attach_card":null,
"meta":{"app_meta":{"from":"create.dynamic.web","mobi_app":"web"}}},
"web_repost_src":{"dyn_id_str":"1269515495776714760"}}
```

空附言时 `contents` 为 `[]`。

---

## 五、验证状态

- 构建：`1.0.12.8` 构建通过并已注册。
- 结构：与两套网页端实现逐字段对齐（上表）。
- **真实写入：尚未执行**。按任务要求"每次写入前确认"，等你点头再跑。

## 六、仍无法确认的

- `dsh-shlr-bilitoolkit` 在本机不存在（`C:\Users\黎翱白Leobai\.dsh\plugins` 只有 `dsh-agent-procedures / dsh-model-fusion / dsh-taskbar-progress / dsh-undo-savepoint`），**无法检查它是否误用了表单编码**。仓库内的转发实现曾经就是表单，已在本轮改掉。
- BiliKernel（NuGet 包 `Richasy.BiliKernel`）是否内置转发实现：本地只有二进制，没有源码；未反编译确认。
- 转发成功后的服务端回执字段（`data.dyn_id_str` 之外还有没有 `origin` 信息）未验证。
- VSR/画质解锁与此无关，未触碰。
