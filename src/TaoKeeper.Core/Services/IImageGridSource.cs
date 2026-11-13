using System;
using System.Threading;
using System.Threading.Tasks;

namespace TaoKeeper.Core.Services;

/// <summary>8×8 归一化灰度格（相似照片感知哈希的输入，解码方负责缩放）。</summary>
public sealed record ImageGrayGrid(int Width, int Height, byte[] Gray);

/// <summary>
/// 图片灰度格来源（相似照片识别的解码契约）：把一张图片解成原始分辨率 + 8×8 灰度格。
/// 只读契约：实现不得移动、修改或删除任何文件。
/// 图片不可解（损坏/不支持格式）返回 null，由调用方如实计跳过，不造假数据。
/// </summary>
public interface IImageGridSource
{
    /// <summary>解出灰度格；文件不可读或格式不支持时返回 null。</summary>
    Task<ImageGrayGrid?> DecodeAsync(string fullPath, CancellationToken cancellation = default);
}
