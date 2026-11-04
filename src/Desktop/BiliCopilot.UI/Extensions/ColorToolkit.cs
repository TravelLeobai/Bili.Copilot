// Copyright (c) Bili Copilot. All rights reserved.

using Windows.UI;

namespace BiliCopilot.UI.Extensions;

/// <summary>
/// 颜色微调：按 HSL 改亮度和饱和度，用来做「同色系但更亮更淡」的派生色.
/// </summary>
internal static class ColorToolkit
{
    /// <summary>
    /// 调整亮度和饱和度.
    /// </summary>
    /// <param name="color">原色.</param>
    /// <param name="lightnessDelta">亮度偏移，0~1 之间，正数变亮.</param>
    /// <param name="saturationScale">饱和度系数，小于 1 变淡.</param>
    /// <returns>调整后的颜色，Alpha 保持不变.</returns>
    public static Color WithLightnessAndSaturation(Color color, double lightnessDelta, double saturationScale)
    {
        var (hue, saturation, lightness) = ToHsl(color);
        saturation = Math.Clamp(saturation * saturationScale, 0d, 1d);
        lightness = Math.Clamp(lightness + lightnessDelta, 0d, 1d);
        var (red, green, blue) = FromHsl(hue, saturation, lightness);
        return Color.FromArgb(color.A, red, green, blue);
    }

    private static (double Hue, double Saturation, double Lightness) ToHsl(Color color)
    {
        var red = color.R / 255d;
        var green = color.G / 255d;
        var blue = color.B / 255d;
        var max = Math.Max(red, Math.Max(green, blue));
        var min = Math.Min(red, Math.Min(green, blue));
        var lightness = (max + min) / 2d;
        var delta = max - min;

        if (delta < 1e-6)
        {
            return (0d, 0d, lightness);
        }

        var saturation = lightness > 0.5 ? delta / (2d - max - min) : delta / (max + min);

        double hue;
        if (max == red)
        {
            hue = ((green - blue) / delta) + (green < blue ? 6d : 0d);
        }
        else if (max == green)
        {
            hue = ((blue - red) / delta) + 2d;
        }
        else
        {
            hue = ((red - green) / delta) + 4d;
        }

        return (hue / 6d, saturation, lightness);
    }

    private static (byte Red, byte Green, byte Blue) FromHsl(double hue, double saturation, double lightness)
    {
        if (saturation <= 0d)
        {
            var gray = ToByte(lightness * 255d);
            return (gray, gray, gray);
        }

        var q = lightness < 0.5
            ? lightness * (1d + saturation)
            : lightness + saturation - (lightness * saturation);
        var p = (2d * lightness) - q;

        return (
            ToByte(HueToRgb(p, q, hue + (1d / 3d)) * 255d),
            ToByte(HueToRgb(p, q, hue) * 255d),
            ToByte(HueToRgb(p, q, hue - (1d / 3d)) * 255d));
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0d)
        {
            t += 1d;
        }

        if (t > 1d)
        {
            t -= 1d;
        }

        if (t < (1d / 6d))
        {
            return p + ((q - p) * 6d * t);
        }

        if (t < 0.5d)
        {
            return q;
        }

        if (t < (2d / 3d))
        {
            return p + ((q - p) * ((2d / 3d) - t) * 6d);
        }

        return p;
    }

    private static byte ToByte(double value)
        => (byte)Math.Clamp(Math.Round(value), 0d, 255d);
}
