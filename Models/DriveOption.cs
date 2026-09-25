using System.IO;
using WinDirectoryStats.Helpers;

namespace WinDirectoryStats.Models;

public sealed class DriveOption
{
    public DriveOption(string displayName, string path)
    {
        DisplayName = displayName;
        Path = path;
    }

    public DriveOption(DriveInfo drive)
    {
        Path = drive.RootDirectory.FullName;
        string label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Local disk" : drive.VolumeLabel;
        DisplayName = $"{label} ({Path.TrimEnd('\\')}) · {SizeFormatter.Format(drive.AvailableFreeSpace)} free";
    }

    public string Path { get; }
    public string DisplayName { get; }
}
