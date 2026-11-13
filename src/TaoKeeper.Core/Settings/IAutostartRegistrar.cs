namespace TaoKeeper.Core.Settings;

/// <summary>
/// 托盘开机自启注册契约（R-P12-9）：注册/注销/查询本应用的开机自启项。
/// 实现侧（Adapters）落 HKCU Run 键（仓为 MSI/WiX 打包形态，见 tools/msi/Product.wxs，非 MSIX）。
/// 所有方法不做静默跳过：失败如实返回 false 并给原因，调用方原样上报，不假成功。
/// </summary>
public interface IAutostartRegistrar
{
    /// <summary>当前是否已注册自启项（查询失败按未注册如实返回，不抛出）。</summary>
    bool IsRegistered();

    /// <summary>注册开机自启项。成功返回 true；失败返回 false 且 error 给出可读原因。</summary>
    bool TryRegister(out string error);

    /// <summary>注销开机自启项（未注册时注销按幂等成功处理）。失败返回 false 且 error 给出可读原因。</summary>
    bool TryUnregister(out string error);
}
