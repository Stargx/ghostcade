using System.Runtime.InteropServices;

namespace Attractor.Core.Windowing;

/// <summary>
/// Sets a specific process's volume and mute via its WASAPI audio session —
/// instant, no relaunch, exactly what the Windows volume mixer's per-app slider
/// does. Both are scoped to that one process and are independent of the system
/// master volume. MAME spawns a NEW process per chunk, so the host re-applies the
/// level on every WindowReady.
/// </summary>
public static class ProcessAudio
{
    /// <returns>true if the process's session was found and muted/unmuted.</returns>
    public static bool TrySetMute(int pid, bool mute) =>
        TryApply(pid, vol => SetMute(vol, mute));

    /// <summary>Sets the process's per-app session volume, 0.0 (silent)–1.0 (full).</summary>
    /// <returns>true if the process's session was found and set.</returns>
    public static bool TrySetVolume(int pid, float level) =>
        TryApply(pid, vol => SetVolume(vol, level));

    /// <summary>Sets both volume and mute in a single session lookup, so a fresh
    /// chunk's session is brought to the intended state in one pass.</summary>
    /// <returns>true if the process's session was found and set.</returns>
    public static bool TrySetVolumeAndMute(int pid, float level, bool mute) =>
        TryApply(pid, vol => { SetVolume(vol, level); SetMute(vol, mute); });

    private static void SetVolume(ISimpleAudioVolume volume, float level)
    {
        var context = Guid.Empty;
        volume.SetMasterVolume(Math.Clamp(level, 0f, 1f), ref context);
    }

    private static void SetMute(ISimpleAudioVolume volume, bool mute)
    {
        var context = Guid.Empty;
        volume.SetMute(mute, ref context);
    }

    /// <summary>Find the render session owned by <paramref name="pid"/> and run
    /// <paramref name="action"/> against its volume interface.</summary>
    /// <returns>true if a matching session was found.</returns>
    private static bool TryApply(int pid, Action<ISimpleAudioVolume> action)
    {
        try
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
                    action(volume);
                    return true;
                }
                return false;
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }
        catch (COMException)
        {
            return false;
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
        void SetMasterVolume(float level, ref Guid eventContext);
        void NotImpl_GetMasterVolume();
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
    }
}
