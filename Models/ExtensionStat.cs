using Microsoft.UI.Xaml.Media;
using WinDirectoryStats.Helpers;

namespace WinDirectoryStats.Models;

public sealed class ExtensionStat
{
    public ExtensionStat(string extension, long size, int fileCount, long totalSize)
    {
        Extension = extension;
        Size = size;
        FileCount = fileCount;
        Percentage = totalSize > 0 ? size * 100.0 / totalSize : 0;
        ColorBrush = new SolidColorBrush(ExtensionPalette.GetColor(extension));
    }

    public string Extension { get; }
    public long Size { get; }
    public int FileCount { get; }
    public double Percentage { get; }
    public SolidColorBrush ColorBrush { get; }
    public string SizeText => SizeFormatter.Format(Size);
    public string PercentageText => SizeFormatter.FormatPercentage(Percentage);
    public string FileCountText => FileCount.ToString("N0");
}
