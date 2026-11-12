using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
/// 存储感知页 code-behind：Bind(vm) 注入计划；总开关=Enable/Disable（开启需确认弹窗，P9-1/B4）；
/// 逐项勾选=ToggleScope（开启态清空最后一项的引擎报红如实展示，不静默吞）；
/// 「立即检查一次」完成 感知（真实候选）→提示（确认预览）→确认→执行（回收站语义） 闭环（P9-2）。
/// 执行走与主程序同一 CleanupCoordinator 链，方案变化经 PlanChanged 钩子落盘。
/// </summary>
public sealed partial class StoragePage : Page
{
    private StorageSensePageViewModel _vm = new();
    private List<StorageScopeRow> _rows = new();

    /// <summary>真实候选扫描（宿主接线；返回未确认候选清单，未就绪为空清单）。</summary>
    public static Func<IReadOnlyList<CleanupActionItem>>? StorageCandidatesProvider { get; set; }

    /// <summary>清理执行（宿主接线；与磁盘清理同一 CleanupCoordinator，回收站语义）。</summary>
    public static Func<IReadOnlyList<CleanupActionItem>, Task<CleanupBatchResult>>? CleanupExecutorProvider { get; set; }

    /// <summary>最近一次到点待执行的候选（确认后逐条置 Confirmed 提交；页面生命周期内有效）。</summary>
    private IReadOnlyList<CleanupActionItem>? _pendingItems;

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
        if (MasterSwitch.IsOn)
        {
            // P9-1：开启需确认弹窗（先回弹开关，用户确认后才真正 Enable 并落盘）
            _suppressToggle = true;
            MasterSwitch.IsOn = _vm.Plan.Enabled;
            _suppressToggle = false;
            _ = ConfirmEnableAsync();
            return;
        }
        _vm.Disable();
        RefreshRows();
        RefreshStatus();
    }

    /// <summary>开启确认弹窗：明示节奏与回收站语义，确认后才落账开启。</summary>
    private async System.Threading.Tasks.Task ConfirmEnableAsync()
    {
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "开启存储感知？",
                Content = $"开启后每 {_vm.Plan.IntervalDays} 天到点提示一次确认预览：先确认、先进回收站、可还原；不会静默清理任何文件。",
                PrimaryButtonText = "确认开启",
                CloseButtonText = "先不开",
                DefaultButton = ContentDialogButton.Primary,
            };
            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;
            if (_vm.Plan.Scope == StorageCleanupScope.None)
            {
                // 默认先纳入临时文件，保证可开启；其余范围仍由用户逐项增减
                _vm.ToggleScope(StorageCleanupScope.TempFiles, on: true);
            }
            _vm.Enable(runOnStartupIfOverdue: false, DateTimeOffset.Now);
        }
        catch (InvalidOperationException ex)
        {
            ScopeError.Text = ex.Message;
            ScopeError.Visibility = Visibility.Visible;
        }
        RefreshRows();
        RefreshStatus();
    }

    /// <summary>感知→提示：真实候选扫描 + 到点判定；到点出确认预览，未到点如实说明不动任何文件。</summary>
    private void OnCheckNowClick(object sender, RoutedEventArgs e)
    {
        PendingActions.Visibility = Visibility.Collapsed;
        _pendingItems = null;
        if (!_vm.Plan.Enabled)
        {
            CheckResult.Text = "存储感知当前是关闭状态（默认关）。请先开启总开关，再检查到点情况。";
            return;
        }

        var candidates = StorageCandidatesProvider?.Invoke() ?? Array.Empty<CleanupActionItem>();
        var bytes = candidates.Sum(c => c.SizeBytes);
        if (!_vm.EvaluateDue(DateTimeOffset.Now, candidates.Count, bytes))
        {
            CheckResult.Text = _vm.Plan.NextRunUtc(DateTimeOffset.Now) is { } next
                ? $"未到计划时间（下次 {next.ToLocalTime():yyyy-MM-dd HH:mm}）。本次未扫描出待清项，未动任何文件。"
                : "已开启但尚未排期（首轮锚点未到）。本次未动任何文件。";
            return;
        }

        var preview = _vm.PendingPreview;
        if (preview is null || candidates.Count == 0)
        {
            CheckResult.Text = "已到点，但本轮没有可用候选（先在磁盘清理页完成一次扫描）；未执行任何清理。";
            return;
        }

        // 提示：确认预览（清单+总量+回收站语义），等用户确认
        _pendingItems = candidates;
        CheckResult.Text = preview.ConfirmText;
        PendingActions.Visibility = Visibility.Visible;
    }

    /// <summary>确认→执行：逐条置确认态后走与主程序同一清理链；完成后回填方案锚点并落盘。</summary>
    private async void OnConfirmRunClick(object sender, RoutedEventArgs e)
    {
        ConfirmRunButton.IsEnabled = false;
        try
        {
            var items = (_pendingItems ?? Array.Empty<CleanupActionItem>())
                .Select(i => i with { Confirmed = true })
                .ToList();
            if (items.Count == 0 || CleanupExecutorProvider is null)
            {
                CheckResult.Text = "没有可执行项或执行通道未接线；未动任何文件。";
                return;
            }
            var batch = await CleanupExecutorProvider(items);
            _vm.MarkRunCompleted(DateTimeOffset.Now); // 锚点后移，防重复提示
            CheckResult.Text = $"执行完成：清理 {batch.SucceededCount} 项（约 {batch.SucceededBytes / 1024.0 / 1024:F1} MB），"
                + $"全部进回收站可还原；未成 {batch.FailedCount} 项、拒绝 {batch.RefusedCount} 项（受保护目录不碰）。";
        }
        catch (Exception ex)
        {
            CheckResult.Text = $"执行未完成：{ex.Message}（目标文件未动，可重试）";
        }
        finally
        {
            ConfirmRunButton.IsEnabled = true;
            PendingActions.Visibility = Visibility.Collapsed;
            _pendingItems = null;
            RefreshStatus();
        }
    }

    /// <summary>本轮先不清：解除待确认态，下次到点再次提示（不动任何文件）。</summary>
    private void OnSkipRunClick(object sender, RoutedEventArgs e)
    {
        _vm.DismissPendingPreview();
        PendingActions.Visibility = Visibility.Collapsed;
        _pendingItems = null;
        CheckResult.Text = "本轮已跳过，未动任何文件；到下次计划时间会再次提示。";
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
