using System;
using System.Collections.Generic;
using System.Linq;

namespace TaoKeeper.Core.Modules;

/// <summary>一份内存快照：物理内存与虚拟内存（页面文件）的某一时刻状态。</summary>
public sealed record MemorySnapshot(
    DateTimeOffset CapturedUtc,
    long TotalPhysicalBytes,
    long AvailablePhysicalBytes,
    /// <summary>页面文件总大小；null 表示系统托管或未配置，由适配层填充。</summary>
    long? PageFileTotalBytes,
    /// <summary>页面文件已用量；与 Total 同为 null 时表示托管态。</summary>
    long? PageFileUsedBytes)
{
    /// <summary>物理内存占用率 0–100。</summary>
    public double PhysicalUsedPercent => TotalPhysicalBytes <= 0
        ? 0
        : Math.Round((TotalPhysicalBytes - AvailablePhysicalBytes) * 100.0 / TotalPhysicalBytes, 1);

    /// <summary>页面文件占用率；托管态返回 null（本轮未测）。</summary>
    public double? PageFileUsedPercent => PageFileTotalBytes is { } total && total > 0 && PageFileUsedBytes is { } used
        ? Math.Round(used * 100.0 / total, 1)
        : null;
}

/// <summary>内存占用排行中的一条进程记录（只读快照，不含句柄等真实资源）。</summary>
public sealed record MemoryTopEntry(
    int ProcessId,
    string ProcessName,
    long WorkingSetBytes,
    /// <summary>签名状态占位：null=未知，由适配层核验后填充。</summary>
    bool? IsSigned = null);

/// <summary>
/// 趋势采样序列：环形有界，超限时按步长降采样保留最早与最新样本。
/// 线程安全性：仅要求单线程调用（监控循环串行采集）。
/// </summary>
public sealed class MemoryTrendSeries
{
    private readonly int _capacity;
    private readonly List<MemorySnapshot> _samples = new();
    private long _appended;

    public MemoryTrendSeries(int capacity = 600)
    {
        if (capacity < 2) throw new ArgumentOutOfRangeException(nameof(capacity), "容量至少为 2");
        _capacity = capacity;
    }

    public int Count => _samples.Count;
    public IReadOnlyList<MemorySnapshot> Samples => _samples;
    public MemorySnapshot? Latest => _samples.Count == 0 ? null : _samples[^1];

    /// <summary>追加一份快照；容量满后每两次追加抽掉一个旧样本（保留首尾），整体趋势不变。</summary>
    public void Append(MemorySnapshot snapshot)
    {
        _appended++;
        if (_samples.Count >= _capacity)
        {
            // 丢弃偶数序号旧样本，保留趋势的同时腾出一个空位。
            _samples.RemoveAt(1);
        }
        _samples.Add(snapshot);
    }

    /// <summary>按给定间隔聚合降采样，返回用于绘图的新序列（不改变自身）。</summary>
    public IReadOnlyList<MemorySnapshot> Downsample(int every)
    {
        if (every <= 1 || _samples.Count <= every) return _samples.ToList();
        var result = new List<MemorySnapshot>();
        for (int i = 0; i < _samples.Count; i += every)
            result.Add(_samples[i]);
        if (result.Count == 0 || result[^1] != _samples[^1]) result.Add(_samples[^1]);
        return result;
    }

    /// <summary>累计追加次数（用于测试降采样行为）。</summary>
    public long TotalAppended => _appended;
}

/// <summary>页面文件预设方案。口径与设计文档 v0.5（M9 内存管理）一致。</summary>
public enum PageFilePreset
{
    /// <summary>智能推荐：按物理内存给合理默认值。</summary>
    SmartRecommendation,
    /// <summary>性能优先：较大页面文件，减少换页。</summary>
    PerformanceFirst,
    /// <summary>空间优先：较小页面文件，省空间但大程序多时可能不够。</summary>
    SpaceFirst,
    /// <summary>系统托管：交回 Windows 默认管理。</summary>
    SystemManaged,
    /// <summary>自定义：用户指定盘位与初始/最大值。</summary>
    Custom
}

/// <summary>用户对页面文件的目标设置：预设方案或自定义参数。</summary>
public sealed record PageFileSettings(
    PageFilePreset Preset,
    /// <summary>自定义盘位，如 "C"；系统托管/预设时可为 null 由适配层补默认。</summary>
    string? DriveLetter,
    /// <summary>自定义初始大小（MB）。</summary>
    int? InitialSizeMb,
    /// <summary>自定义最大大小（MB）。</summary>
    int? MaximumSizeMb,
    /// <summary>是否坚持完全关闭页面文件；危险操作，必须显式确认。</summary>
    bool FullyDisabled = false)
{
    /// <summary>预设方案给出的建议参数（初始/最大 MB）；非自定义方案返回 null 由适配层计算。</summary>
    public (int InitialMb, int MaximumMb)? SuggestedValues(long physicalMemoryMb) => Preset switch
    {
        PageFilePreset.SmartRecommendation => (ClampMb(physicalMemoryMb), ClampMb(physicalMemoryMb) * 2),
        PageFilePreset.PerformanceFirst => (ClampMb(physicalMemoryMb) * 2, ClampMb(physicalMemoryMb) * 4),
        PageFilePreset.SpaceFirst => (2048, 4096),
        PageFilePreset.SystemManaged => null,
        _ => null
    };

    private static int ClampMb(long physicalMemoryMb)
    {
        var mb = Math.Max(8192, physicalMemoryMb); // 下限 8 GB，小内存机器也给出可用值
        return (int)Math.Min(mb, 65536);           // 上限 64 GB，避免失真
    }
}

/// <summary>校验结果：错误阻止应用，警告提示但允许继续（对应对话页的黄色提示）。</summary>
public sealed record PageFileValidation(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// 页面文件参数校验器（M9 内存管理子功能，可移植逻辑层）。
/// 只做参数合理性判定，不触碰真实系统设置；应用动作永远属于用户确认后的适配层。
/// </summary>
public static class PageFileSettingsValidator
{
    /// <summary>单值合理区间下限：256 MB（微软推荐的最小可用页面文件量级）。</summary>
    public const int MinSizeMb = 256;
    /// <summary>单值合理区间上限：4 倍物理内存且不超过 128 GB。</summary>
    public const long MaxSizeMbCap = 131072;

    public static PageFileValidation Validate(PageFileSettings settings, long physicalMemoryBytes)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (settings.Preset == PageFilePreset.SystemManaged)
        {
            if (settings.FullyDisabled)
                errors.Add("系统托管与完全关闭不能同时选择");
            return new PageFileValidation(errors, warnings);
        }

        if (settings.FullyDisabled)
        {
            // 完全关闭属于危险操作：合法但必须由用户在界面上显式二次确认后携带该标记。
            if (settings.InitialSizeMb is not null || settings.MaximumSizeMb is not null)
                errors.Add("完全关闭页面文件时不允许同时填写初始或最大值");
            warnings.Add("完全关闭页面文件后，部分大型程序可能无法启动，且崩溃时无法生成转储");
            return new PageFileValidation(errors, warnings);
        }

        var physicalMb = Math.Max(1, physicalMemoryBytes / 1024 / 1024);

        if (settings.Preset == PageFilePreset.Custom)
        {
            ValidateCustom(settings, physicalMb, errors, warnings);
        }
        else
        {
            // 预设方案：参数由预设给出，仅校验盘位提示。
            if (string.IsNullOrWhiteSpace(settings.DriveLetter))
                warnings.Add("未指定盘位，将使用系统盘（C:）");
        }

        return new PageFileValidation(errors, warnings);
    }

    private static void ValidateCustom(PageFileSettings s, long physicalMb, List<string> errors, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(s.DriveLetter))
        {
            errors.Add("自定义模式必须指定盘位");
        }
        else if (s.DriveLetter.Length != 1 || !char.IsAsciiLetter(s.DriveLetter[0]))
        {
            errors.Add("盘位格式不正确，应为单个盘符字母（如 C）");
        }

        if (s.InitialSizeMb is not { } initial)
        {
            errors.Add("自定义模式必须填写初始大小");
            initial = MinSizeMb;
        }

        if (s.MaximumSizeMb is not { } maximum)
        {
            errors.Add("自定义模式必须填写最大大小");
            maximum = initial;
        }

        if (initial < MinSizeMb)
            errors.Add($"初始大小不得小于 {MinSizeMb} MB");

        if (maximum < initial)
            errors.Add("最大大小不得小于初始大小");

        var capMb = Math.Min(physicalMb * 4, MaxSizeMbCap);
        if (maximum > capMb)
            errors.Add($"最大大小超出合理区间（上限 {capMb} MB，约为物理内存的 4 倍）");

        if (maximum < physicalMb)
            warnings.Add("最大大小小于物理内存，大程序较多时可能提示虚拟内存不足");
    }
}
