// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.UI.Controls.Glass;

/// <summary>
/// 圆角矩形内的位移场：把每条边按 <see cref="DisplacementDistanceFunction"/> 弯一次.
/// </summary>
internal sealed class RoundedCornerRectangleDisplacementFunction
{
    private readonly DisplacementDistanceFunction _distanceFunction;
    private readonly double _width;
    private readonly double _height;
    private readonly double _radius;

    /// <summary>
    /// Initializes a new instance of the <see cref="RoundedCornerRectangleDisplacementFunction"/> class.
    /// </summary>
    /// <param name="distanceFunction">距离映射函数.</param>
    /// <param name="width">宽度.</param>
    /// <param name="height">高度.</param>
    /// <param name="cornerRadius">圆角半径（四角相同）.</param>
    public RoundedCornerRectangleDisplacementFunction(
        DisplacementDistanceFunction distanceFunction,
        double width,
        double height,
        double cornerRadius)
    {
        _distanceFunction = distanceFunction;
        _width = width;
        _height = height;
        _radius = cornerRadius;
    }

    /// <summary>
    /// 计算某个像素的位移.
    /// </summary>
    /// <param name="x">横坐标.</param>
    /// <param name="y">纵坐标.</param>
    /// <returns>位移向量.</returns>
    public (double X, double Y) Calculate(double x, double y)
    {
        var halfWidth = _width / 2;
        var halfHeight = _height / 2;
        double displaceX;
        double displaceY;

        if (x < halfWidth)
        {
            if (y < halfHeight)
            {
                (displaceX, displaceY) = DisplaceAtQuarter(x, y);
            }
            else
            {
                (displaceX, displaceY) = DisplaceAtQuarter(x, _height - y);
                displaceY = -displaceY;
            }
        }
        else
        {
            if (y < halfHeight)
            {
                (displaceX, displaceY) = DisplaceAtQuarter(_width - x, y);
                displaceX = -displaceX;
            }
            else
            {
                (displaceX, displaceY) = DisplaceAtQuarter(_width - x, _height - y);
                displaceX = -displaceX;
                displaceY = -displaceY;
            }
        }

        return (displaceX, displaceY);
    }

    private (double X, double Y) DisplaceAtQuarter(double x, double y)
    {
        var r = _radius;
        if (x <= r && y <= r)
        {
            var d = r - Math.Sqrt(((x - r) * (x - r)) + ((y - r) * (y - r)));
            if (d < 0)
            {
                return (0, 0);
            }

            var k = (_distanceFunction.Calculate(d) - d) / (d - r);
            return (k * (x - r), k * (y - r));
        }

        if (x < y)
        {
            return (_distanceFunction.Calculate(x) - x, 0);
        }

        return (0, _distanceFunction.Calculate(y) - y);
    }
}
