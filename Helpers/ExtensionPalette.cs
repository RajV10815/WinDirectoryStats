using System;
using Windows.UI;

namespace WinDirectoryStats.Helpers;

public static class ExtensionPalette
{
    public static Color GetColor(string value)
    {
        uint hash = 2166136261;
        foreach (char character in value.ToUpperInvariant())
        {
            hash ^= character;
            hash *= 16777619;
        }

        double hue = hash % 360;
        double saturation = 0.62 + ((hash >> 8) % 18) / 100.0;
        double lightness = 0.48 + ((hash >> 16) % 12) / 100.0;
        return FromHsl(hue, saturation, lightness);
    }

    private static Color FromHsl(double hue, double saturation, double lightness)
    {
        double chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        double section = hue / 60;
        double secondary = chroma * (1 - Math.Abs(section % 2 - 1));
        (double red, double green, double blue) = section switch
        {
            < 1 => (chroma, secondary, 0d),
            < 2 => (secondary, chroma, 0d),
            < 3 => (0d, chroma, secondary),
            < 4 => (0d, secondary, chroma),
            < 5 => (secondary, 0d, chroma),
            _ => (chroma, 0d, secondary)
        };
        double match = lightness - chroma / 2;
        return Color.FromArgb(
            255,
            (byte)Math.Round((red + match) * 255),
            (byte)Math.Round((green + match) * 255),
            (byte)Math.Round((blue + match) * 255));
    }
}
