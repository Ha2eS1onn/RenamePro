using System.Runtime.InteropServices;

namespace RenamePro.Interop;

// 本文件为手写 COM Interop 声明。
// 依据：Windows SDK shobjidl_core.h / shobjidl.h（经 Wine 头文件与公开 SDK 引用交叉核对）：
//   IID_IOperationsProgressDialog = 0C9FB851-E5C9-43EB-A370-F0677B13874C
//   CLSID_OperationsProgressDialog = F8383852-FCD3-11d1-A6B9-006097DF5BD4
// 注意：手写接口的方法顺序必须与 vtable 顺序完全一致。

/// <summary>SPACTION：进度对话框的操作类型。</summary>
internal enum SpAction
{
    /// <summary>无操作。</summary>
    None = 0,
    /// <summary>移动。</summary>
    Moving = 1,
    /// <summary>复制（本项目按“写入新文件”语义使用）。</summary>
    Copying = 2,
    /// <summary>回收。</summary>
    Recycling = 3,
    /// <summary>应用属性。</summary>
    ApplyingAttributes = 4,
    /// <summary>下载。</summary>
    Downloading = 5,
    /// <summary>重命名。</summary>
    Renaming = 11
}

/// <summary>PDMODE：进度对话框显示模式（PDM_*）。</summary>
internal enum PdMode : uint
{
    /// <summary>默认。</summary>
    Default = 0x0,
    /// <summary>运行中（显示确定性进度）。</summary>
    Run = 0x1,
    /// <summary>预检。</summary>
    Preflight = 0x2,
    /// <summary>撤销中。</summary>
    Undoing = 0x4,
    /// <summary>错误阻塞。</summary>
    ErrorsBlocking = 0x8,
    /// <summary>不确定进度（滚动条/速度模式，用于未知总量）。</summary>
    Indeterminate = 0x10
}

/// <summary>OPPROGDLGF：StartProgressDialog 的标志（OPPROGDLG_*）。</summary>
internal enum OpgFlags : uint
{
    /// <summary>默认。</summary>
    Default = 0x0,
    /// <summary>允许暂停。</summary>
    EnablePause = 0x80,
    /// <summary>允许撤销。</summary>
    AllowUndo = 0x100,
    /// <summary>不显示源路径。</summary>
    DontDisplaySourcePath = 0x200,
    /// <summary>不显示目标路径。</summary>
    DontDisplayDestPath = 0x400,
    /// <summary>不做多日估算。</summary>
    NoMultiDayEstimates = 0x800,
    /// <summary>不显示位置行。</summary>
    DontDisplayLocations = 0x1000
}

/// <summary>PDOPSTATUS：进度对话框运行状态（用于检测用户“取消”）。</summary>
internal enum PdOpStatus
{
    /// <summary>运行中。</summary>
    Running = 1,
    /// <summary>已暂停。</summary>
    Paused = 2,
    /// <summary>已取消（用户点了取消或关闭对话框）。</summary>
    Cancelled = 3,
    /// <summary>已停止。</summary>
    Stopped = 4,
    /// <summary>出错。</summary>
    Errors = 5
}

/// <summary>
/// IOperationsProgressDialog：系统资源管理器传输框同款进度对话框。
/// 方法顺序即 vtable 顺序，不可调整。
/// </summary>
[ComImport]
[Guid("0C9FB851-E5C9-43EB-A370-F0677B13874C")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOperationsProgressDialog
{
    /// <summary>启动并显示对话框。</summary>
    [PreserveSig]
    int StartProgressDialog(IntPtr hwndOwner, uint flags);

    /// <summary>停止并关闭对话框。</summary>
    [PreserveSig]
    int StopProgressDialog();

    /// <summary>设置操作类型（SPACTION）。</summary>
    [PreserveSig]
    int SetOperation(int action);

    /// <summary>设置显示模式（PDMODE）。</summary>
    [PreserveSig]
    int SetMode(uint mode);

    /// <summary>更新进度（点数/大小/项目数，均为 当前值 + 总量）。</summary>
    [PreserveSig]
    int UpdateProgress(ulong currentPoints, ulong totalPoints, ulong currentSize, ulong totalSize,
        ulong currentItem, ulong totalItem);

    /// <summary>更新显示的位置（源、目标、当前项）。</summary>
    [PreserveSig]
    int UpdateLocations(IShellItem? source, IShellItem? target, IShellItem? item);

    /// <summary>重置计时器。</summary>
    [PreserveSig]
    int ResetTimer();

    /// <summary>暂停计时器。</summary>
    [PreserveSig]
    int PauseTimer();

    /// <summary>恢复计时器。</summary>
    [PreserveSig]
    int ResumeTimer();

    /// <summary>取已用/剩余毫秒数。</summary>
    [PreserveSig]
    int GetMilliseconds(out ulong elapsed, out ulong remaining);

    /// <summary>取运行状态（PDOPSTATUS）。</summary>
    [PreserveSig]
    int GetOperationStatus(out int status);
}

/// <summary>CLSID_OperationsProgressDialog 的 coclass（new 即 CoCreateInstance）。</summary>
[ComImport]
[Guid("F8383852-FCD3-11d1-A6B9-006097DF5BD4")]
internal class OperationsProgressDialog
{
}

/// <summary>IShellItem：Shell 项（进度对话框显示路径/文件名用）。</summary>
[ComImport]
[Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem
{
    /// <summary>绑定到处理器（本项目不使用）。</summary>
    [PreserveSig]
    int BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid, out IntPtr result);

    /// <summary>取父项。</summary>
    [PreserveSig]
    int GetParent(out IShellItem parent);

    /// <summary>取显示名。</summary>
    [PreserveSig]
    int GetDisplayName(uint nameType, out IntPtr name);

    /// <summary>取属性。</summary>
    [PreserveSig]
    int GetAttributes(uint mask, out uint attributes);

    /// <summary>比较两个 Shell 项。</summary>
    [PreserveSig]
    int Compare(IShellItem other, uint hint, out int order);
}