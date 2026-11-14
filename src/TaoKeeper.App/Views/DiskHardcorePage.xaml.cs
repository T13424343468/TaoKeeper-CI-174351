using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaoKeeper.AppKernel.ViewModels;
using TaoKeeper.Core.Modules.DiskHardcore;

namespace TaoKeeper.App.Views;

/// <summary>
/// P-6 硬核四项 code-behind：适配器由组合根接线（真机=扇区级 P/Invoke；假数据不出）。
/// 写路径前置确认：重建/标记点击先弹二次确认，确认后才走引擎（引擎侧再强制镜像先行）。
/// </summary>
public sealed partial class DiskHardcorePage : Page
{
    private readonly DiskHardcorePageViewModel _vm = new();

    /// <summary>磁盘适配器提供者（组合根接线；未接线时如实提示不可用，不假数据）。</summary>
    public static Func<IHardcoreDiskAdapter>? AdapterProvider { get; set; }

    /// <summary>内存页测试器提供者（可空；未接线时内存坏页一栏不预填）。</summary>
    public static Func<IMemoryPageTester>? MemoryTesterProvider { get; set; }

    public DiskHardcorePage() => InitializeComponent();

    private void OnScan(object sender, RoutedEventArgs e)
    {
        if (AdapterProvider is null)
        {
            EmptyLine.Text = "磁盘通道未就绪（等待运行期接线），不假数据。";
            EmptyLine.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            _vm.MirrorAndScan(AdapterProvider.Invoke(), MemoryTesterProvider?.Invoke());
            Render();
        }
        catch (Exception ex)
        {
            EmptyLine.Text = $"扫描中断：{ex.Message}";
            EmptyLine.Visibility = Visibility.Visible;
        }
    }

    private async void OnRebuild(object sender, RoutedEventArgs e)
    {
        if (!_vm.HasRebuildPlan)
        {
            EmptyLine.Text = _vm.IsScanned ? "当前没有可执行的重建方案（分区表正常）。" : _vm.EmptyText;
            EmptyLine.Visibility = Visibility.Visible;
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "确认在镜像上重建分区表？",
            Content = "源盘不会被动；镜像可回滚。这是一次写镜像操作，确认执行吗？",
            PrimaryButtonText = "确认执行",
            CloseButtonText = "再想想",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        _vm.ApplyRebuild();
        Render();
    }

    private async void OnMark(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "确认标记坏道？",
            Content = "标记会写盘（确认后执行）；缓解走文件系统级挪避，不格式化。",
            PrimaryButtonText = "确认标记",
            CloseButtonText = "再想想",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        if (_vm.MarkBadSectors()) Render();
    }

    /// <summary>恢复到用户选定目录（R-P10-5b：只写目标目录，源盘零改动）。</summary>
    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        if (!_vm.IsScanned)
        {
            EmptyLine.Text = _vm.EmptyText;
            EmptyLine.Visibility = Visibility.Visible;
            return;
        }

        // WinUI 桌面：FolderPicker 需要窗口句柄初始化（与文件粉碎选择器同口径）
        var shell = (Application.Current as App)?.Shell;
        if (shell is null)
        {
            EmptyLine.Text = "窗口尚未就绪，无法打开目录选择器。";
            EmptyLine.Visibility = Visibility.Visible;
            return;
        }
        var picker = new Windows.Storage.Pickers.FolderPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop,
        };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(shell));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        var (restored, message) = _vm.RestoreTo(folder.Path);
        EmptyLine.Text = message;
        EmptyLine.Visibility = Visibility.Visible;
    }

    private void Render()
    {
        EmptyLine.Visibility = Visibility.Collapsed;
        RowList.Items.Clear();
        foreach (var row in _vm.Rows)
        {
            var panel = new StackPanel { Spacing = 2 };
            panel.Children.Add(new TextBlock
            {
                FontSize = 12.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Text = $"· {row.ToolLabel}：{row.Summary}",
            });
            if (!string.IsNullOrEmpty(row.Detail))
            {
                panel.Children.Add(new TextBlock
                {
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.8,
                    Text = $"  {row.Detail}",
                });
            }
            RowList.Items.Add(panel);
        }

        FileList.Items.Clear();
        foreach (var f in _vm.RecoverableFiles)
        {
            FileList.Items.Add(new TextBlock
            {
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Text = f,
            });
        }
    }
}
