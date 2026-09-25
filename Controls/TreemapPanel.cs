using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Foundation;
using Windows.UI;
using WinDirectoryStats.Helpers;
using WinDirectoryStats.Models;

namespace WinDirectoryStats.Controls;

public sealed class TreemapPanel : Canvas
{
    private const int MaximumTiles = 700;
    private IReadOnlyList<DiskItem> _items = Array.Empty<DiskItem>();
    private string? _highlightExtension;
    private string? _selectedPath;
    private bool _renderQueued;

    public TreemapPanel()
    {
        SizeChanged += (_, _) => QueueRender();
    }

    public event EventHandler<TreemapItemInvokedEventArgs>? ItemInvoked;

    public void SetItems(IEnumerable<DiskItem> items)
    {
        _items = items
            .Where(item => !item.IsDirectory && item.Size > 0)
            .Take(MaximumTiles)
            .ToArray();
        _selectedPath = null;
        QueueRender();
    }

    public void SetExtensionHighlight(string? extension)
    {
        _highlightExtension = extension;
        QueueRender();
    }

    public void SetSelectedItem(DiskItem? item)
    {
        _selectedPath = item?.FullPath;
        QueueRender();
    }

    private void QueueRender()
    {
        if (_renderQueued)
        {
            return;
        }

        _renderQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _renderQueued = false;
            Render();
        }))
        {
            _renderQueued = false;
        }
    }

    private void Render()
    {
        try
        {
            Children.Clear();
            if (_items.Count == 0 || ActualWidth < 4 || ActualHeight < 4)
            {
                return;
            }

            var rectangles = BuildLayout(_items, new Rect(0, 0, ActualWidth, ActualHeight));
            foreach ((DiskItem item, Rect bounds) in rectangles)
            {
                if (bounds.Width < 1.5 || bounds.Height < 1.5)
                {
                    continue;
                }

                Color color = ExtensionPalette.GetColor(item.Extension);
                bool extensionDimmed = !string.IsNullOrEmpty(_highlightExtension)
                    && !string.Equals(item.Extension, _highlightExtension, StringComparison.OrdinalIgnoreCase);
                bool selected = string.Equals(item.FullPath, _selectedPath, StringComparison.OrdinalIgnoreCase);
                var tile = new Border
                {
                    Width = Math.Max(0, bounds.Width - 1),
                    Height = Math.Max(0, bounds.Height - 1),
                    Background = new SolidColorBrush(color),
                    BorderBrush = new SolidColorBrush(selected ? Colors.Yellow : Color.FromArgb(100, 0, 0, 0)),
                    BorderThickness = new Thickness(selected ? 4 : 1),
                    CornerRadius = new CornerRadius(bounds.Width > 8 && bounds.Height > 8 ? 2 : 0),
                    Opacity = extensionDimmed && !selected ? 0.18 : 1,
                    Tag = item
                };
                SetZIndex(tile, selected ? 1 : 0);

                if (bounds.Width >= 80 && bounds.Height >= 34)
                {
                    double luminance = 0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B;
                    tile.Child = new TextBlock
                    {
                        Text = bounds.Height >= 52 ? item.Name + "\n" + item.SizeText : item.Name,
                        Margin = new Thickness(5, 3, 5, 3),
                        Foreground = new SolidColorBrush(luminance > 155 ? Colors.Black : Colors.White),
                        FontSize = 11,
                        FontWeight = FontWeights.SemiBold,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        TextWrapping = TextWrapping.WrapWholeWords,
                        IsHitTestVisible = false
                    };
                }

                ToolTipService.SetToolTip(tile, item.FullPath + "\n" + item.SizeText + " · " + item.Extension);
                tile.Tapped += Tile_Tapped;
                SetLeft(tile, bounds.X);
                SetTop(tile, bounds.Y);
                Children.Add(tile);
            }
        }
        catch (Exception exception)
        {
            Children.Clear();
            App.LogException(exception, "TreemapPanel.Render");
        }
    }

    private void Tile_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is Border { Tag: DiskItem item })
        {
            _selectedPath = item.FullPath;
            ItemInvoked?.Invoke(this, new TreemapItemInvokedEventArgs(item));
            QueueRender();
        }
    }

    private static IReadOnlyList<(DiskItem Item, Rect Bounds)> BuildLayout(IReadOnlyList<DiskItem> items, Rect bounds)
    {
        var output = new List<(DiskItem Item, Rect Bounds)>(items.Count);
        var pending = new Stack<(int Start, int Count, Rect Bounds)>();
        pending.Push((0, items.Count, bounds));

        while (pending.Count > 0)
        {
            (int start, int count, Rect area) = pending.Pop();
            if (count == 1)
            {
                output.Add((items[start], area));
                continue;
            }

            long total = 0;
            for (int index = start; index < start + count; index++)
            {
                total += Math.Max(1, items[index].Size);
            }

            long target = total / 2;
            long firstTotal = 0;
            int firstCount = 0;
            while (firstCount < count - 1)
            {
                long next = Math.Max(1, items[start + firstCount].Size);
                if (firstCount > 0 && firstTotal + next > target)
                {
                    break;
                }

                firstTotal += next;
                firstCount++;
            }

            firstCount = Math.Clamp(firstCount, 1, count - 1);
            double ratio = total > 0 ? Math.Clamp(firstTotal / (double)total, 0.02, 0.98) : 0.5;
            Rect first;
            Rect second;
            if (area.Width >= area.Height)
            {
                double split = area.Width * ratio;
                first = new Rect(area.X, area.Y, split, area.Height);
                second = new Rect(area.X + split, area.Y, area.Width - split, area.Height);
            }
            else
            {
                double split = area.Height * ratio;
                first = new Rect(area.X, area.Y, area.Width, split);
                second = new Rect(area.X, area.Y + split, area.Width, area.Height - split);
            }

            pending.Push((start + firstCount, count - firstCount, second));
            pending.Push((start, firstCount, first));
        }

        return output;
    }
}

public sealed class TreemapItemInvokedEventArgs : EventArgs
{
    public TreemapItemInvokedEventArgs(DiskItem item)
    {
        Item = item;
    }

    public DiskItem Item { get; }
}
