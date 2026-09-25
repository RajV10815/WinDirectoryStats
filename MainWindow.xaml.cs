using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.VisualBasic.FileIO;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinDirectoryStats.Controls;
using WinDirectoryStats.Helpers;
using WinDirectoryStats.Models;
using WinDirectoryStats.Services;
using WinRT.Interop;

namespace WinDirectoryStats;

public sealed partial class MainWindow : Window
{
    private const int MaximumVisibleFiles = 1000;
    private const int MaximumExtensionRows = 256;
    private readonly AppWindow _appWindow;
    private readonly DiskScanner _scanner = new();
    private CancellationTokenSource? _scanCancellation;
    private ScanResult? _scanResult;
    private DiskItem? _selectedDirectory;
    private DiskItem? _selectedFile;
    private List<DiskItem> _directoryFiles = new();
    private string? _lastScanPath;
    private string _fileSort = "Size";
    private bool _fileSortDescending = true;
    private int _maximumDepth = 6;

    public ObservableCollection<DriveOption> Drives { get; } = new();
    public ObservableCollection<DiskItem> LargestFiles { get; } = new();
    public ObservableCollection<ExtensionStat> ExtensionStats { get; } = new();

    public MainWindow()
    {
        InitializeComponent();

        IntPtr windowHandle = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(windowHandle));
        _appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }
        TrySetWindowIcon();
        LoadDrives();
        DriveCombo.ItemsSource = Drives;
        Closed += (_, _) =>
        {
            _scanCancellation?.Cancel();
            _scanCancellation?.Dispose();
        };
    }

    public void OpenPath(string path)
    {
        DispatcherQueue.TryEnqueue(() => _ = StartScanAsync(path));
    }

    private async void ScanFolder_Click(object sender, RoutedEventArgs e)
    {
        StorageFolder? folder = await PickFolderAsync();
        if (folder is not null)
        {
            DriveOption? existing = Drives.FirstOrDefault(
                option => string.Equals(option.Path, folder.Path, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                existing = new DriveOption($"Selected folder — {folder.Path}", folder.Path);
                Drives.Insert(0, existing);
            }

            DriveCombo.SelectedItem = existing;
        }
    }

    private async void ScanDrive_Click(object sender, RoutedEventArgs e)
    {
        if (DriveCombo.SelectedItem is DriveOption drive)
        {
            await StartScanAsync(drive.Path);
        }
    }

    private async void Rescan_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_lastScanPath))
        {
            await StartScanAsync(_lastScanPath);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _scanCancellation?.Cancel();
    }

    private void DepthDecrease_Click(object sender, RoutedEventArgs e)
    {
        SetMaximumDepth(_maximumDepth - 1);
    }

    private void DepthIncrease_Click(object sender, RoutedEventArgs e)
    {
        SetMaximumDepth(_maximumDepth + 1);
    }

    private void SetMaximumDepth(int value)
    {
        _maximumDepth = Math.Clamp(value, 1, 128);
        DepthText.Text = _maximumDepth.ToString();
        DepthDecreaseButton.IsEnabled = _maximumDepth > 1 && _scanCancellation is null;
        DepthIncreaseButton.IsEnabled = _maximumDepth < 128 && _scanCancellation is null;
    }

    private async Task StartScanAsync(string path)
    {
        if (!Directory.Exists(path))
        {
            StatusText.Text = "The selected folder is no longer available.";
            return;
        }

        _scanCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;
        _lastScanPath = Path.GetFullPath(path);
        int maximumDepth = GetMaximumDepth();
        ClearResultsForScan(_lastScanPath);
        SetScanningState(true);

        var progress = new Progress<ScanProgress>(scanProgress =>
        {
            if (!ReferenceEquals(_scanCancellation, cancellation))
            {
                return;
            }

            StatusText.Text = $"Scanning depth {scanProgress.CurrentDepth}/{maximumDepth}: {scanProgress.CurrentPath}";
            SummaryText.Text = $"{scanProgress.FileCount:N0} files · {scanProgress.FolderCount:N0} folders · {SizeFormatter.Format(scanProgress.TotalBytes)}";
        });

        try
        {
            ScanResult result = await _scanner.ScanAsync(_lastScanPath, maximumDepth, progress, cancellation.Token);
            if (!ReferenceEquals(_scanCancellation, cancellation))
            {
                return;
            }

            ApplyScanResult(result);
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_scanCancellation, cancellation))
            {
                StatusText.Text = "Scan stopped.";
                SummaryText.Text = string.Empty;
            }
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_scanCancellation, cancellation))
            {
                StatusText.Text = "Scan failed: " + exception.Message;
                SummaryText.Text = string.Empty;
            }
        }
        finally
        {
            if (ReferenceEquals(_scanCancellation, cancellation))
            {
                _scanCancellation = null;
                SetScanningState(false);
            }

            cancellation.Dispose();
        }
    }

    private void ClearResultsForScan(string path)
    {
        _scanResult = null;
        _selectedDirectory = null;
        _selectedFile = null;
        _directoryFiles.Clear();
        LargestFiles.Clear();
        ExtensionStats.Clear();
        DirectoryTree.RootNodes.Clear();
        ExtensionsList.SelectedItem = null;
        Treemap.SetItems(Array.Empty<DiskItem>());
        TreemapEmptyText.Visibility = Visibility.Visible;
        SelectedNameText.Text = "Scanning…";
        SelectedPathText.Text = path;
        SelectedSizeText.Text = "—";
        SelectedFilesText.Text = "—";
        SelectedFoldersText.Text = "—";
        ErrorsButton.Visibility = Visibility.Collapsed;
        SetSelectedFile(null);
    }

    private void ApplyScanResult(ScanResult result)
    {
        _scanResult = result;
        result.Root.ViewPercentage = 100;
        TreeViewNode rootNode = CreateTreeNode(result.Root, true);
        rootNode.IsExpanded = true;
        DirectoryTree.RootNodes.Add(rootNode);
        DirectoryTree.SelectedNode = rootNode;
        if (!ReferenceEquals(_selectedDirectory, result.Root))
        {
            SelectDirectory(result.Root);
        }

        ErrorsButton.Visibility = result.ErrorCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        ErrorsButton.Content = result.ErrorCount == 1 ? "1 scan warning" : $"{result.ErrorCount:N0} scan warnings";
        var completionNotes = new List<string>();
        if (result.DepthLimitedFolderCount > 0)
        {
            completionNotes.Add($"{result.DepthLimitedFolderCount:N0} deeper folder(s) skipped at the depth limit");
        }

        if (result.ErrorCount > 0)
        {
            completionNotes.Add($"{result.ErrorCount:N0} inaccessible or unreadable path(s) skipped");
        }

        StatusText.Text = completionNotes.Count == 0
            ? $"Scan complete through depth {result.MaximumDepth}."
            : "Scan complete. " + string.Join("; ", completionNotes) + ".";
        SummaryText.Text = $"{result.Files.Count:N0} files · {result.Root.FolderCount:N0} folders · {SizeFormatter.Format(result.Root.Size)} · depth ≤ {result.MaximumDepth}";
    }

    private TreeViewNode CreateTreeNode(DiskItem item, bool materializeChildren = false)
    {
        if (item.Parent is not null)
        {
            item.ViewPercentage = item.Parent.Size > 0 ? item.Size * 100.0 / item.Parent.Size : 0;
        }

        var node = new TreeViewNode { Content = item };
        List<DiskItem> childDirectories = item.Children.Where(child => child.IsDirectory).ToList();
        if (materializeChildren)
        {
            foreach (DiskItem child in childDirectories)
            {
                node.Children.Add(CreateTreeNode(child));
            }
        }
        else
        {
            node.HasUnrealizedChildren = childDirectories.Count > 0;
        }

        return node;
    }

    private void DirectoryTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        TreeViewNode node = args.Node;
        if (!node.HasUnrealizedChildren || node.Content is not DiskItem directory)
        {
            return;
        }

        node.HasUnrealizedChildren = false;
        foreach (DiskItem child in directory.Children.Where(item => item.IsDirectory))
        {
            node.Children.Add(CreateTreeNode(child));
        }
    }

    private void DirectoryTree_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (sender.SelectedNode?.Content is DiskItem directory)
        {
            SelectDirectory(directory);
        }
    }

    private void SelectDirectory(DiskItem directory)
    {
        if (_scanResult is null || !directory.IsDirectory)
        {
            return;
        }

        _selectedDirectory = directory;
        _directoryFiles = _scanResult.Files
            .Where(file => file.IsDescendantOf(directory))
            .ToList();

        SelectedNameText.Text = directory.Name;
        SelectedPathText.Text = directory.FullPath;
        SelectedSizeText.Text = directory.SizeText;
        SelectedFilesText.Text = directory.FileCount.ToString("N0");
        SelectedFoldersText.Text = directory.FolderCount.ToString("N0");
        UpButton.IsEnabled = directory.Parent is not null;
        SetSelectedFile(null);
        ExtensionsList.SelectedItem = null;
        BuildExtensionStats();
        RefreshFileList();
        Treemap.SetExtensionHighlight(null);
        Treemap.SetItems(_directoryFiles);
        TreemapEmptyText.Visibility = _directoryFiles.Any(file => file.Size > 0) ? Visibility.Collapsed : Visibility.Visible;
        TreemapEmptyText.Text = _directoryFiles.Count == 0 ? "This folder contains no files" : "All files in this folder are empty";
    }

    private void BuildExtensionStats()
    {
        ExtensionStats.Clear();
        long totalSize = _directoryFiles.Sum(file => file.Size);
        var groups = _directoryFiles
            .GroupBy(file => file.Extension, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Extension = group.Key,
                Size = group.Sum(file => file.Size),
                FileCount = group.Count()
            })
            .OrderByDescending(group => group.Size)
            .ThenBy(group => group.Extension, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int individualRows = Math.Min(groups.Count, MaximumExtensionRows - 1);
        foreach (var group in groups.Take(individualRows))
        {
            ExtensionStats.Add(new ExtensionStat(group.Extension, group.Size, group.FileCount, totalSize));
        }

        if (groups.Count > individualRows)
        {
            ExtensionStats.Add(new ExtensionStat(
                "(other extensions)",
                groups.Skip(individualRows).Sum(group => group.Size),
                groups.Skip(individualRows).Sum(group => group.FileCount),
                totalSize));
        }
    }

    private void RefreshFileList()
    {
        string filter = FileFilterBox.Text.Trim();
        IEnumerable<DiskItem> files = _directoryFiles;
        if (!string.IsNullOrEmpty(filter))
        {
            files = files.Where(file =>
                file.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || file.FullPath.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || file.Extension.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        files = _fileSort switch
        {
            "Name" => _fileSortDescending
                ? files.OrderByDescending(file => file.Name, StringComparer.CurrentCultureIgnoreCase)
                : files.OrderBy(file => file.Name, StringComparer.CurrentCultureIgnoreCase),
            "Modified" => _fileSortDescending
                ? files.OrderByDescending(file => file.LastModified)
                : files.OrderBy(file => file.LastModified),
            _ => _fileSortDescending
                ? files
                : files.OrderBy(file => file.Size)
        };

        LargestFiles.Clear();
        long directorySize = Math.Max(1, _selectedDirectory?.Size ?? 0);
        foreach (DiskItem file in files.Take(MaximumVisibleFiles))
        {
            file.ViewPercentage = file.Size * 100.0 / directorySize;
            LargestFiles.Add(file);
        }
    }

    private void FileFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshFileList();
    }

    private void SortFiles_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string sort })
        {
            return;
        }

        if (string.Equals(_fileSort, sort, StringComparison.Ordinal))
        {
            _fileSortDescending = !_fileSortDescending;
        }
        else
        {
            _fileSort = sort;
            _fileSortDescending = !string.Equals(sort, "Name", StringComparison.Ordinal);
        }

        RefreshFileList();
    }

    private void FilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SetSelectedFile(FilesList.SelectedItem as DiskItem);
    }

    private void ResultsSplitter_DragDelta(object sender, DragDeltaEventArgs e)
    {
        double availableWidth = DetailsColumn.ActualWidth + TreemapColumn.ActualWidth;
        double detailsWidth = Math.Clamp(
            DetailsColumn.ActualWidth + e.HorizontalChange,
            DetailsColumn.MinWidth,
            availableWidth - TreemapColumn.MinWidth);

        DetailsColumn.Width = new GridLength(detailsWidth);
        TreemapColumn.Width = new GridLength(availableWidth - detailsWidth);
    }

    private void FilesList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        OpenSelectedFile();
    }

    private void ExtensionsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        string? extension = (ExtensionsList.SelectedItem as ExtensionStat)?.Extension;
        Treemap.SetExtensionHighlight(extension);
        TreemapHintText.Text = extension is null
            ? "Area = file size · Color = extension"
            : $"Highlighting {extension} · Select another extension to change";
    }

    private void Treemap_ItemInvoked(object sender, TreemapItemInvokedEventArgs e)
    {
        SetSelectedFile(e.Item);
        ResultsTabView.SelectedItem = LargestFilesTab;
        if (LargestFiles.Contains(e.Item))
        {
            FilesList.SelectedItem = e.Item;
            FilesList.ScrollIntoView(e.Item);
        }
    }

    private void SetSelectedFile(DiskItem? file)
    {
        _selectedFile = file;
        Treemap.SetSelectedItem(file);
        if (file is not null)
        {
            StatusText.Text = file.FullPath + " · " + file.SizeText;
        }
    }

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDirectory?.Parent is DiskItem parent)
        {
            SelectDirectory(parent);
            SelectTreeNodeForDirectory(parent);
        }
    }

    private void SelectTreeNodeForDirectory(DiskItem directory)
    {
        foreach (TreeViewNode root in DirectoryTree.RootNodes)
        {
            TreeViewNode? match = FindMaterializedNode(root, directory);
            if (match is not null)
            {
                DirectoryTree.SelectedNode = match;
                return;
            }
        }
    }

    private static TreeViewNode? FindMaterializedNode(TreeViewNode node, DiskItem directory)
    {
        if (ReferenceEquals(node.Content, directory))
        {
            return node;
        }

        foreach (TreeViewNode child in node.Children)
        {
            TreeViewNode? match = FindMaterializedNode(child, directory);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private void OpenSelected_Click(object sender, RoutedEventArgs e)
    {
        OpenSelectedFile();
    }

    private void OpenSelectedFile()
    {
        if (_selectedFile is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(_selectedFile.FullPath) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            StatusText.Text = "Could not open the file: " + exception.Message;
        }
    }

    private void RevealSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedFile is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + _selectedFile.FullPath + "\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            StatusText.Text = "Could not open Explorer: " + exception.Message;
        }
    }

    private void CopySelected_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedFile is null)
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(_selectedFile.FullPath);
        Clipboard.SetContent(package);
        Clipboard.Flush();
        StatusText.Text = "Path copied to the clipboard.";
    }

    private async void RecycleSelected_Click(object sender, RoutedEventArgs e)
    {
        DiskItem? file = _selectedFile;
        if (file is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Move this file to the Recycle Bin?",
            Content = file.FullPath + "\n\n" + file.SizeText,
            PrimaryButtonText = "Move to Recycle Bin",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            FileSystem.DeleteFile(file.FullPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            StatusText.Text = "File moved to the Recycle Bin. Refreshing the scan…";
            if (!string.IsNullOrEmpty(_lastScanPath))
            {
                await StartScanAsync(_lastScanPath);
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = "Could not recycle the file: " + exception.Message;
        }
    }

    private async void Errors_Click(object sender, RoutedEventArgs e)
    {
        if (_scanResult is null || _scanResult.ErrorCount == 0)
        {
            return;
        }

        string details = string.Join("\n\n", _scanResult.Errors.Select(error => error.Path + "\n" + error.Message));
        if (_scanResult.ErrorCount > _scanResult.Errors.Count)
        {
            details += $"\n\n…and {_scanResult.ErrorCount - _scanResult.Errors.Count:N0} more warning(s).";
        }

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Paths skipped during the scan",
            Content = new ScrollViewer
            {
                MaxHeight = 520,
                Content = new TextBlock { Text = details, TextWrapping = TextWrapping.Wrap }
            },
            CloseButtonText = "Close"
        };
        await dialog.ShowAsync();
    }

    private async Task<StorageFolder?> PickFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        return await picker.PickSingleFolderAsync();
    }

    private void LoadDrives()
    {
        Drives.Clear();
        var addedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddLocation("User profile", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), addedPaths);
        AddLocation("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), addedPaths);
        AddLocation("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), addedPaths);
        AddLocation("Downloads", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), addedPaths);
        AddLocation("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), addedPaths);
        AddLocation("Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), addedPaths);
        AddLocation("Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), addedPaths);
        AddLocation("AppData (Roaming)", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), addedPaths);
        AddLocation("AppData (Local)", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), addedPaths);
        AddLocation("Program Files", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), addedPaths);
        AddLocation("Program Files (x86)", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), addedPaths);

        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady && drive.DriveType != DriveType.CDRom)
                {
                    Drives.Add(new DriveOption(drive));
                    addedPaths.Add(drive.RootDirectory.FullName);
                }
            }
            catch
            {
                // A removable drive can disappear while this list is being built.
            }
        }

        if (Drives.Count > 0)
        {
            DriveCombo.SelectedIndex = 0;
        }
    }

    private void AddLocation(string name, string path, ISet<string> addedPaths)
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path) && addedPaths.Add(path))
        {
            Drives.Add(new DriveOption($"{name} — {path}", path));
        }
    }

    private void SetScanningState(bool isScanning)
    {
        ScanProgressBar.Visibility = isScanning ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = isScanning ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = isScanning;
        RescanButton.IsEnabled = !isScanning && !string.IsNullOrEmpty(_lastScanPath);
        DriveCombo.IsEnabled = !isScanning;
        BrowseFolderButton.IsEnabled = !isScanning;
        StartScanButton.IsEnabled = !isScanning && DriveCombo.SelectedItem is DriveOption;
        DepthDecreaseButton.IsEnabled = !isScanning && _maximumDepth > 1;
        DepthIncreaseButton.IsEnabled = !isScanning && _maximumDepth < 128;
    }

    private int GetMaximumDepth()
    {
        return _maximumDepth;
    }

    private void TrySetWindowIcon()
    {
        try
        {
            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (File.Exists(iconPath))
            {
                _appWindow.SetIcon(iconPath);
            }
        }
        catch
        {
            // The app can continue with the package-provided default icon.
        }
    }
}
