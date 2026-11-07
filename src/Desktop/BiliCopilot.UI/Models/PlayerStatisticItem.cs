// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.UI.Models;

/// <summary>
/// 统计信息浮层里的一行：左边标签，右边数值.
/// </summary>
/// <param name="Label">字段名.</param>
/// <param name="Value">字段值.</param>
public sealed record PlayerStatisticItem(string Label, string Value);
