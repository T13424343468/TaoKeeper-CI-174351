using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaoKeeper.AppKernel.ViewModels;
using TaoKeeper.Core.Modules;

namespace TaoKeeper.App.Views;

/// <summary>
/// 驱动升级页 code-behind：清册经 DriverPageViewModel 绑定；
/// 「备份并升级」= 确认弹窗点头 → VM.ConfirmUpgrade → 经 Core 三道闸执行；
/// 未确认/哈希失配/未知设备一律拒绝且不触达适配层（与 VM 同口径，本页不二次放行）。
/// </summary>
public sealed partial class DriverPage : Page
{
    private DriverPageViewModel? _vm;

    /// <summary>驱动适配器提供者（运行期接线；未接线时升级按钮如实提示）。</summary>
    public static Func<IDriverUpdateAdapter>? DriverAdapterProvider { get; set; }

    public DriverPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is DriverPageViewModel vm)
        {
            _vm = vm;
            Bind(vm);
        }
    }

    /// <summary>注入驱动清册数据（未传时如实显示未扫描）。</summary>
    public void Bind(DriverPageViewModel vm)
    {
        _vm = vm;
        SummaryText.Text = vm.SummaryText;
        DriverRows.ItemsSource = vm.Rows.Select(MakeRow).ToList();
    }

    private Grid MakeRow(DriverPageRow r)
    {
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 7, 0, 7) };
        for (var i = 0; i < 4; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });

        AddText(grid, 0, r.DeviceName, "TaoInkBrush", 14, Microsoft.UI.Text.FontWeights.SemiBold);
        AddText(grid, 1, r.InstalledVersionText, "TaoInk2Brush", 12.5, null);
        AddText(grid, 2, r.AvailableVersionText, "TaoInk2Brush", 12.5, null);
        AddText(grid, 3, r.SourceText, "TaoInk2Brush", 12.5, null);

        var action = new Border
        {
            Padding = new Thickness(10, 4, 10, 4),
            CornerRadius = new CornerRadius(10),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Background = (Brush)Application.Current.Resources["TaoAzureWashBrush"],
            Child = new TextBlock
            {
                Text = r.State switch
                {
                    DriverRowState.UpToDate => "—",
                    DriverRowState.BlockedByPolicy => "哈希失配 · 拦截",
                    DriverRowState.UpgradeNeedConfirm => "备份并升级（需确认）",
                    DriverRowState.UpgradeSilent => "备份并升级",
                    _ => "—",
                },
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources[
                    r.State switch
                    {
                        DriverRowState.BlockedByPolicy => "TaoCinnabarBrush",
                        DriverRowState.UpgradeNeedConfirm => "TaoGoldBrush",
                        _ => "TaoInk2Brush",
                    }],
            },
        };
        if (r.State is DriverRowState.UpgradeNeedConfirm or DriverRowState.UpgradeSilent)
        {
            var btn = new Button
            {
                Content = "备份并升级",
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 5, 12, 5),
            };
            btn.Click += async (_, _) => await _ = UpgradeAsync(r);
            Grid.SetColumn(btn, 4);
            grid.Children.Add(btn);
        }
        else
        {
            Grid.SetColumn(action, 4);
            grid.Children.Add(action);
        }
        return grid;
    }

    private static void AddText(Grid grid, int column, string text, string brushKey, double size, Microsoft.UI.Text.FontWeight? weight)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = size,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources[brushKey],
        };
        if (weight is { } w) tb.FontWeight = w;
        Grid.SetColumn(tb, column);
        grid.Children.Add(tb);
    }

    /// <summary>升级确认弹窗（真机呈现）：点头 → 确认 → 经 Core 三道闸执行 → 结果如实回显。</summary>
    private async System.Threading.Tasks.Task UpgradeAsync(DriverPageRow row)
    {
        var dialog = new ContentDialog
        {
            Title = $"升级 {row.DeviceName}",
            Content = $"{row.SourceText} · {row.InstalledVersionText} → {row.AvailableVersionText}\n\n" +
                      $"升级前将先备份当前驱动：{row.BackupCommand}\n回滚指引：{row.RollbackGuidance}\n\n确认后执行。",
            PrimaryButtonText = "确认升级",
            CloseButtonText = "先不",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        if (_vm is null || DriverAdapterProvider is null)
        {
            await ShowNote("驱动扫描通道尚未接线，本次未执行任何动作。");
            return;
        }

        var adapter = DriverAdapterProvider();
        _vm.ConfirmUpgrade(row.DeviceId);
        var result = await _vm.ExecuteUpgradeAsync(adapter, row.DeviceId);
        await ShowNote(result.Success
            ? $"已完成 {row.DeviceName} 升级（备份先行，可按指引回滚）。"
            : $"未执行升级：{result.Detail}");
    }

    private async System.Threading.Tasks.Task ShowNote(string text)
    {
        await new ContentDialog { Title = "驱动升级", Content = text, CloseButtonText = "好", XamlRoot = XamlRoot }.ShowAsync();
    }
}
