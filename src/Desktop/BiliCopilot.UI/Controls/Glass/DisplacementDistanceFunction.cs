// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.UI.Controls.Glass;

/// <summary>
/// 距离边缘 d 的点应该显示位于距离多远的点.
/// </summary>
internal sealed class DisplacementDistanceFunction
{
    private readonly double _thickness;
    private readonly double _maxDistance;
    private readonly double _a;

    /// <summary>
    /// Initializes a new instance of the <see cref="DisplacementDistanceFunction"/> class.
    /// </summary>
    /// <param name="thickness">玻璃边缘厚度.</param>
    /// <param name="maxDistance">最大位移距离.</param>
    public DisplacementDistanceFunction(double thickness, double maxDistance)
    {
        _thickness = thickness;
        _maxDistance = maxDistance;
        _a = (_maxDistance - _thickness) / Math.Pow(_thickness, 4);
    }

    /// <summary>
    /// 计算某个距离处应该采样的距离.
    /// </summary>
    /// <param name="distance">距离边缘的距离.</param>
    /// <returns>采样距离.</returns>
    public double Calculate(double distance)
        => distance > _thickness
            ? distance
            : (_a * Math.Pow(distance - _thickness, 4)) + _thickness;
}
