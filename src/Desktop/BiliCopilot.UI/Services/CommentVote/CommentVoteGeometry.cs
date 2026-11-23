// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.UI.Services;

/// <summary>
/// 饼图的几何计算（纯数学，不碰 UI）。
/// <para>
/// 放在服务层是为了能被测试工程直接编进去：扇形角度、点击落在哪一片都只是算术，
/// 出错了在界面上很难看出来，值得单独测.
/// </para>
/// </summary>
internal static class CommentVoteGeometry
{
    /// <summary>
    /// 每一片至少占的角度。0 票的选项如果按比例就是零宽，点不到，所以给它一条缝.
    /// </summary>
    public const double MinimumSweep = 6d;

    /// <summary>
    /// 按票数算每一片的角度.
    /// <para>
    /// 都没票（还没人投）时平均分；有票按比例分，但每一项先留 <see cref="MinimumSweep"/> 度的底，
    /// 剩下的角度再按票数分——票多的片仍然明显更大，0 票的片也还能点.
    /// </para>
    /// </summary>
    /// <param name="counts">每项的票数（顺序与选项一致）.</param>
    /// <returns>每项的角度（度），总和 360.</returns>
    public static IReadOnlyList<double> ComputeSweeps(IReadOnlyList<long> counts)
    {
        if (counts is null || counts.Count == 0)
        {
            return [];
        }

        if (counts.Count == 1)
        {
            return [360d];
        }

        var total = 0L;
        foreach (var count in counts)
        {
            total += Math.Max(0, count);
        }

        var sweeps = new double[counts.Count];
        if (total <= 0)
        {
            var even = 360d / counts.Count;
            for (var i = 0; i < sweeps.Length; i++)
            {
                sweeps[i] = even;
            }

            // 除不尽的余数塞给最后一片，保证正好 360.
            sweeps[^1] = 360d - (even * (counts.Count - 1));
            return sweeps;
        }

        // 底缝最多占掉每片的四分之一，免得项数一多就把比例压没了.
        var minimum = Math.Min(MinimumSweep, 360d / counts.Count / 4);
        var flexible = 360d - (minimum * counts.Count);
        for (var i = 0; i < sweeps.Length; i++)
        {
            sweeps[i] = minimum + (Math.Max(0, counts[i]) / (double)total * flexible);
        }

        return sweeps;
    }

    /// <summary>
    /// 点击点落在哪一片上.
    /// </summary>
    /// <param name="sweeps">每项的角度.</param>
    /// <param name="angleFromTop">从 12 点方向顺时针算的角度（度），可以是任意实数.</param>
    /// <returns>下标；没有可选片时 -1.</returns>
    public static int HitTest(IReadOnlyList<double> sweeps, double angleFromTop)
    {
        if (sweeps is null || sweeps.Count == 0)
        {
            return -1;
        }

        var angle = Normalize(angleFromTop);
        var cursor = 0d;
        for (var i = 0; i < sweeps.Count; i++)
        {
            cursor += sweeps[i];
            if (angle < cursor)
            {
                return i;
            }
        }

        // 累加误差导致落到最后一片之外时，按最后一片算.
        return sweeps.Count - 1;
    }

    /// <summary>
    /// 把角度折到 [0, 360) .
    /// </summary>
    /// <param name="angle">任意角度.</param>
    /// <returns>折好的角度.</returns>
    public static double Normalize(double angle)
    {
        var value = angle % 360d;
        return value < 0 ? value + 360d : value;
    }
}
