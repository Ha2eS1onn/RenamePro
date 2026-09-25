using System.Runtime.InteropServices;
using RenamePro.Core;

namespace RenamePro.Interop;

/// <summary>
/// IOperationsProgressDialog 的托管包装（外观同资源管理器传输框）。
/// 只允许在专用 STA 消息线程上创建与调用（由 Progress\StaDispatcher 封送）；
/// 所有调用返回 HRESULT 并在此处统一检查，任一步失败都记为“降级”。
/// </summary>
internal sealed class ShellProgressDialog : IDisposable
{
    /// <summary>底层 COM 对象（仅 STA 线程访问）。</summary>
    private IOperationsProgressDialog? _dialog;

    /// <summary>对话框是否已成功显示。</summary>
    public bool IsStarted { get; private set; }

    /// <summary>
    /// 创建并显示进度对话框。
    /// </summary>
    /// <param name="ownerHandle">拥有者窗口句柄（可为 IntPtr.Zero）</param>
    /// <returns>成功返回 true；失败返回 false（调用方应降级为静默转换）</returns>
    public bool Start(IntPtr ownerHandle)
    {
        var step = "CoCreateInstance";
        try
        {
            // CoCreateInstance(CLSID_OperationsProgressDialog)
            _dialog = (IOperationsProgressDialog)new OperationsProgressDialog();
            // 顺序必须为：先启动对话框，再设置操作类型与显示模式
            // （在未启动时调用 SetOperation/SetMode 会返回 E_UNEXPECTED）
            step = "StartProgressDialog";
            CheckHr(_dialog.StartProgressDialog(ownerHandle, (uint)OpgFlags.Default), step);
            step = "SetOperation";
            CheckHr(_dialog.SetOperation((int)SpAction.Copying), step);
            step = "SetMode";
            CheckHr(_dialog.SetMode((uint)PdMode.Run), step);
            IsStarted = true;
            return true;
        }
        catch (Exception ex)
        {
            // 初始化失败（CLSID 未注册 / 环境不支持 / 被策略拦截）→ 整体降级
            Log.Warn($"进度对话框启动失败（步骤 {step}）：{ex.Message}（本会话降级为静默转换 + 完成后 Toast）");
            Dispose();
            return false;
        }
    }

    /// <summary>切换为不确定进度（滚动条/速度模式，用于未知总量）。</summary>
    public void SetIndeterminate()
    {
        if (_dialog == null) return;
        Try(_dialog.SetMode((uint)PdMode.Indeterminate), "SetMode(Indeterminate)");
    }

    /// <summary>切换回确定性进度。</summary>
    public void SetNormal()
    {
        if (_dialog == null) return;
        Try(_dialog.SetMode((uint)PdMode.Run), "SetMode(Run)");
    }

    /// <summary>更新进度（按点数：当前已完成 / 总点数）。</summary>
    /// <param name="currentPoints">当前完成量</param>
    /// <param name="totalPoints">总量</param>
    public void UpdateProgress(ulong currentPoints, ulong totalPoints)
    {
        if (_dialog == null) return;
        Try(_dialog.UpdateProgress(currentPoints, totalPoints, 0, 0, 0, 0), "UpdateProgress");
    }

    /// <summary>更新对话框显示的位置（同目录转换：源/目标为所在目录，当前项为正在处理的文件）。</summary>
    /// <param name="currentItemPath">当前处理的文件完整路径</param>
    public void SetLocations(string currentItemPath)
    {
        if (_dialog == null) return;
        IShellItem? folder = null;
        IShellItem? item = null;
        try
        {
            var directory = Path.GetDirectoryName(currentItemPath);
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory)) folder = ShellItemHelper.CreateItem(directory);
            if (File.Exists(currentItemPath)) item = ShellItemHelper.CreateItem(currentItemPath);
            Try(_dialog.UpdateLocations(folder, folder, item), "UpdateLocations");
        }
        catch (Exception ex)
        {
            // 位置显示失败不影响进度显示
            Log.Warn($"更新进度对话框位置失败：{ex.Message}");
        }
        finally
        {
            if (item != null) Marshal.ReleaseComObject(item);
            if (folder != null) Marshal.ReleaseComObject(folder);
        }
    }

    /// <summary>
    /// 读取对话框状态（用于检测用户点击“取消”）。
    /// </summary>
    /// <returns>PDOPSTATUS 值；读取失败或对话框已关闭返回 -1</returns>
    public int GetStatus()
    {
        if (_dialog == null) return -1;
        try
        {
            return _dialog.GetOperationStatus(out var status) >= 0 ? status : -1;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>停止并关闭对话框，释放 COM 对象。</summary>
    public void Stop()
    {
        var dialog = _dialog;
        _dialog = null;
        IsStarted = false;
        if (dialog == null) return;
        try
        {
            dialog.StopProgressDialog();
        }
        catch
        {
            // 对话框可能已被用户关闭
        }
        try
        {
            Marshal.ReleaseComObject(dialog);
        }
        catch
        {
            // 忽略释放异常
        }
    }

    /// <summary>释放（等同停止）。</summary>
    public void Dispose() => Stop();

    /// <summary>HRESULT 检查：失败则抛出。</summary>
    private static void CheckHr(int hr, string operation)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    /// <summary>尽力调用：失败只记日志（进度类更新失败不该中断转换）。</summary>
    private static void Try(int hr, string operation)
    {
        if (hr >= 0) return;
        Log.Warn($"进度对话框 {operation} 返回 HRESULT=0x{hr:X8}");
    }
}