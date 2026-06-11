// Attractor smoke harness: drives Attractor.Core against a real MAME install
// with no UI. Doubles as the reference implementation for any future host
// (e.g. the Unreal 3D arcade). Rotation harness arrives with M2.

using Attractor.Core.Mame;

if (args is ["jobtest", var mamePath])
{
    // Launch MAME, then hard-kill ourselves with no cleanup. The kill-on-close
    // job object must take MAME down with us — the no-orphan guarantee.
    using var launcher = new MameLauncher();
    var proc = launcher.Launch(new MameLaunchSpec(mamePath, "", SecondsToRun: 0));
    Console.WriteLine($"PID={proc.Pid}");
    await Task.Delay(4000);
    System.Diagnostics.Process.GetCurrentProcess().Kill(); // TerminateProcess: no dispose, no finally
    return;
}

Console.WriteLine("Attractor.Smoke — rotation harness arrives with M2.");
