using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoKeeper.AppKernel.ViewModels;
using TaoKeeper.Core.Models;
using TaoKeeper.Core.Modules;
using TaoKeeper.Core.Services;

namespace TaoKeeper.App.Views;

/// <summary>
/// 相似照片页 code-behind：组经 SimilarPhotoPageViewModel 绑定（感知哈希分组，海明距离≤阈值才判相似）；
/// 页内即扫（与空间分析同节奏）：进入即只读枚举图片目录→逐张解码建纹→分组展示；
/// 逐张勾选走 VM.Toggle（保留份不可勾）、改选保留走 VM.SetKeep（无效选择不动作不报错）；
/// 「转入确认队列」经 CleanupQueueViewModel 产出待执行清单（闸门标死受保护路径），
/// 实际执行由统一清理链编排（与磁盘清理/重复文件同一条确认链），本页零写动作。
/// </summary>
public sealed partial class SimilarPhotoPage : Page
{
    private SimilarPhotoPageViewModel? _vm;
    private bool _scanning;

    /// <summary>图片扫描根（宿主接线；用户图片目录，只读）。</summary>
    public static Func<IReadOnlyList<string>>? ScanRootsProvider { get; set; }

    public SimilarPhotoPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is SimilarPhotoPageViewModel vm)
        {
            _vm = vm;
            Refresh();
            if (!vm.HasGroups && !_scanning) _ = RunScanAsync();
        }
    }

    private void Refresh()
    {
        if (_vm is null) return;

        var groups = _vm.Groups;
        SummaryText.Text = _vm.SummaryText;
        EmptyHint.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        GroupList.ItemsSource = groups.Select(MakeGroupCard).ToList();

        TotalCard.Visibility = groups.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        TotalText.Text = $"拟处理 {_vm.SelectedCount} 张，按当前勾选可释放 {Fmt(_vm.SelectedBytes)}";
    }

    /// <summary>P10 相似图片：只读实扫图片目录（真实路径+字节数），处置回确认队列走闸门。</summary>
    private async Task RunScanAsync()
    {
        _scanning = true;
        try
        {
            var roots = ScanRootsProvider?.Invoke() ?? Array.Empty<string>();
            if (roots.Count == 0)
            {
                StatusText.Text = "未找到可扫描的图片目录；未做任何扫描。";
                return;
            }

            StatusText.Text = "扫描中…（只读枚举图片目录，不动任何文件）";
            var scan = await new PortableDiskScanner().ScanAsync(roots, ScanOptions.Defaults(ScanMode.Quick));
            var progress = new Progress<SimilarPhotoScanProgress>(p =>
                StatusText.Text = $"识别中…{p.Processed}/{p.Total}（不可识别跳过 {p.Skipped}）");
            var groups = await new SimilarPhotoFinder(new WinuiImageGridSource())
                .FindAsync(scan.Files, options: new SimilarPhotoOptions(), progress: progress);

            _vm!.BuildFromGroups(groups);
            Refresh();
            StatusText.Text = $"实扫完成：{scan.TotalFiles} 个文件（只读）；处置请走确认队列闸门。";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "扫描已取消；未产生任何结果。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"扫描未完成：{ex.Message}（未动任何文件）";
        }
        finally
        {
            _scanning = false;
        }
    }

    private UIElement MakeGroupCard(SimilarPhotoPageViewModel.SimilarGroupItem group)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(18, 14, 18, 14),
            Margin = new Thickness(0, 0, 0, 12),
            BorderBrush = (Brush)Application.Current.Resources["TaoLineSoftBrush"],
            Background = (Brush)Application.Current.Resources["TaoCardBrush"],
        };

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = $"相似组 · {Fmt(group.TotalBytes)} × {group.Photos.Count} 张（拟处理 {group.SelectedCount} 张，可释放 {Fmt(group.SelectedBytes)}）",
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TaoInkBrush"],
        });

        // 保留份（金色徽标 + 路径 + 「设为保留份」按钮组内任一行均可改选）
        foreach (var photo in group.Photos)
        {
            var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var pathText = new TextBlock
            {
                Text = photo.FullPath == group.KeepPath
                    ? $"★ 保留：{photo.FullPath}（{photo.Width}×{photo.Height}，{Fmt(photo.SizeBytes)}）"
                    : $"{photo.FullPath}（{photo.Width}×{photo.Height}，{Fmt(photo.SizeBytes)}）",
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources[photo.FullPath == group.KeepPath ? "TaoGoldBrush" : "TaoInk2Brush"],
            };
            Grid.SetColumn(pathText, 0);
            row.Children.Add(pathText);

            // 拟处理勾选：保留份恒不勾且不可勾（保留=未勾选，想多留几张就多取消几张）
            var check = new CheckBox
            {
                Content = "拟处理",
                IsChecked = group.IsSelected(photo.FullPath),
                IsEnabled = photo.FullPath != group.KeepPath,
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 0,
            };
            check.Checked += (_, _) => { group.Toggle(photo.FullPath); Refresh(); };
            check.Unchecked += (_, _) => { group.Toggle(photo.FullPath); Refresh(); };
            Grid.SetColumn(check, 1);
            row.Children.Add(check);

            var keepButton = new Button
            {
                Content = "设为保留份",
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 4, 10, 4),
                IsEnabled = photo.FullPath != group.KeepPath,
                VerticalAlignment = VerticalAlignment.Center,
            };
            keepButton.Click += (_, _) => OnKeepClick(group, photo.FullPath);
            Grid.SetColumn(keepButton, 2);
            row.Children.Add(keepButton);
            panel.Children.Add(row);
        }

        card.Child = panel;
        return card;
    }

    private void OnKeepClick(SimilarPhotoPageViewModel.SimilarGroupItem group, string path)
    {
        if (_vm is null) return;

        // 无效选择（路径不在本组/已是保留份）SetKeep 返 false，界面原样不动（与 VM 同口径，不弹错打断）
        group.SetKeep(path);
        Refresh();
    }

    private async Task ConfirmQueueAsync()
    {
        if (_vm is null) return;

        // 零写动作：只产出待执行清单（受保护路径已由闸门在源头标死）；进回收站由编排层执行（统一清理链）
        var queue = _vm.BuildCleanupQueue();
        queue.SelectAll(); // 拟处理张即用户已选定的清理意图，逐条全选可确认项
        var refused = queue.Items.Count(i => !i.CanConfirm);
        var refusalNote = refused > 0 ? $"\n另有 {refused} 条受保护路径条目已被安全闸门标死，不会执行。" : string.Empty;

        if (queue.ConfirmedCount == 0)
        {
            await new ContentDialog
            {
                Title = "没有可处理项",
                Content = "当前没有勾选拟处理的相似照片；想留几张就在组里多取消几张，保留份不占清理名额。",
                CloseButtonText = "好",
                XamlRoot = XamlRoot,
            }.ShowAsync();
            return;
        }

        await new ContentDialog
        {
            Title = "转入清理确认队列（先进回收站）",
            Content = $"已确认 {queue.ConfirmedCount} 张，合计 {Fmt(queue.ConfirmedBytes)}。{refusalNote}\n\n执行编排接线后，这里将把清单交给清理协调器逐条进回收站（可还原）。当前为展示确认，不做任何删除。",
            CloseButtonText = "好",
            XamlRoot = XamlRoot,
        }.ShowAsync();
    }

    private void OnQueueClick(object sender, RoutedEventArgs e) => _ = ConfirmQueueAsync();

    private static string Fmt(long bytes)
    {
        double v = bytes;
        const long GB = 1024L * 1024 * 1024, MB = 1024 * 1024;
        return bytes >= GB ? $"{v / GB:F1} GB"
             : bytes >= MB ? $"{v / MB:F0} MB"
             : $"{bytes} B";
    }
}

/// <summary>
/// 图片灰度格来源（WinUI 适配）：BitmapDecoder 解码为原始分辨率 + 8×8 灰度格（解码器负责缩放）。
/// 只读实现：只读文件流，不改名、不移动、不删除；文件不可解（损坏/不支持格式）返回 null，由识别方如实计跳过。
/// </summary>
internal sealed class WinuiImageGridSource : IImageGridSource
{
    public async Task<ImageGrayGrid?> DecodeAsync(string fullPath, CancellationToken cancellation = default)
    {
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(fullPath);
            await using var stream = await file.OpenReadAsync();

            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
            var transform = new Windows.Graphics.Imaging.BitmapTransform { ScaledWidth = 8, ScaledHeight = 8 };
            var pixelData = await decoder.GetPixelDataAsync(
                Windows.Graphics.Imaging.BitmapPixelFormat.Gray8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                transform,
                Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation,
                Windows.Graphics.Imaging.ColorManagementMode.ColorManageToSRgb);

            var gray = pixelData.DetachPixelData();
            if (gray.Length != 64) return null; // 解码器未按 8×8 缩放时如实拒绝，不造假格
            return new ImageGrayGrid((int)decoder.OrientedPixelWidth, (int)decoder.OrientedPixelHeight, gray);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null; // 不可解（损坏/不支持/路径已失效）按契约返回 null，由调用方计跳过
        }
    }
}
