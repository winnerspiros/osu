// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Runtime.InteropServices;
using Debug = System.Diagnostics.Debug;

namespace osu.Android.Native
{
    /// <summary>
    /// Managed-side wrapper for the native pthread liveness watchdog implemented in
    /// <c>osu.Android/Native/native_watchdog.cpp</c>.
    ///
    /// <para>
    /// The native watchdog exists because the managed <see cref="osu.Android.HangWatchdog"/>
    /// runs as a normal <c>System.Threading.Thread</c>, which Mono suspends during a
    /// stop-the-world GC by sending <c>SIGRTMIN+N</c>. If a Mono thread is stuck
    /// inside a long native call (Vulkan present-queue futex, Realm fifo open,
    /// AAudio init, …) the STW request never completes and every other managed
    /// thread — including our managed watchdog's monitor — is parked indefinitely.
    /// A pure pthread-only watchdog that never attaches to Mono is the only thing
    /// that can produce a diagnostic dump under that condition.
    /// </para>
    ///
    /// <para>
    /// All entry points are best-effort: a missing native library or a
    /// <see cref="DllNotFoundException"/> is non-fatal and silently downgraded to
    /// a <see cref="Debug.WriteLine"/> call so startup is unaffected.
    /// </para>
    /// </summary>
    internal static class NativeWatchdog
    {
        // Same lib name used by OboeAudioBridge — single shared libosu_native.so.
        private const string lib_name = "osu_native";

        /// <summary>
        /// Arm the native watchdog with the given log path and hang threshold.
        /// Idempotent: subsequent calls are no-ops on the native side.
        /// Never throws.
        /// </summary>
        /// <param name="logPath">Absolute path of the file to append hang dumps to (typically <c>FilesDir/native_crash.log</c>).</param>
        /// <param name="hangSeconds">Seconds without a heartbeat before a dump is triggered. Native side clamps to [3, 120].</param>
        public static void Start(string? logPath, int hangSeconds)
        {
            try
            {
                osu_native_watchdog_start(logPath, hangSeconds);
            }
            catch (DllNotFoundException e)
            {
                Debug.WriteLine($"[osu!] NativeWatchdog.Start: libosu_native.so not loaded, watchdog disabled ({e.Message})");
            }
            catch (EntryPointNotFoundException e)
            {
                // The native entry point is absent — most likely an old libosu_native.so
                // in the APK that does not include native_watchdog.cpp. Treat as disabled
                // rather than crashing the user's startup.
                Debug.WriteLine($"[osu!] NativeWatchdog.Start: entry point missing, watchdog disabled ({e.Message})");
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] NativeWatchdog.Start unexpected failure: {e.Message}");
            }
        }

        /// <summary>
        /// Bump the native heartbeat. Called from the managed
        /// <see cref="osu.Android.HangWatchdog"/> per-thread tick so the native watchdog
        /// can observe Update-thread liveness across Mono STW pauses. The underlying
        /// native call performs a single <c>__atomic_store_n</c> on a 64-bit slot;
        /// safe to call at any rate, from any thread, without locking.
        /// Never throws.
        /// </summary>
        public static void Heartbeat()
        {
            try
            {
                osu_native_watchdog_heartbeat();
            }
            catch (DllNotFoundException) { /* watchdog disabled — no-op */ }
            catch (EntryPointNotFoundException) { /* old libosu_native.so — no-op */ }
            catch (Exception e)
            {
                // Heartbeat is on the GameThread tick path; never let a diagnostic
                // failure escape into the game loop.
                Debug.WriteLine($"[osu!] NativeWatchdog.Heartbeat unexpected failure: {e.Message}");
            }
        }

        [DllImport(lib_name)]
        private static extern void osu_native_watchdog_start([MarshalAs(UnmanagedType.LPUTF8Str)] string? logPath, int hangSeconds);

        [DllImport(lib_name)]
        private static extern void osu_native_watchdog_heartbeat();
    }
}
