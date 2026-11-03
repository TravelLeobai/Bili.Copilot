# 哔哩助理 · 自用重写版

> 个人自用的客户端源码存档，不分发安装包，也不面向普通用户发布。

## 先说清楚

- **原项目**：[Bili.Copilot](https://github.com/azpanel/Bili.Copilot)，作者**云之幻**。这个仓库是在原项目基础上给自己用的改造分支，界面骨架、播放内核、登录与数据接口绝大部分仍是原作者的心血。原作者没参与这个分支，这个分支的问题也不要去找原作者。
- **B 站版权**：所有视频、音频、封面、弹幕、评论等内容版权归哔哩哔哩及对应 UP 主、版权方所有。本项目只做个人观看时的客户端界面，不提供任何内容的下载分发、不做破解付费、不绕过会员权限。
- 仅供个人学习与自用。任何人使用本仓库代码产生的行为与后果自负，与原项目作者及哔哩哔哩官方均无关。
- 原来的 README 已经删掉，换成这份说明。删它是因为这份代码已经很不一样了，挂原文会让人误以为是官方发布。

## 这个分支做了什么

用 **Reactor**（Microsoft.UI.Reactor 声明式 UI）重写界面层，并在 **DeepSeek Harness** 里配合 AI 一起迭代。改动集中在自用场景最在意的几块：

- **动态页**：综合动态改成响应式瀑布流，可用宽度 ≥900 DIP 两列、≥1250 DIP 三列，卡片间距统一 12 DIP；顶部关注 UP 头像条可筛选。
- **评论面板**：整块用 Reactor 重写，主评论与「N 条回复」的子评论详情共用同一套卡片；排序切换、点赞、回复、加载更多都保留。
- **顶栏**：搜索框真正居中，收藏 / 稍后再看 / 历史移到搜索框左侧，右侧只留窗口按钮。
- **侧边栏**：头像放进 PaneFooter，和导航项同一套图标位置、字号与高度；展开后右侧显示昵称。
- **覆盖层**：评论/设置这类浮层改成按需挂载，不再出现「点关闭后再切页闪现一下」的问题。

## 技术栈

- .NET 10 / C# / WinUI 3（Windows App SDK 2.2）
- Microsoft.UI.Reactor 0.1.0-preview
- BiliKernel（原项目的数据与接口层）
- 构建脚本与自检脚本在 `tools/`，Windows 上用 x64 Debug 跑通。

## 构建（Windows）

```powershell
git clone https://github.com/<your-account>/Bili.Copilot.git
cd Bili.Copilot/src
dotnet build Desktop\BiliCopilot.UI\BiliCopilot.UI.csproj -c Debug -p:Platform=x64
```

需要 Windows 11、.NET 10 SDK 与 Windows App SDK 运行时。首次运行会走原项目的登录流程，登录态与缓存保存在本机用户目录。

## 许可

沿用原项目的 LICENSE。代码可读可学，但要拿去发布、改名上架或商用之前，请先满足原项目的许可条件并取得 B 站相关的合法授权——这不是本仓库能替你解决的事。

---

给云之幻：谢谢你写的 Bili.Copilot，没有它这个分支无从谈起。
