// Copyright (c) Bili Copilot. All rights reserved.

using System.Text;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Text;
using Richasy.WinUIKernel.Share.Base;
using System.Windows.Input;

namespace BiliCopilot.UI.Controls.Comment;

/// <summary>
/// 评论输入框。
/// <para>
/// 用 RichEditBox 而不是 TextBox：只有它能往正文里塞图片，
/// 表情因此显示成图（正文里仍记着 [名字] 占位文本），发出去还是 B 站认的那个占位符。
/// </para>
/// </summary>
public sealed partial class CommentTextBox : LayoutUserControlBase
{
    /// <summary>
    /// <see cref="Text"/> 的依赖属性.
    /// </summary>
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(CommentTextBox), new PropertyMetadata(default, OnTextChanged));

    /// <summary>
    /// <see cref="SendCommand"/> 的依赖属性.
    /// </summary>
    public static readonly DependencyProperty SendCommandProperty =
        DependencyProperty.Register(nameof(SendCommand), typeof(IAsyncRelayCommand), typeof(CommentTextBox), new PropertyMetadata(default));

    /// <summary>
    /// <see cref="ReplyTip"/> 的依赖属性.
    /// </summary>
    public static readonly DependencyProperty ReplyTipProperty =
        DependencyProperty.Register(nameof(ReplyTip), typeof(string), typeof(CommentTextBox), new PropertyMetadata(default));

    /// <summary>
    /// <see cref="ResetSelectedCommand"/> 的依赖属性.
    /// </summary>
    public static readonly DependencyProperty ResetSelectedCommandProperty =
        DependencyProperty.Register(nameof(ResetSelectedCommand), typeof(ICommand), typeof(CommentTextBox), new PropertyMetadata(default));

    /// <summary>
    /// <see cref="IsEmoteFlyoutOpened"/> 的依赖属性.
    /// </summary>
    public static readonly DependencyProperty IsEmoteFlyoutOpenedProperty =
        DependencyProperty.Register(nameof(IsEmoteFlyoutOpened), typeof(bool), typeof(CommentTextBox), new PropertyMetadata(default));

    /// <summary>
    /// 插进去的表情 token，按插入顺序记；读正文时把图片占位符换回它们.
    /// </summary>
    private readonly List<string> _tokens = [];

    private bool _syncing;

    /// <summary>
    /// Initializes a new instance of the <see cref="CommentTextBox"/> class.
    /// </summary>
    public CommentTextBox()
    {
        InitializeComponent();
        ReplyEditor.TextChanged += (_, _) => SyncTextFromEditor();
        ReplyEditor.KeyDown += OnEditorKeyDown;
    }

    /// <summary>
    /// 回复文本.
    /// </summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>
    /// 发送评论命令.
    /// </summary>
    public IAsyncRelayCommand SendCommand
    {
        get => (IAsyncRelayCommand)GetValue(SendCommandProperty);
        set => SetValue(SendCommandProperty, value);
    }

    /// <summary>
    /// 评论提示.
    /// </summary>
    public string ReplyTip
    {
        get => (string)GetValue(ReplyTipProperty);
        set => SetValue(ReplyTipProperty, value);
    }

    /// <summary>
    /// 重置已选择评论的命令.
    /// </summary>
    public ICommand ResetSelectedCommand
    {
        get => (ICommand)GetValue(ResetSelectedCommandProperty);
        set => SetValue(ResetSelectedCommandProperty, value);
    }

    /// <summary>
    /// 表情面板是否打开.
    /// </summary>
    public bool IsEmoteFlyoutOpened
    {
        get => (bool)GetValue(IsEmoteFlyoutOpenedProperty);
        set => SetValue(IsEmoteFlyoutOpenedProperty, value);
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (CommentTextBox)d;
        if (control._syncing)
        {
            return;
        }

        // 外部直接赋值（例如点回复时写入 "@某人 "）：整篇重写，之前的表情作废.
        control.SetEditorPlainText(e.NewValue as string ?? string.Empty);
    }

    private void SyncTextFromEditor()
    {
        if (_syncing)
        {
            return;
        }

        _syncing = true;
        try
        {
            Text = ReadPlain();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnEditorKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
        {
            return;
        }

        // Shift+Enter 换行，单独 Enter 直接发.
        var shift = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
        if (shift.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            return;
        }

        e.Handled = true;
        _ = SubmitAsync();
    }

    private async void OnSendClicked(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => await SubmitAsync();

    private async Task SubmitAsync()
    {
        var text = ReadPlain().Trim();
        if (string.IsNullOrEmpty(text) || SendCommand is null)
        {
            return;
        }

        var finalText = string.IsNullOrEmpty(ReplyTip) ? text : $"回复 @{ReplyTip} : {text}";
        await SendCommand.ExecuteAsync(finalText);
        SetEditorPlainText(string.Empty);
    }

    private void OnItemClick(object sender, string e)
        => InsertEmote(e);

    private void OnFlyoutClosed(object sender, object e)
    {
        IsEmoteFlyoutOpened = false;
        ReplyEditor.Focus(FocusState.Programmatic);
    }

    private void OnFlyoutOpened(object sender, object e)
        => IsEmoteFlyoutOpened = true;

    /// <summary>
    /// 读完正文：图片占位符按插入顺序换回 [名字].
    /// </summary>
    /// <returns>可发送的正文.</returns>
    private string ReadPlain()
    {
        ReplyEditor.Document.GetText(TextGetOptions.NoHidden, out var raw);
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(raw.Length + (_tokens.Count * 8));
        var index = 0;
        foreach (var ch in raw)
        {
            if (ch == '\uFFFC')
            {
                if (index < _tokens.Count)
                {
                    builder.Append(_tokens[index]);
                }

                index++;
            }
            else if (ch == '\r')
            {
                builder.Append('\n');
            }
            else
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// 整篇写成纯文本（表情列表清空）.
    /// </summary>
    /// <param name="text">文本.</param>
    private void SetEditorPlainText(string text)
    {
        _syncing = true;
        try
        {
            _tokens.Clear();
            ReplyEditor.Document.SetText(TextSetOptions.None, text ?? string.Empty);
            ReplyEditor.Document.Selection.SetRange((text ?? string.Empty).Length, (text ?? string.Empty).Length);
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>
    /// 在光标处插一个表情：有图就插图（正文里是占位符），没图就插 [名字] 文本.
    /// </summary>
    /// <param name="token">表情占位文本.</param>
    private async void InsertEmote(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        var emote = Services.BiliEmoteService.TryGet(token);
        byte[]? bytes = null;
        if (!string.IsNullOrWhiteSpace(emote?.Url)
            && Uri.TryCreate(emote!.Url, UriKind.Absolute, out var uri))
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
                bytes = await client.GetByteArrayAsync(uri);
            }
            catch
            {
                bytes = null;
            }
        }

        if (bytes is null || bytes.Length == 0)
        {
            InsertTextAtCaret(token);
            _tokens.Add(token);
            SyncTextFromEditor();
            return;
        }

        InsertPictureAtCaret(bytes, emote?.Size == 2 ? 36 : 22);
        _tokens.Add(token);
        SyncTextFromEditor();
    }

    private void InsertTextAtCaret(string text)
    {
        ReplyEditor.Document.GetText(TextGetOptions.NoHidden, out var raw);
        raw = raw.TrimEnd('\r');
        var caret = Math.Clamp(ReplyEditor.Document.Selection.StartPosition, 0, raw.Length);
        var merged = raw.Insert(caret, text);
        ReplyEditor.Document.SetText(TextSetOptions.None, merged);
        ReplyEditor.Document.Selection.SetRange(caret + text.Length, caret + text.Length);
    }

    /// <summary>
    /// 把图片插到光标处：RichEdit 的 ITextRange 没有 InsertImage，只能整篇按 RTF 重建.
    /// 中文必须转成 \uNNNN，否则 RichEdit 会丢字（这正是一月份那版表情包"发出去是空的"的原因）.
    /// </summary>
    /// <param name="imageBytes">图片字节.</param>
    /// <param name="side">边长（像素）.</param>
    private void InsertPictureAtCaret(byte[] imageBytes, int side)
    {
        ReplyEditor.Document.GetText(TextGetOptions.NoHidden, out var raw);
        raw = raw.TrimEnd('\r');

        var caret = Math.Clamp(ReplyEditor.Document.Selection.StartPosition, 0, raw.Length);
        var before = EscapeRtf(raw[..caret]);
        var after = EscapeRtf(raw[caret..]);
        var target = side * 15;
        var hex = Convert.ToHexString(imageBytes);
        var pict = $@"{{\pict\pngblip\picw{side}\pich{side}\picwgoal{target}\pichgoal{target} {hex}}}";
        var rtf = $@"{{\rtf1\ansi\ansicpg936\deff0{{\fonttbl{{\f0 Microsoft YaHei;}}}}\f0\fs28 {before}{pict}{after}}}";

        ReplyEditor.Document.SetText(TextSetOptions.FormatRtf, rtf);
        ReplyEditor.Document.Selection.SetRange(caret + 1, caret + 1);
    }

    /// <summary>
    /// RTF 转义：控制字符按规则转义，非 ASCII 字符写成 \uNNNN? .
    /// </summary>
    /// <param name="value">原文.</param>
    /// <returns>可直接拼进 RTF 的片段.</returns>
    private static string EscapeRtf(string value)
    {
        var builder = new StringBuilder(value.Length * 2);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '{':
                    builder.Append("\\{");
                    break;
                case '}':
                    builder.Append("\\}");
                    break;
                case '\r':
                case '\n':
                    builder.Append("\\par ");
                    break;
                default:
                    if (ch > 0x7F)
                    {
                        builder.Append("\\u").Append((int)ch).Append('?');
                    }
                    else
                    {
                        builder.Append(ch);
                    }

                    break;
            }
        }

        return builder.ToString();
    }
}
