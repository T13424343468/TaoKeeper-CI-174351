using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaoKeeper.AppKernel.ViewModels;
using TaoKeeper.Core.Modules;

namespace TaoKeeper.App.Views;

/// <summary>存储感知范围行展示态（勾选经 VM ToggleScope 落账）。</summary>
public sealed class StorageScopeRow
{
    public StorageCleanupScope Flag { get; }
    public string Name { get; }
    public string Meta { get; }
    public bool IsOn { get; set; }
    public bool CanToggle { get; set; } = true;

    public StorageScopeRow(StorageCleanupScope flag, string name, string meta, bool isOn) =>
        (Flag, Name, Meta, IsOn) = (flag, name, meta, isOn);
}

/// <summary>
/// 存储感知页 code-behind：Bind(vm) 注入计划；总开关=Enable/Disable（意图转述，适配层执行）；
/// 逐项勾选=ToggleScope（开启态清空最后一项的引擎报红如实展示，不静默吞）；
/// 未开/未排期一律如实显示。本页零写动作。
/// </summary>
public sealed partial class StoragePage : Page
{
    private StorageSensePageViewModel _vm = new();
    private List<StorageScopeRow> _rows = new();

    /// <summary>范围行的如实说明（UI 层文案；名称与引擎 ScopeCatalog 同源）。</summary>
    private static readonly Dictionary<StorageCleanupScope, string> ScopeMeta = new()
    {
        [StorageCleanupScope.TempFiles] = "临时目录中超过保留期的文件，到点并入确认预览",
        [StorageCleanupScope.RecycleBinOld] = "回收站中存放较久的已删项，到点并入确认预览",
        [StorageCleanupScope.DownloadsAged] = "下载目录中长期未访问的文件，到点并入确认预览",
        [StorageCleanupScope.BrowserCache] = "浏览器缓存文件，清理后首次打开网页稍慢",
    };

    public StoragePage()
    {
        InitializeComponent();
        Bind(_vm);
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is StorageSensePageViewModel vm) Bind(vm);
    }

    /// <summary>注入存储感知计划（默认关，如实显示）。</summary>
    public void Bind(StorageSensePageViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        _vm = vm;

        _suppressToggle = true;
        MasterSwitch.IsOn = vm.Plan.Enabled;
        _suppressToggle = false;

        RefreshRows();
        RefreshStatus();
    }

    private bool _suppressToggle;

    private void RefreshRows()
    {
        _rows = StorageSensePageViewModel.ScopeCatalog
            .Select(s => new StorageScopeRow(
                s.Flag, s.Name, ScopeMeta[s.Flag],
                (_vm.Plan.Scope & s.Flag) != 0))
            .ToList();
        foreach (var r in _rows) r.CanToggle = _vm.Plan.Enabled; // 关闭态范围不可勾（先开总开关）
        ScopeList.ItemsSource = _rows;
    }

    private void RefreshStatus()
    {
        StatusText.Text = _vm.GetStatusText(DateTimeOffset.Now);
    }

    private void OnMasterToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        ScopeError.Visibility = Visibility.Collapsed;
        try
        {
            if (MasterSwitch.IsOn)
                _vm.Enable(runOnStartupIfOverdue: false, DateTimeOffset.Now);
            else
                _vm.Disable();
        }
        catch (InvalidOperationException ex)
        {
            // 引擎报红如实展示（如范围未勾选就开开关）
            ScopeError.Text = ex.Message;
            ScopeError.Visibility = Visibility.Visible;
            _suppressToggle = true;
            MasterSwitch.IsOn = _vm.Plan.Enabled;
            _suppressToggle = false;
        }
        RefreshRows();
        RefreshStatus();
    }

    private void OnScopeToggle(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: StorageCleanupScope flag }) return;
        ScopeError.Visibility = Visibility.Collapsed;
        try
        {
            _vm.ToggleScope(flag, sender is CheckBox { IsChecked: true });
        }
        catch (InvalidOperationException ex)
        {
            // 「至少保留一项」报红如实展示，勾选态回滚到 VM 实况
            ScopeError.Text = ex.Message;
            ScopeError.Visibility = Visibility.Visible;
        }
        RefreshRows();
        RefreshStatus();
    }
}
