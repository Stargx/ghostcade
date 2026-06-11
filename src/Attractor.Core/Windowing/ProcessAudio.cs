using System.Runtime.InteropServices;

namespace Attractor.Core.Windowing;

/// <summary>
/// Mutes/unmutes a specific process's audio session via WASAPI — instant, no
/// relaunch, exactly what the volume mixer does. MAME spawns a NEW process per
/// chunk, so the host re-applies the mute on every WindowReady.
/// </summary>
public static class ProcessAudio
{
    /// <returns>true if the process's session was found and set.</returns>
    public static bool TrySetMute(int pid, bool mute)
    {
        try
        {
            return SetMuteCore(pid, mute);
        }
        catch (COMException)
        {
            return false;
        }
    }

    private static bool SetMuteCore(int pid, bool mute)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        try
        {
            enumerator.GetDefaultAudioEndpoint(DataFlowRender, RoleMultimedia, out var device);
            var iid = typeof(IAudioSessionManager2).GUID;
            device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var managerObj);
            var manager = (IAudioSessionManager2)managerObj;
            manager.GetSessionEnumerator(out var sessions);
            sessions.GetCount(out int count);

            for (int i = 0; i < count; i++)
            {
                sessions.GetSession(i, out var session);
                if (session is not IAudioSessionControl2 session2)
                    continue;
                session2.GetProcessId(out uint sessionPid);
                if (sessionPid != (uint)pid)
                    continue;
                if (session is not ISimpleAudioVolume volume)
                    continue;
                var context = Guid.Empty;
                volume.SetMute(mute, ref context);
                return true;
            }
            return false;
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private const int DataFlowRender = 0;
    private const int RoleMultimedia = 1;
    private const int ClsCtxAll = 0x17;

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        void NotImpl_EnumAudioEndpoints();
        void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object @interface);
    }

    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        void NotImpl_GetAudioSessionControl();
        void NotImpl_GetSimpleAudioVolume();
        void GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
    }

    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        void GetCount(out int sessionCount);
        void GetSession(int index, out IAudioSessionControl session);
    }

    [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        void NotImpl_GetState();
        void NotImpl_GetDisplayName();
        void NotImpl_SetDisplayName();
        void NotImpl_GetIconPath();
        void NotImpl_SetIconPath();
        void NotImpl_GetGroupingParam();
        void NotImpl_SetGroupingParam();
        void NotImpl_RegisterAudioSessionNotification();
        void NotImpl_UnregisterAudioSessionNotification();
    }

    [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2 // : IAudioSessionControl
    {
        // inherited IAudioSessionControl vtable
        void NotImpl_GetState();
        void NotImpl_GetDisplayName();
        void NotImpl_SetDisplayName();
        void NotImpl_GetIconPath();
        void NotImpl_SetIconPath();
        void NotImpl_GetGroupingParam();
        void NotImpl_SetGroupingParam();
        void NotImpl_RegisterAudioSessionNotification();
        void NotImpl_UnregisterAudioSessionNotification();
        // IAudioSessionControl2
        void NotImpl_GetSessionIdentifier();
        void NotImpl_GetSessionInstanceIdentifier();
        void GetProcessId(out uint pid);
    }

    [Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        void NotImpl_SetMasterVolume();
        void NotImpl_GetMasterVolume();
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
    }
}
