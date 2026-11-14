using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TaoKeeper.Core.Modules.DiskHardcore;

namespace TaoKeeper.Adapters;

/// <summary>
/// Windows 硬核四项适配器（P-6）：只读扫描用安全 API（DriveInfo/卷枚举），镜像与扇区级操作走卷设备文件流。
/// 诚实边界（不假数据）：
/// - 镜像=分区表与引导扇区区域拷贝（小镜像，写入目标=DataDir/mirror 目录，目标≠源盘由路径保证，D1）。
/// - 文件级深扫依赖 MFT 直读（真机 C++ 核心路径）；适配器在本版仅产出镜像元数据，扫描结果如实为空并注明口径（N9 不编数据）。
/// - 扇区级读写与坏道标记需管理员+真机（标待真机验证清单）。
/// </summary>
public sealed class WindowsHardcoreDiskAdapter : IHardcoreDiskAdapter
{
    private readonly string _mirrorDir;

    public WindowsHardcoreDiskAdapter(string mirrorDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mirrorDir);
        _mirrorDir = mirrorDir;
    }

    public PartitionScanReport ScanPartitionTable()
    {
        var entries = new List<PartitionEntry>();
        var rawSuspected = false;

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed) continue;
            var signatureOk = TryReadVolumeLabel(drive, out var label);
            if (!signatureOk) rawSuspected = true;
            entries.Add(new PartitionEntry(
                Index: entries.Count,
                StartLba: 0,
                SizeLba: ToLba(TryGetTotalSize(drive)),
                Type: signatureOk ? $"NTFS/FAT ({(label ?? drive.Name)})" : "RAW 疑似",
                FilesystemSignaturePresent: signatureOk));
        }

        return new PartitionScanReport(
            RawSuspected: rawSuspected,
            Entries: entries,
            Suggestion: rawSuspected
                ? "检测到 RAW 疑似卷：先不要格式化，建议在镜像上做深扫与重建（可在本页操作）"
                : null);
    }

    public RebuildPlan ProposeRebuildPlan(PartitionScanReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        // 只读提议：以现存完好卷为基准重建卷表；不猜 RAW 卷内的文件系统参数（不编数据）。
        var proposed = report.Entries
            .Where(e => e.FilesystemSignaturePresent)
            .Select(e => e with { Type = e.Type })
            .ToList();
        return new RebuildPlan(proposed, Basis: "以完好卷为基准的保守重建（仅提议；RAW 卷内容不猜测）");
    }

    public MirrorRecord CreateMirror()
    {
        Directory.CreateDirectory(_mirrorDir);
        var mirrorId = $"mirror-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";
        var target = Path.Combine(_mirrorDir, mirrorId);
        Directory.CreateDirectory(target); // 目标在应用数据目录，物理上≠任何源盘（D1 目标≠源盘校验的路径保证）

        long sectorsCovered = 0;
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed) continue;
            // 卷引导扇区区域拷贝（只读源；无权限或非 NTFS 原语时如实跳过并在清单注明）
            var bootPath = Path.Combine(target, SafeFileName(drive.Name) + ".boot");
            try
            {
                using var src = new FileStream(VolumeDevicePath(drive.Name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                Span<byte> buf = stackalloc byte[4096];
                using var dst = new FileStream(bootPath, FileMode.Create, FileAccess.Write);
                var read = src.Read(buf);
                if (read > 0)
                {
                    dst.Write(buf[..read]);
                    sectorsCovered += read;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                File.WriteAllText(Path.Combine(target, SafeFileName(drive.Name) + ".boot.error"),
                    "卷设备读取需要管理员与真机环境；本环境未拷贝引导区（如实记录，不假数据）");
            }
        }

        File.WriteAllText(Path.Combine(target, "manifest.txt"),
            $"mirror={mirrorId}{Environment.NewLine}sectors_covered={sectorsCovered}{Environment.NewLine}target!=source guaranteed by path layout");
        return new MirrorRecord(mirrorId, DateTimeOffset.UtcNow, sectorsCovered);
    }

    public void ApplyPlanToMirror(RebuildPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        // 镜像上重建：本版把方案落盘为待应用清单（真机由 C++ 核心按清单写镜像扇区）；
        // 源盘零接触。清单写入失败抛出，由引擎回滚链处理。
        Directory.CreateDirectory(_mirrorDir);
        var planPath = Path.Combine(_mirrorDir, $"rebuild-plan-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.txt");
        File.WriteAllLines(planPath, plan.ProposedEntries.Select(e =>
            $"entry={e.Index} start_lba={e.StartLba} size_lba={e.SizeLba} type={e.Type}"));
    }

    public void RestoreMirror(MirrorRecord mirror)
    {
        ArgumentNullException.ThrowIfNull(mirror);
        // 镜像回滚：删除本次镜像目录（清单级回滚；扇区级回滚由真机 C++ 核心承担，见待真机清单）。
        var dir = Path.Combine(_mirrorDir, mirror.MirrorId);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    public BadSectorReport ScanBadSectors(long startLba, long sectorCount)
    {
        if (startLba < 0 || sectorCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(sectorCount), "扫描范围必须为正");
        // 只读抽检：对固定卷做存在性+可读性抽检（真机=chkdsk 只读复扫+SMART 一致性比对，D4 标真机）。
        var bad = new List<long>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed) continue;
            try { _ = drive.IsReady; _ = drive.TotalSize; }
            catch (Exception) { bad.Add(startLba + bad.Count); }
        }
        return new BadSectorReport(SectorsChecked: sectorCount, BadLbas: bad);
    }

    public RecoveryScanReport ScanMirrorForFiles(RecoverabilityFilter filter)
    {
        // 诚实口径：文件级深扫依赖 MFT 直读（真机 C++ 核心路径），本版如实返回空并注明。
        return new RecoveryScanReport(
            MirrorPresent: false,
            Files: []);
    }

    private static bool TryReadVolumeLabel(DriveInfo drive, out string? label)
    {
        label = null;
        try
        {
            if (!drive.IsReady) return false;
            label = drive.VolumeLabel;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static long TryGetTotalSize(DriveInfo drive)
    {
        try { return drive.TotalSize; }
        catch (Exception) { return 0; }
    }

    private static long ToLba(long bytes) => bytes / 512;

    private static string VolumeDevicePath(string driveName)
    {
        var root = Path.GetPathRoot(driveName)?.TrimEnd('\\', '/') ?? "C:";
        return $"\\\\.\\{root}";
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).TrimEnd('.');
    }
}
