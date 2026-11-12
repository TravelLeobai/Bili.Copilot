# 转发所需的 OAuth access_token：获取路径调研

> 目标：在**不改现有 Cookie 登录方式**、或只做最小改动的前提下，拿到账号的 `access_key`，让 vc 的
> `dynamic_repost/repost` 能用。
> 结论先行：**Cookie 换不出 token**，公开接口里没有这条路；可行的最小方案是 TV 扫码，
> 但它强制要求后续 vc 请求用**同一组 appkey** 签名（否则 `-663 鉴权失败`）。

---

## 1. 现有登录方式能不能直接换 token

查了 BiliUWP-Lite 全部登录相关代码（`Models/Requests/Api/AccountApi.cs`）：

| 端点 | 行 | 用途 |
|---|---:|---|
| `/x/passport-login/web/key` | 28 | 取登录公钥 |
| `/api/oauth2/login` | 42 | **账密登录，返回 access_token** |
| `/x/passport-login/oauth2/login` | 58 | 同上（新版） |
| `/x/passport-login/login/sms` | 118 | 短信登录 |
| `/api/login/sso` | 136 | SSO 登录 |
| `/x/passport-login/web/exchange_cookie` | 228 | **Cookie 刷新**（不是换 token） |
| `/api/oauth2/refreshToken` | 465 | 用 access_token + refresh_token 续期 |

`Modules/User/Account.cs` 里 `SaveLogin(access_token, refresh_token, expires_in, mid, …, appKey)` 存的是登录接口直接返回的 token；
`GetLoginAppKeySecret()` 只负责取签名用的 key，不做兑换。

**没有** "拿 SESSDATA 换 access_token" 的端点。所以：

- 想不改变登录方式 → 只能**额外**走一次扫码（web 或 TV）拿 token；
- 或者走账密/短信登录（那才是改登录方式）。

---

## 2. TV 扫码：最小改动方案

接口（公开文档，见文末引用）：

| 步骤 | 端点 | 关键参数 |
|---|---|---|
| 取二维码 | `POST https://passport.bilibili.com/x/passport-tv-login/qrcode/auth_code` | `appkey=4409e2ce8ffd12b8`、`local_id`、`ts`、`sign` |
| 轮询结果 | `POST https://passport.bilibili.com/x/passport-tv-login/qrcode/poll` | `appkey`、`auth_code`、`local_id`、`ts`、`sign` |
| 续期 | `POST https://passport.bilibili.com/x/passport-tv-login/oauth2/refresh_token` | `access_key`、`refresh_token`、`appkey`、`ts`、`sign` |

返回里拿 `access_token`、`refresh_token`、`mid`、`expires_in`。

TV 用的就是 BiliUWP-Lite `Services/ApiHelper.cs:45` 那组 key：

```csharp
public static ApiKeyInfo AndroidTVKey = new ApiKeyInfo("4409e2ce8ffd12b8", "59b43e04ad6965f34319062b478f83dd", …);
```

### 关键约束（必须照做）

> "通过某一组 APPKEY/APPSEC 获取到的 access_token，当接口需要 sign 签名时也只能使用该组 APPKEY/APPSEC，
> 否则出现 `{ code: -663, message: '鉴权失败，请联系账号组' }`"

这意味着：

1. 用 TV key 扫码拿的 token，**vc 转发也必须用 TV key 签名**；
2. 我们的 `BiliVcSigner` 现在写死 Android key（`1d8b6e7d45233436`），得改成"跟着 token 走"——
   存 token 时一并记下它属于哪组 key。

---

## 3. 落地需要什么（工作量清单）

| 项 | 必要性 | 说明 |
|---|---|---|
| `BiliOAuthService` | 必须 | auth_code / poll / refresh 三个调用 + token 落盘（LocalState） |
| `BiliVcSigner` 参数化 | 必须 | appkey/appsecret 从常量改成"当前 token 对应的 key" |
| QR 渲染 | 必须 | TV 登录返回的是一个 URL，不是图片；现有启动页拿的是服务端给的 PNG，没法直接复用 → 需要引入二维码生成（如 QRCoder） |
| 一个扫码入口 | 必须 | 设置页加一行"转发授权（扫码）"，或转发失败时在对话框里提示 |
| token 过期处理 | 建议 | `expires_in` 到了先用 refresh_token 续，续失败再提示重新扫码 |

也就是：**不是几行改动能搞定的**，至少是一个服务 + 一个对话框 + 一个 NuGet 依赖。

---

## 4. 但先别急着做：vc 可能根本不需要 token

现行实现里 vc 转发已经加了 appkey 签名，只是没带 `access_key`。
`access_key` 缺失时服务端无非两种反应：`-101 账号未登录`（要 token）或正常接受 Cookie。

**这个可以零成本验证**：拿一个不存在的 dynamic_id 打一次 repost——不会创建任何动态，
看返回是 `-101/412`（说明必须要 token）还是"动态不存在/参数错误"（说明 Cookie 就够）。

如果 Cookie 就够，上面整张清单都不用做。

---

## 5. 三种方案的取舍

| 方案 | 改动量 | 风险 | 需要用户做什么 |
|---|---|---|---|
| A. 先验证 Cookie 是否够用 | 极小（一次请求） | 无副作用（不写入） | 同意发一次无效请求 |
| B. TV 扫码拿 token | 中（服务 + QR 依赖 + 对话框） | 需要 token 绑 key，签名要一起改 | 用 B 站 App 扫一次码 |
| C. 改成 App 账密/短信登录 | 大（改登录方式） | 违背"不改登录方式" | 重新登录 |

推荐顺序：**A → B**，C 不建议。

---

## 6. 仍未确认

- vc `dynamic_repost/repost` 在只有 Cookie + appkey 签名时到底是否放行（方案 A 就能回答）。
- TV 扫码拿到的 token 能否直接用于 vc 转发（文档层面允许，未实测）。
- refresh_token 的实际有效期（`expires_in` 之外的行为未验证）。
- 本机 `dsh-shlr-bilitoolkit` 不存在，无法核对它是否已有 token 逻辑。

## 引用

- 二维码登录（含 TV 端 auth_code / poll）：<https://gitee.com/Gongzx_0313/bilibili-API-collect/blob/master/login/login_action/QR.md>
- 同文档关于 appkey 与 token 绑定的 `-663` 说明：<https://github.com/zzxqasdcc/bilibili-API-collectforfork/blob/master/docs/login/login_action/QR.md>
- refresh_token 用法示例（第三方客户端）：<https://github.com/bilive/bilive_client/blob/master/bilive/lib/app_client.ts>
