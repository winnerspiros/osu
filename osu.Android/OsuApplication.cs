// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using Android.App;
using Android.Runtime;

namespace osu.Android
{
    /// <summary>
    /// Custom <see cref="Application"/> subclass that runs before any <see cref="Activity"/>
    /// is created. Used to install the native crash handler at the absolute earliest point
    /// in the process lifecycle so that crashes occurring during early library load,
    /// JNI_OnLoad, or static .NET assembly load are captured to <c>native_crash.log</c>
    /// instead of leaving only a 2-frame Android tombstone.
    /// </summary>
    [Application]
    public class OsuApplication : Application
    {
        public OsuApplication(System.IntPtr handle, JniHandleOwnership transfer)
            : base(handle, transfer)
        {
        }

        public override void OnCreate()
        {
            // Install the native crash handler FIRST — before base.OnCreate runs the
            // .NET runtime's own initialisation that may pull in reflection-heavy
            // assemblies and crash. The native handler doesn't depend on the .NET
            // runtime being fully initialised.
            CrashDiagnostics.InstallNativeHandler(this);
            CrashDiagnostics.InstallManagedExceptionHooks();
            CrashDiagnostics.WriteAliveMarker("Application.OnCreate entry");

            base.OnCreate();
        }
    }
}
