using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Attractor.Core.Windowing;

/// <summary>
/// A kill-on-close Win32 job object. Every MAME process is assigned to it
/// before its first instruction runs, so no emulator can outlive the app —
/// even if the app is terminated with taskkill /f.
/// </summary>
public sealed class JobObject : IDisposable
{
    private readonly IntPtr _handle;

    public JobObject()
    {
        _handle = NativeMethods.CreateJobObjectW(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed");

        var info = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!NativeMethods.SetInformationJobObject(
                _handle, NativeMethods.JobObjectExtendedLimitInformation,
                ref info, (uint)Marshal.SizeOf<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetInformationJobObject failed");
    }

    public void Assign(IntPtr processHandle)
    {
        if (!NativeMethods.AssignProcessToJobObject(_handle, processHandle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "AssignProcessToJobObject failed");
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
            NativeMethods.CloseHandle(_handle);
    }
}
