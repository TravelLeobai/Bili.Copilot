# 图片加载 / 预解码 / 视口虚拟化 专项审计

审计基准：
- 本仓库 `6a4a43f`（改动前）
- BiliFluent `50dff70cf2b1c332b60666ccf0e7e9868b28cde3`（论文级对照）

本文只记录**有证据的现状**，以及每条改动落在哪个文件。没有测到的指标明确写「未测」。

---

## 一、BiliFluent 侧（源码证据）

| 机制 | 证据 | 事实 |
|---|---|---|
| 图片基类来自 NuGet 包 | `Richasy.WinUIKernel.Share 0.0.12-preview3` xml 文档 669-679 | `ImageExBase` 提供 `ImageLoaded/ImageFailed`、`CancelAllLoading`、`RestoreCancelledImages`、`GetCacheSubFolder`、`GetAllCacheSizeAsync` |
| 占位骨架 | `Base\ImageExBase\ImageExBase.xaml:26-31` | Shimmer + `IsImageLoading` |
| 后台解码关闭 | `App.xaml.cs:169` | `ImageExBase.EnableBackgroundDecoding = false;` 注释写明「true 会为每张图新建 CanvasDevice 并做 GPU→CPU→GPU 拷贝」 |
| 解码宽度自适应（仅图库页） | `GalleryPage.xaml.cs:66-77` | `viewportPixels = ActualWidth × RasterizationScale`，`DecodePixelWidth = min(2560, ceil(viewportPixels × 2))` |
| 解码宽度改写 | `GalleryPage.xaml.cs:79-95` | `SizeChanged` / `XamlRoot.Changed` 时改写已加载 `BitmapImage.DecodePixelWidth` |
| 列表回收长度 | `Base\Repeater\AppRepeater.xaml:22` | 固定 `VerticalCacheLength="20"` |

**未发现实现**（仓库源码 0 命中）：`ImageOpened`/`ImageFailed` 处理、进程内解码位图缓存、图片预取/预解码、图片请求并发上限、`MaxConnectionsPerServer`、`DecodePixelHeight` 显式设置、自定义 `VirtualizingLayout`、`ImageBrush`。

---

## 二、本项目四个界面的真实现状

### 0) 共享图片控件

`Controls/BasicCoverImage.cs` → 继承包内 `ImageExBase`，**Win2D 路径不是 BitmapImage**：

- `:52 DrawImage(CanvasBitmap)` → `:54-66` 按宽高比回写 `DecodeHeight`，比例不符就**新建** `CanvasImageSource`（`dpi:96`）
- `:94-122 NormalizeSource()` → 按 `DecodeWidth` 拼服务端缩略地址（`@480w…webp`）
- 磁盘缓存：`GetCacheSubFolder() => "ImageCache"`；基类实际目录 `ImageExCache`
- 请求：单例 `HttpClient`（`Extensions/InternalHttpExtensions.cs:29`），无并发上限
- **无** `ImageOpened` 校验；`ImageLoaded/ImageFailed` 订阅后不解绑
- 进程内缓存：**没有**（只有 `SmallImageCache`，见下）

### 1) 评论列表（Reactor 路径）

- 列表：`Reactor/CommentPanel.cs:209` `VirtualList(...)`，`estimatedItemHeight: 132`
- 卡片 key：`:219` `$"c-{index}-{rpid}"`，`:222` 索引器同款
- 头像：`:291-297` 原生 `Image` + `SmallImageCache.Get(url, 40)` → `BitmapImage` + `DecodePixelWidth = 显示尺寸×2`（`Extensions/SmallImageCache.cs:39-67`），LRU 容量 192，内存压力时 `Trim()`
- 表情：`:524-531` `new BitmapImage(new Uri(...))` **未设解码尺寸**，显示 22/32px
- 评论附图：**这条路径不渲染**（只读 `Content.Text`）
- 加载：`CommentMainViewModel.cs:70` 无 CancellationToken，失败 `_preventLoadMore = true` 永久停

### 2) 动态-视频

- 列表：`VideoMomentSectionDetailControl.xaml:113` `AppRepeater` + `MomentLayout`（`App.xaml:114` UniformGridLayout，260×280，间距 20）
- 宿主：`IncrementVerticalScrollControl` → ScrollView → **Grid**
- 卡片：Reactor `MomentVideoCard.cs:81-88` 封面 `DecodeWidth = 600`、`:45-52` 头像 `36×36 / 100`
- 事件：`Attach/Detach` 成对（`:144-180`），`ElementPrepared` 也解绑（`:203-211`）
- 分区切换：VM 侧按 `_momentIds` HashSet 去重增量（`:108-114`），刷新才 `Clear()`

### 3) 动态-综合（瀑布流）

- 列表：**手写 `StackPanel` 多列**（`Reactor/MomentWaterfall.cs:60-71`），不是虚拟化列表
- 列数：宽度断点 900/1250 → 1/2/3 列（`:20-23`、`:118-126`），`i % columnCount` 分桶
- 滚动：Reactor `ScrollView`（`:90-96`），触底阈值 600（`:128-134`，**无 `IsLoading` 守卫**）
- 复用：`SyncColumn`（`:136-170`）按 `panel.Children[i]` 索引复用并直接 `card.ViewModel = items[i]`
- 图片：`FullMomentPresenter.xaml:31-37` 头像 100；`MomentImageGallery.xaml:20-29` 九宫格 120×120 显示、`DecodeWidth=300`

### 4) 动态-直播

- 列表：`ItemsRepeater`（`LiveMomentSectionDetailControl.xaml:220`），**ScrollViewer 直连**（`:212-219`）——四段里唯一保持虚拟化链路
- Layout：`UniformGridLayout ItemsStretch=None`（`:222-227`），`MinItemWidth` 由 `UpdateItemWidth()` 按宽度改写（`.xaml.cs:79-110`）
- 加载：两阶段，回放用 `AppendCards`（VM `:200-213`）增量追加
- 身份：卡片 `Tag="{x:Bind}"`（`xaml:31`），`ResolveCard` 三级回退（`.xaml.cs:191-227`）

---

## 三、本轮已实施的改动

### A. 解码宽度按实际渲染尺寸自适应（`Controls/BasicCoverImage.cs`）

XAML 里的 `DecodeWidth` 改为**上限**，真实解码宽度：

```
target = min(渲染宽度 × XamlRoot.RasterizationScale, 上限)
bucket = 吸附到 [32,48,64,96,128,192,256,320,384,480,600]
变化 < 25% 时不动（避免抖动式重新解码）
```

- 收益：600 的封面卡放在 300 宽列里不再解 600；高 DPI 下 100 的头像不会糊
- 档位吸附：同档位的图共用一份磁盘缓存与解码结果，避免「一张图七八种尺寸」

### B. 综合瀑布流卡片复用改成身份驱动（`Reactor/MomentWaterfall.cs`）

```csharp
if (!ReferenceEquals(card.Tag, items[i]))   // 索引相同 ≠ 同一条数据
{
    card.ViewModel = null;                  // 先解绑：取消旧请求、清旧源
    card.Tag = items[i];
    card.ViewModel = items[i];
}
```

- 回收多余卡片时同样先 `ViewModel = null` 再移除
- 收益：插入/重排后不会把上一条内容的图短暂显示在新卡片上；内容没变则不重新请求

---

## 四、未做的部分与原因

| 项 | 现状 | 原因 |
|---|---|---|
| 综合瀑布流真·虚拟化 | 全量 `StackPanel` | 可变高度 masonry 需要自定义 `VirtualizingLayout`；改造成本高，且会改变现有滚动锚点行为。备选：换 `ItemsRepeater + UniformGridLayout`（丢 masonry 效果）或做「只保留视口前后各一屏卡片」的窗口化 |
| 图片请求并发上限 | 无 | 基类在 NuGet 包里，仓库无法加闸门；在控件层再加一层限流会和基类的在途去重打架 |
| 预解码 | 未做 | `EnableBackgroundDecoding` 已显式关闭（包内实现是 GPU→CPU→GPU 拷贝，反而更慢）；Win2D 路径下没有安全的跨线程预解码入口 |
| 评论附图 | Reactor 路径不渲染 | 属于功能缺失，不是性能问题 |
| 内存解码缓存 | 只有 `SmallImageCache`（LRU 192） | 覆盖评论/弹幕头像；`BasicCoverImage` 无进程内缓存，靠磁盘缓存 + 在途去重 |

---

## 五、验证状态

| 项 | 状态 |
|---|---|
| 构建（含分析器） | 通过 |
| 启动 | `STARTUP-OK` |
| 首屏/滚动/内存基线数据 | **未测**（缺少登录态与可重复测试路径） |
| 30 次快速滚动、20 次刷新、20 次分区切换 | **未执行** |
| 图片串位回归 | **未执行**（改动是针对该风险的修复，但未实测验证） |

回归脚本没跑之前，不能声称「已消除串位」。
