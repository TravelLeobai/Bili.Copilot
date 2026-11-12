// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Core;
using Microsoft.UI.Xaml;
using Richasy.WinUIKernel.Share;

namespace BiliCopilot.UI.Extensions;

/// <summary>
/// 从当前激活窗口拿 XamlRoot.
/// 窗口还没 Content、或激活窗口本身就是 null 时返回 <c>null</c>，
/// 不要在这里抛异常——调用方（弹窗、扫码、打开配置文件）都会因此整条失败.
/// </summary>
public sealed class XamlRootProvider : IXamlRootProvider
{
    /// <inheritdoc/>
    public XamlRoot? XamlRoot
    {
        get
        {
            var window = GlobalDependencies.Kernel.GetRequiredService<AppViewModel>().ActivatedWindow;
            return (window?.Content as FrameworkElement)?.XamlRoot;
        }
    }
}
