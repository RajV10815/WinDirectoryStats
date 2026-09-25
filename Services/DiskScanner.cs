using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WinDirectoryStats.Models;

namespace WinDirectoryStats.Services;

public sealed class DiskScanner
{
    public Task<ScanResult> ScanAsync(
        string path,
        int maximumDepth,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        return Task.Run(() => Scan(path, maximumDepth, progress, cancellationToken), cancellationToken);
    }

    private static ScanResult Scan(
        string path,
        int maximumDepth,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        string rootPath = Path.GetFullPath(path);
        var state = new ScanState(Math.Clamp(maximumDepth, 1, 128), progress, cancellationToken);
        DiskItem root = ScanDirectory(new DirectoryInfo(rootPath), null, 0, state);
        state.Files.Sort((left, right) => right.Size.CompareTo(left.Size));
        progress?.Report(new ScanProgress(state.Files.Count, state.FolderCount, state.TotalBytes, rootPath, 0));
        return new ScanResult(
            root,
            state.Files,
            state.Errors,
            state.ErrorCount,
            state.MaximumDepth,
            state.DepthLimitedFolderCount);
    }

    private static DiskItem ScanDirectory(DirectoryInfo directory, DiskItem? parent, int depth, ScanState state)
    {
        state.CancellationToken.ThrowIfCancellationRequested();

        string name = directory.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = directory.FullName;
        }

        var node = new DiskItem(name, directory.FullName, true, parent);
        state.FolderCount++;
        try
        {
            node.LastModified = directory.LastWriteTimeUtc;
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            state.AddError(directory.FullName, exception);
        }

        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(directory.FullName);
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            state.AddError(directory.FullName, exception);
            return node;
        }

        foreach (string entryPath in entries)
        {
            state.CancellationToken.ThrowIfCancellationRequested();
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(entryPath);
            }
            catch (Exception exception) when (IsFileSystemException(exception))
            {
                state.AddError(entryPath, exception);
                continue;
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if (depth >= state.MaximumDepth)
                {
                    state.DepthLimitedFolderCount++;
                    continue;
                }

                try
                {
                    DiskItem childDirectory = ScanDirectory(new DirectoryInfo(entryPath), node, depth + 1, state);
                    node.Children.Add(childDirectory);
                    node.Size += childDirectory.Size;
                    node.FileCount += childDirectory.FileCount;
                    node.FolderCount += childDirectory.FolderCount + 1;
                }
                catch (Exception exception) when (IsFileSystemException(exception))
                {
                    state.AddError(entryPath, exception);
                }
            }
            else
            {
                try
                {
                    var info = new FileInfo(entryPath);
                    var file = new DiskItem(info.Name, info.FullName, false, node)
                    {
                        Size = info.Length,
                        LastModified = info.LastWriteTimeUtc,
                        FileCount = 1
                    };
                    node.Children.Add(file);
                    node.Size += file.Size;
                    node.FileCount++;
                    state.Files.Add(file);
                    state.TotalBytes += file.Size;
                }
                catch (Exception exception) when (IsFileSystemException(exception))
                {
                    state.AddError(entryPath, exception);
                }
            }

            state.Report(entryPath, depth);
        }

        node.Children.Sort((left, right) =>
        {
            int directoryOrder = right.IsDirectory.CompareTo(left.IsDirectory);
            return directoryOrder != 0 ? directoryOrder : right.Size.CompareTo(left.Size);
        });
        return node;
    }

    private static bool IsFileSystemException(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or NotSupportedException
            or ArgumentException;
    }

    private sealed class ScanState
    {
        private readonly IProgress<ScanProgress>? _progress;
        private int _visited;

        public ScanState(int maximumDepth, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
        {
            MaximumDepth = maximumDepth;
            _progress = progress;
            CancellationToken = cancellationToken;
        }

        public CancellationToken CancellationToken { get; }
        public int MaximumDepth { get; }
        public List<DiskItem> Files { get; } = new();
        public List<ScanError> Errors { get; } = new();
        public int ErrorCount { get; private set; }
        public int DepthLimitedFolderCount { get; set; }
        public int FolderCount { get; set; }
        public long TotalBytes { get; set; }

        public void Report(string currentPath, int depth)
        {
            _visited++;
            if ((_visited & 255) == 0)
            {
                _progress?.Report(new ScanProgress(Files.Count, FolderCount, TotalBytes, currentPath, depth));
            }
        }

        public void AddError(string path, Exception exception)
        {
            ErrorCount++;
            if (Errors.Count < 100)
            {
                Errors.Add(new ScanError(path, exception.Message));
            }
        }
    }
}

public sealed class ScanResult
{
    public ScanResult(
        DiskItem root,
        IReadOnlyList<DiskItem> files,
        IReadOnlyList<ScanError> errors,
        int errorCount,
        int maximumDepth,
        int depthLimitedFolderCount)
    {
        Root = root;
        Files = files;
        Errors = errors;
        ErrorCount = errorCount;
        MaximumDepth = maximumDepth;
        DepthLimitedFolderCount = depthLimitedFolderCount;
    }

    public DiskItem Root { get; }
    public IReadOnlyList<DiskItem> Files { get; }
    public IReadOnlyList<ScanError> Errors { get; }
    public int ErrorCount { get; }
    public int MaximumDepth { get; }
    public int DepthLimitedFolderCount { get; }
}

public sealed class ScanProgress
{
    public ScanProgress(int fileCount, int folderCount, long totalBytes, string currentPath, int currentDepth)
    {
        FileCount = fileCount;
        FolderCount = folderCount;
        TotalBytes = totalBytes;
        CurrentPath = currentPath;
        CurrentDepth = currentDepth;
    }

    public int FileCount { get; }
    public int FolderCount { get; }
    public long TotalBytes { get; }
    public string CurrentPath { get; }
    public int CurrentDepth { get; }
}

public sealed class ScanError
{
    public ScanError(string path, string message)
    {
        Path = path;
        Message = message;
    }

    public string Path { get; }
    public string Message { get; }
}
