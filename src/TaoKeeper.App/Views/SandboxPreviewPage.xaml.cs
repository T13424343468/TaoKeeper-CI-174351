using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaoKeeper.AppKernel.ViewModels;
using TaoKeeper.Core.Modules.SandboxPreview;
using TaoKeeper.Core.Modules.SnapshotChain;

namespace TaoKeeper.App.Views;

/// <summary>
/// P13 先试后行（沙箱预演）code-behind：引擎由组合根接线（真实文件系统适配器 + 快照链）。
/// 越界记录逐条展开上报（R-P13-4），零越界如实 0，不造记录；未预演态零预填（V-A10）。
/// </summary>
public sealed partial class SandboxPreviewPage : Page
{
    private SandboxPreviewEngine? _engine;
    private readonly SandboxPreviewPageViewModel _vm = new();

    /// <summary>引擎提供者（组合根接线；未接线时页面如实提示不可用，不假数据）。</summary>
    public static Func<SandboxPreviewEngine>? EngineProvider { get; set; }

    public SandboxPreviewPage()
    {
        InitializeComponent();
        _vm.EngineProvider = () => _engine;
    }

    private void OnBegin(object sender, RoutedEventArgs e)
    {
        if (EngineProvider is null)
        {
            ResultLine.Text = "预演引擎未就绪（等待运行期接线），不假数据。";
            return;
        }

        _engine = EngineProvider();
        _engine.Begin();
        _vm.BeginPreview();
        _vm.SyncInterceptions(_engine);
        RenderInterceptions("预演进行中：本次预演的写入暂不落盘。");
    }

    private async void OnCommit(object sender, RoutedEventArgs e)
    {
        if (_engine is null)
        {
            ResultLine.Text = "尚未开始预演。";
            return;
        }

        RunRing.Visibility = Visibility.Visible;
        try
        {
            await _vm.CommitAsync(_engine, "沙箱预演留下").ConfigureAwait(true);
            RenderInterceptions(_vm.LastResultText ?? string.Empty);
        }
        finally
        {
            RunRing.Visibility = Visibility.Collapsed;
        }
    }

    private void OnDiscard(object sender, RoutedEventArgs e)
    {
        if (_engine is null)
        {
            ResultLine.Text = "尚未开始预演。";
            return;
        }

        _vm.Discard(_engine);
        RenderInterceptions(_vm.LastResultText ?? string.Empty);
    }

    private async void OnFreeze(object sender, RoutedEventArgs e)
    {
        if (_engine is null)
        {
            ResultLine.Text = "尚未开始预演。";
            return;
        }

        RunRing.Visibility = Visibility.Visible;
        try
        {
            await _vm.FreezeAsync(_engine, "沙箱预演冻结").ConfigureAwait(true);
            RenderInterceptions(_vm.LastResultText ?? string.Empty);
        }
        finally
        {
            RunRing.Visibility = Visibility.Collapsed;
        }
    }

    private void RenderInterceptions(string result)
    {
        ViolationHeader.Text = _vm.ViolationCount == 0
            ? "越界拦截 0 次（如实显示，宁误拦不写穿）"
            : $"越界拦截 {_vm.ViolationCount} 次（逐条展开：时间 + 目标路径 + 拦截原因）";
        EmptyLine.Visibility = _vm.ViolationCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        InterceptionList.Items.Clear();
        foreach (var row in _vm.Interceptions)
        {
            InterceptionList.Items.Add(new TextBlock
            {
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Text = $"{row.TimeLocal}｜{row.TargetPath}｜{row.Reason}",
            });
        }
        ResultLine.Text = result;
    }
}
