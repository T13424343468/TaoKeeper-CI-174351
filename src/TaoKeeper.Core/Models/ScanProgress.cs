using System;

namespace TaoKeeper.Core.Models;

/// <summary>扫描过程中的瞬时状态，仅用于展示，不参与最终结果。</summary>
public sealed record ScanProgress(
    long FilesSeen,
    long BytesSeen,
    string CurrentDirectory,
    ScanPhase Phase);

/// <summary>扫描阶段。</summary>
public enum ScanPhase
{
    Idle,
    Walking,
    Hashing,
    DuplicateGrouping,
    Completed,
    Cancelled,
    Failed
}
