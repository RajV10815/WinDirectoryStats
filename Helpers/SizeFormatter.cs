using System;
using System.Globalization;

namespace WinDirectoryStats.Helpers;

public static class SizeFormatter
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

    public static string Format(long bytes)
    {
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        string format = unit == 0 ? "N0" : value >= 100 ? "N0" : value >= 10 ? "N1" : "N2";
        return value.ToString(format, CultureInfo.CurrentCulture) + " " + Units[unit];
    }

    public static string FormatPercentage(double value)
    {
        return Math.Clamp(value, 0, 100).ToString("N1", CultureInfo.CurrentCulture) + "%";
    }
}
