using System.Runtime.InteropServices;

namespace RenamePro.Interop;

/// <summary>
/// Shell 辅助：<see cref="IShellItem"/> 创建（path 直传，兼容中文与空格，不做 URI 转换），
/// 以及开始菜单快捷方式的 AppUserModelID 写入（Windows Toast 的必要条件）。
/// </summary>
internal static class ShellItemHelper
{
    /// <summary>IShellItem 的 IID。</summary>
    private static readonly Guid ShellItemIid = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    /// <summary>IPropertyStore 的 IID。</summary>
    private static readonly Guid PropertyStoreIid = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");

    /// <summary>PKEY_AppUserModel_ID（{9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}, 5）。</summary>
    private static readonly PropertyKey AppUserModelIdKey = new()
    {
        FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        PropertyId = 5
    };

    /// <summary>VT_LPWSTR。</summary>
    private const ushort VariantTypeLpwStr = 31;

    /// <summary>GPS_READWRITE。</summary>
    private const uint GpsReadWrite = 0x2;

    /// <summary>
    /// 由路径创建 IShellItem（使用 path 重载，避免 URI 转换；中文/空格路径安全）。
    /// </summary>
    /// <param name="path">文件或目录完整路径（必须已存在）</param>
    /// <returns>IShellItem（调用方负责 Marshal.ReleaseComObject）</returns>
    public static IShellItem CreateItem(string path)
    {
        var iid = ShellItemIid;
        SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out IShellItem item);
        return item;
    }

    /// <summary>
    /// 给已存在的快捷方式写入 AppUserModelID（无 AUMID 的 Toast 会被系统静默丢弃）。
    /// </summary>
    /// <param name="shortcutPath">.lnk 快捷方式完整路径</param>
    /// <param name="appUserModelId">应用用户模型 ID</param>
    public static void SetShortcutAppUserModelId(string shortcutPath, string appUserModelId)
    {
        var iid = PropertyStoreIid;
        SHGetPropertyStoreFromParsingName(shortcutPath, IntPtr.Zero, GpsReadWrite, ref iid, out IPropertyStore store);
        try
        {
            var value = new PropVariant
            {
                VariantType = VariantTypeLpwStr,
                PointerValue = Marshal.StringToCoTaskMemUni(appUserModelId)
            };
            var key = AppUserModelIdKey;
            var hr = store.SetValue(ref key, ref value);
            Marshal.FreeCoTaskMem(value.PointerValue); // 字符串内存由本方法分配，立即释放
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            hr = store.Commit();
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    /// <summary>设置当前进程的显式 AppUserModelID（Toast 归属本程序）。</summary>
    /// <param name="appUserModelId">应用用户模型 ID</param>
    public static void SetProcessAppUserModelId(string appUserModelId) =>
        SetCurrentProcessExplicitAppUserModelID(appUserModelId);

    /// <summary>P/Invoke：由解析名创建 IShellItem。</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string parsingName, IntPtr bindContext, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    /// <summary>P/Invoke：由解析名获取属性存储（用于写快捷方式 AUMID）。</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHGetPropertyStoreFromParsingName(string parsingName, IntPtr bindContext, uint flags,
        ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    /// <summary>P/Invoke：设置进程级 AppUserModelID。</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SetCurrentProcessExplicitAppUserModelID(string appId);
}

/// <summary>IPropertyStore：属性存储（只用到 SetValue/Commit）。</summary>
[ComImport]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    /// <summary>取属性数量。</summary>
    [PreserveSig]
    int GetCount(out uint propertyCount);

    /// <summary>按索引取键。</summary>
    [PreserveSig]
    int GetAt(uint propertyIndex, out PropertyKey key);

    /// <summary>取属性值。</summary>
    [PreserveSig]
    int GetValue(ref PropertyKey key, out PropVariant value);

    /// <summary>写属性值。</summary>
    [PreserveSig]
    int SetValue(ref PropertyKey key, ref PropVariant value);

    /// <summary>提交写入。</summary>
    [PreserveSig]
    int Commit();
}

/// <summary>PROPERTYKEY：属性键（16 字节 GUID + 4 字节 PID）。</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct PropertyKey
{
    /// <summary>格式 ID。</summary>
    public Guid FormatId;

    /// <summary>属性 ID。</summary>
    public uint PropertyId;
}

/// <summary>PROPVARIANT 的简化布局：仅用于 VT_LPWSTR 字符串写入。</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct PropVariant
{
    /// <summary>类型（VT_*）。</summary>
    public ushort VariantType;

    /// <summary>保留字段 1。</summary>
    public ushort Reserved1;

    /// <summary>保留字段 2。</summary>
    public ushort Reserved2;

    /// <summary>保留字段 3。</summary>
    public ushort Reserved3;

    /// <summary>值指针（VT_LPWSTR 时为字符串指针）。</summary>
    public IntPtr PointerValue;
}