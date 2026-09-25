using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using WinDirectoryStats.Helpers;

namespace WinDirectoryStats.Models;

public sealed class DiskItem : INotifyPropertyChanged
{
    private double _viewPercentage;

    public DiskItem(string name, string fullPath, bool isDirectory, DiskItem? parent)
    {
        Name = name;
        FullPath = fullPath;
        IsDirectory = isDirectory;
        Parent = parent;
    }

    public string Name { get; }
    public string FullPath { get; }
    public bool IsDirectory { get; }
    public DiskItem? Parent { get; }
    public List<DiskItem> Children { get; } = new();
    public long Size { get; internal set; }
    public int FileCount { get; internal set; }
    public int FolderCount { get; internal set; }
    public DateTimeOffset LastModified { get; internal set; }

    public string Extension => IsDirectory ? string.Empty : NormalizeExtension(Path.GetExtension(Name));
    public string SizeText => SizeFormatter.Format(Size);
    public string PercentageText => SizeFormatter.FormatPercentage(ViewPercentage);
    public string ItemCountText => IsDirectory
        ? $"{FileCount:N0} files · {FolderCount:N0} folders"
        : Extension;
    public string ModifiedText => LastModified == default ? string.Empty : LastModified.LocalDateTime.ToString("g");
    public string KindText => IsDirectory ? "Folder" : string.IsNullOrEmpty(Extension) ? "File" : Extension + " file";

    public double ViewPercentage
    {
        get => _viewPercentage;
        set
        {
            if (Math.Abs(_viewPercentage - value) < 0.001)
            {
                return;
            }

            _viewPercentage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PercentageText));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsDescendantOf(DiskItem directory)
    {
        DiskItem? current = Parent;
        while (current is not null)
        {
            if (ReferenceEquals(current, directory))
            {
                return true;
            }

            current = current.Parent;
        }

        return false;
    }

    public static string NormalizeExtension(string? extension)
    {
        return string.IsNullOrWhiteSpace(extension) ? "(no extension)" : extension.ToLowerInvariant();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
