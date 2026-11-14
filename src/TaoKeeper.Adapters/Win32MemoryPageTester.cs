using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using TaoKeeper.Core.Modules.DiskHardcore;

namespace TaoKeeper.Adapters;

/// <summary>
/// 内存页测试适配器（D3）：对本进程页做真实写读校验探针——分配页块、写入校验图案、读回比对。
/// 只动本进程已分配内存页，不改引导、不格式化、不触碰他进程（D3 红线）。
/// 大面积系统级坏页定位（如启动级排除参数）需要内核/诊断通道，真机口径见待真机验证清单。
/// </summary>
public sealed class Win32MemoryPageTester : IMemoryPageTester
{
    private const int PageSize = 4096;
    private const long Pattern = unchecked((long)0xA5A5_5A5A_A5A5_5A5A);

    public BadPageReport TestPages(long startPage, long pageCount)
    {
        if (pageCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(pageCount), "测试范围必须为正");
        if (startPage < 0)
            throw new ArgumentOutOfRangeException(nameof(startPage), "起始页必须为非负");

        var bad = new List<long>();
        var probePages = (int)Math.Min(pageCount, 256); // 单轮探针上限 1MB，防止大块分配卡 UI
        var blockBytes = probePages * PageSize;
        var buffer = Marshal.AllocHGlobal(blockBytes);
        try
        {
            var longsPerPage = PageSize / sizeof(long);
            // 写阶段：整块写入校验图案
            for (var i = 0; i < probePages * longsPerPage; i++)
                Marshal.WriteInt64(buffer, i * sizeof(long), Pattern);
            // 读阶段：逐页比对，页内任一长字不符=该页坏
            for (var p = 0; p < probePages; p++)
            {
                var pageBad = false;
                for (var i = 0; i < longsPerPage; i++)
                {
                    if (Marshal.ReadInt64(buffer, (p * longsPerPage + i) * sizeof(long)) != Pattern)
                    {
                        pageBad = true;
                        break;
                    }
                }
                if (pageBad) bad.Add(startPage + p);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return new BadPageReport(
            PagesChecked: probePages,
            BadPageNumbers: bad,
            ExecutableSuggestion: bad.Count > 0
                ? "检出校验失败页：本结果仅覆盖本进程探针页，建议真机重启后运行 Windows 内存诊断做启动级全检（诊断报告口径 D3）"
                : $"本进程 {probePages} 页写读校验全过；启动级全检建议真机运行 Windows 内存诊断（D3 真机口径）");
    }
}
