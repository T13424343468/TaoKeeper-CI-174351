using System;
using System.Collections.Generic;

namespace TaoKeeper.Core.Models;

/// <summary>启动项来源：注册表 Run 键、启动文件夹、计划任务等。</summary>
public enum StartupItemSource { RegistryRun, StartupFolder, ScheduledTask, Service }

/// <summary>启动项风险等级：低=系统/已知厂商，中=未确认发布者，高=常见流氓推广位。</summary>
public enum StartupRiskLevel { Low, Medium, High }

/// <summary>一条开机启动项（只读条目；启用/禁用动作由适配层执行并回填结果）。</summary>
public sealed record StartupItemEntry(
    string Id,
    string Name,
    StartupItemSource Source,
    string? Publisher,
    bool Enabled,
    StartupRiskLevel Risk,
    /// <summary>风险提示文案：为高风险/中风险项准备的一句话解释，可为空。</summary>
    string? RiskHint = null,
    /// <summary>命令行或指向目标的路径，供用户核对来源。</summary>
    string? Command = null,
    /// <summary>归属产品标识：本产品（TaoKeeper）自带的常驻组件（如清理挂件自启）标注于此，
    /// 体检与加速建议中单独呈现、不与第三方混算；用户仍可自行禁用。</summary>
    string? OwningProductId = null);

/// <summary>一个运行中进程的资源快照（只读；签名核验由适配层完成后回填）。</summary>
public sealed record ProcessEntry(
    int ProcessId,
    string ProcessName,
    string? Publisher,
    /// <summary>CPU 占用率 0–100，单次采样口径。</summary>
    double CpuPercent,
    long WorkingSetBytes,
    /// <summary>签名状态占位：null=未核验，适配层核验后填充 true/false。</summary>
    bool? IsSigned = null);

/// <summary>启动项启用/禁用动作的执行结果（由适配层回填，逻辑层只构造与判定展示文案）。</summary>
public sealed record StartupToggleResult(
    string ItemId,
    /// <summary>true=请求启用，false=请求禁用。</summary>
    bool RequestedEnabled,
    bool Success,
    /// <summary>失败原因或成功说明；不泄露系统内部异常细节，供界面直接展示。</summary>
    string? Detail = null)
{
    /// <summary>给界面的动作回执文案。</summary>
    public string DisplayText => Success
        ? (RequestedEnabled ? "已启用，下次开机生效" : "已禁用，下次开机生效")
        : (Detail ?? "操作未完成，请重试");
}

/// <summary>进程/启动项扫描汇总，供性能加速页与体检上下文消费。</summary>
public sealed record StartupProcessSnapshot(
    DateTimeOffset CapturedUtc,
    IReadOnlyList<StartupItemEntry> StartupItems,
    IReadOnlyList<ProcessEntry> Processes)
{
    /// <summary>禁用中的启动项数量（体检规则消费）。</summary>
    public int DisabledCount
    {
        get
        {
            var count = 0;
            foreach (var item in StartupItems)
                if (!item.Enabled) count++;
            return count;
        }
    }
}
