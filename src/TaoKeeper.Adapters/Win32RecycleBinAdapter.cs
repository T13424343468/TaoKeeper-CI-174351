using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TaoKeeper.Core.Modules;

namespace TaoKeeper.Adapters;

/// <summary>
/// Win32 回收站适配器（IFileRemovalAdapter 的 Windows 真机实现）：
/// SHFileOperation 带 FOF_ALLOWUNDO 把文件/空目录移入系统回收站（可还原，不真删）。
/// 回收站落点由系统管理，适配层拿不到内部新路径，BinPath 以「回收站」语义如实返回，
/// 还原走系统回收站原生入口（桌面回收站）。非 Windows 报红不静默跳过（与 winget 适配器同一规则）。
/// </summary>
public sealed class Win32RecycleBinAdapter : IFileRemovalAdapter
{
    private const uint FoDelete = 0x0003;
    private const uint FofAllowUndo = 0x0040;
    private const uint FofNoConfirmation = 0x0010;
    private const uint FofSilent = 0x0004;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr Hwnd;
        public uint Func;
        public string From;
        public string? To;
        public ushort Flags;
        public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHFileOperation(ref ShFileOpStruct op);

    /// <summary>把文件或空目录移入系统回收站；成功返回「回收站」语义落点，失败返回 null。</summary>
    public Task<string?> MoveToRecycleBinAsync(string fullPath, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("回收站移动仅在 Windows 真机可用。");
            if (string.IsNullOrWhiteSpace(fullPath) || !fullPath.Contains('\\'))
                return null; // 相对路径/畸形输入不送回收站，交编排层按失败记账

            // pFrom 必须双空字符结尾（SHFileOperation 契约）
            var op = new ShFileOpStruct
            {
                Func = FoDelete,
                From = fullPath + '\0' + '\0',
                Flags = (ushort)(FofAllowUndo | FofNoConfirmation | FofSilent),
            };
            var result = SHFileOperation(ref op);
            // 0=成功；被用户中止算失败（返回 null，交编排层如实记账，不假装成功）
            return result == 0 && !op.AnyOperationsAborted ? "回收站（系统原生，可从桌面回收站还原）" : null;
        }, cancellation);
    }
}
