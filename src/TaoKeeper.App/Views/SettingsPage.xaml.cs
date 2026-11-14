using System;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaoKeeper.App.Theming;
using TaoKeeper.AppKernel.Navigation;
using TaoKeeper.AppKernel.Settings;
using TaoKeeper.AppKernel.Theming;
using TaoKeeper.AppKernel.ViewModels;

namespace TaoKeeper.App.Views;

/// <summary>
/// 设置页 code-behind：Bind(vm) 注入（OnNavigatedTo 取导航参数，未传=默认态兜底）。
/// 道系边界：默认零写动作——开关/主题/阈值全部只把改动记入 VM 待确认副本，
/// 点底部「保存改动」才经既有 AppSettingsStore 落盘（未接线时如实标明仅本会话生效）；
/// 红线锁定项零控件零写。主题与挂件入口的生效动作随运行期接线，本页只呈现待确认选择。
/// </summary>
public sealed partial class SettingsPage : Page
{
    private SettingsPageViewModel _vm = new();
    private bool _suppressToggles;

    public SettingsPage()
    {
        InitializeComponent();
        Bind(_vm);
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // 主窗在导航参数里带 VM（运行期接线前为 null → 默认态兜底）
        if (e.Parameter is SettingsPageViewModel vm) Bind(vm);
    }

    /// <summary>注入设置数据：控件态一律取 VM 待确认副本，确认条按有无改动显隐。</summary>
    public void Bind(SettingsPageViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        _vm = vm;

        _suppressToggles = true;
        LaunchSwitch.IsOn = vm.Pending.LaunchOnStartup;
        AutoCheckupSwitch.IsOn = vm.Pending.AutoCheckupOnStartup;
        SilentUpgradeSwitch.IsOn = vm.Pending.SilentUpgrade;
        ShortcutSwitch.IsOn = vm.Pending.CreateShortcutAfterInstall;
        LedgerSwitch.IsOn = vm.Pending.RecordInstallLedger;
        StorageSenseSwitch.IsOn = vm.Pending.StorageSenseEnabled;
        QuickScanSwitch.IsOn = vm.Pending.QuickScanByDefault;
        TrayResidentSwitch.IsOn = vm.Pending.TrayResident;
        WidgetSwitch.IsOn = vm.Pending.ShowDesktopWidget;
        _suppressToggles = false;

        // 文案映射（主题/挂件目录、阈值行、位置行）与待确认选择态
        InstallDirText.Text = vm.DefaultInstallDirLine;
        MemoryAlertText.Text = vm.MemoryAlertLine;
        DiskFreeAlertText.Text = vm.DiskFreeAlertLine;

        MarkSelected(ThemeShuimo, vm.Pending.Theme == ThemeName.Shuimo);
        MarkSelected(ThemeTianqing, vm.Pending.Theme == ThemeName.Tianqing);
        MarkSelected(ThemeMobai, vm.Pending.Theme == ThemeName.Mobai);

        MarkSelected(ContentMemory, vm.Pending.WidgetContents.Contains(WidgetContent.Memory));
        MarkSelected(ContentJunkPending, vm.Pending.WidgetContents.Contains(WidgetContent.JunkPending));
        MarkSelected(ContentDiskFree, vm.Pending.WidgetContents.Contains(WidgetContent.DiskFree));

        MarkSelected(SizeStandard, vm.Pending.WidgetSize == WidgetSize.Standard);
        MarkSelected(SizeCompact, vm.Pending.WidgetSize == WidgetSize.Compact);

        ThresholdError.Visibility = Visibility.Collapsed;
        PendingBar.Visibility = vm.HasPendingChanges ? Visibility.Visible : Visibility.Collapsed;
        PendingSummary.Text = vm.PendingSummaryText;
        SaveButton.Content = vm.PendingSaveText;
    }

    // ── 开关（改动只进待确认副本） ──

    private void OnLaunchToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressToggles) return;
        _vm.SetPendingLaunchOnStartup(LaunchSwitch.IsOn);
        Refresh();
    }

    private void OnAutoCheckupToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressToggles) return;
        _vm.SetPendingAutoCheckupOnStartup(AutoCheckupSwitch.IsOn);
        Refresh();
    }

    private void OnSilentUpgradeToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressToggles) return;
        _vm.SetPendingSilentUpgrade(SilentUpgradeSwitch.IsOn);
        Refresh();
    }

    private void OnShortcutToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressToggles) return;
        _vm.SetPendingCreateShortcutAfterInstall(ShortcutSwitch.IsOn);
        Refresh();
    }

    private void OnLedgerToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressToggles) return;
        _vm.SetPendingRecordInstallLedger(LedgerSwitch.IsOn);
        Refresh();
    }

    private void OnStorageSenseToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressToggles) return;
        _vm.SetPendingStorageSenseEnabled(StorageSenseSwitch.IsOn);
        Refresh();
    }

    private void OnQuickScanToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressToggles) return;
        _vm.SetPendingQuickScanByDefault(QuickScanSwitch.IsOn);
        Refresh();
    }

    private void OnTrayResidentToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressToggles) return;
        _vm.SetPendingTrayResident(TrayResidentSwitch.IsOn);
        Refresh();
    }

    private void OnWidgetToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressToggles) return;
        _vm.SetPendingShowDesktopWidget(WidgetSwitch.IsOn);
        Refresh();
    }

    // ── 主题三选 / 挂件内容与大小（原型 plan-row 选项，改动只进待确认副本） ──

    private void OnThemeClick(object sender, RoutedEventArgs e)
    {
        _vm.SetPendingTheme(sender switch
        {
            _ when ReferenceEquals(sender, ThemeShuimo) => ThemeName.Shuimo,
            _ when ReferenceEquals(sender, ThemeTianqing) => ThemeName.Tianqing,
            _ when ReferenceEquals(sender, ThemeMobai) => ThemeName.Mobai,
            _ => _vm.Pending.Theme,
        });
        Refresh();
    }

    private void OnWidgetContentClick(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, ContentMemory)) _vm.TogglePendingWidgetContent(WidgetContent.Memory);
        else if (ReferenceEquals(sender, ContentJunkPending)) _vm.TogglePendingWidgetContent(WidgetContent.JunkPending);
        else if (ReferenceEquals(sender, ContentDiskFree)) _vm.TogglePendingWidgetContent(WidgetContent.DiskFree);
        Refresh();
    }

    private void OnWidgetSizeClick(object sender, RoutedEventArgs e)
    {
        _vm.SetPendingWidgetSize(ReferenceEquals(sender, SizeCompact) ? WidgetSize.Compact : WidgetSize.Standard);
        Refresh();
    }

    // ── 阈值与位置：非法输入防呆（不通过不给记入，红色文案如实说明） ──

    private void OnStageMemoryAlert(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(MemoryAlertInput.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent))
        {
            ShowThresholdError("内存告警线没看懂：请输入 0–100 的整数（0=不提醒），未记入待确认值。");
            return;
        }
        if (!_vm.TrySetPendingMemoryAlertPercent(percent, out var error)) { ShowThresholdError(error); return; }
        MemoryAlertInput.Text = string.Empty;
        Refresh();
    }

    private void OnStageDiskFree(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(DiskFreeInput.Text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var gb))
        {
            ShowThresholdError("磁盘余量提醒没看懂：请输入 0–1024 的数值（GB，0=不提醒），未记入待确认值。");
            return;
        }
        if (!_vm.TrySetPendingDiskFreeAlertGb(gb, out var error)) { ShowThresholdError(error); return; }
        DiskFreeInput.Text = string.Empty;
        Refresh();
    }

    private void OnStageInstallDir(object sender, RoutedEventArgs e)
    {
        if (!_vm.TrySetPendingDefaultInstallDir(InstallDirInput.Text, out var error)) { ShowThresholdError(error); return; }
        InstallDirInput.Text = string.Empty;
        Refresh();
    }

    // ── 确认闸门（页面唯一的落盘动作） ──

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var applied = _vm.ConfirmPendingChanges();
        Refresh();
        EntryHint.Text = applied.Count == 0
            ? "没有改动，未触碰设置文件。"
            : _vm.HasStore
                ? $"已保存 {applied.Count} 项改动：{string.Join("；", applied)}"
                : $"已在本会话生效 {applied.Count} 项（暂无设置文件渠道，未落盘）：{string.Join("；", applied)}";

        // R-P12-9：托盘常驻确认后即时生效；开机自启走通用卡单一开关与注册表差量对齐（2026-11-14 双口径合并），结果如实追加
        if (applied.Count > 0)
        {
            var trayNote = TrayIconController.ApplySettings(_vm.Current.LaunchOnStartup, _vm.Current.TrayResident);
            if (trayNote.Length > 0) EntryHint.Text += $"（托盘：{trayNote}）";
        }
    }

    private void OnDiscardClick(object sender, RoutedEventArgs e)
    {
        _vm.DiscardPendingChanges();
        Refresh();
        EntryHint.Text = "已放弃全部待确认改动，文件零触碰。";
    }

    // ── 其他卡入口（只读导航，零写） ──

    private void OnPopupEntryClick(object sender, RoutedEventArgs e)
    {
        var shell = (App.Current as App)?.Shell;
        if (shell is null)
        {
            EntryHint.Text = "弹窗管理入口随运行期接线，本页未代跳。";
            return;
        }
        shell.SelectRoute(AppRoute.Popup); // 目标页未注册时主窗显示如实占位
    }

    private void Refresh()
    {
        EntryHint.Text = string.Empty;
        Bind(_vm);
    }

    private void ShowThresholdError(string message)
    {
        ThresholdError.Text = message;
        ThresholdError.Visibility = Visibility.Visible;
    }

    private void MarkSelected(Button option, bool selected)
    {
        option.Background = ThemeBrush(selected ? "TaoAzureWashBrush" : "TaoCardBrush");
        option.BorderThickness = new Thickness(1);
        option.BorderBrush = ThemeBrush(selected ? "TaoAzureDeepBrush" : "TaoLineSoftBrush");
        option.Foreground = ThemeBrush("TaoInkBrush");
    }

    private static Microsoft.UI.Xaml.Media.Brush ThemeBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Microsoft.UI.Xaml.Media.Brush brush
            ? brush
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray);
}
