// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Debug = System.Diagnostics.Debug;
using osu.Android.Native;

namespace osu.Android
{
    /// <summary>
    /// Centralised Android crash-diagnostics plumbing.
    ///
    /// We write everything to <b>internal</b> app storage (<c>FilesDir</c>) because external
    /// storage is FUSE-backed, scoped-storage-restricted, and may not be ready at the
    /// instant a very-early crash hits. On the next normal startup we mirror the internal
    /// crash log to <c>GetExternalFilesDir(null)</c> so the user can grab it via the Files
    /// app on an unrooted device, then truncate the internal copy.
    ///
    /// Files (all relative to <c>FilesDir</c>):
    ///   <list type="bullet">
    ///     <item><c>native_crash.log</c> — append target for both the native handler and the managed last-chance hooks; also receives "I am alive" startup markers.</item>
    ///     <item><c>crash_handler_installed.txt</c> — sentinel dropped immediately after <c>nInstallCrashHandler</c> returns. Lets us distinguish "handler never installed (P/Invoke failed → libosu_native.so missing)" from "handler installed but signal bypassed it".</item>
    ///   </list>
    /// </summary>
    internal static class CrashDiagnostics
    {
        public const string CRASH_LOG_NAME = "native_crash.log";
        public const string SENTINEL_NAME = "crash_handler_installed.txt";

        private static int initialised;
        private static int managedHooksInstalled;

        private static string? internalDir;
        private static string? externalDir;

        /// <summary>
        /// Installs the native crash handler against the internal-storage log path, drops the
        /// sentinel, and writes the first "I am alive" marker. Idempotent — safe to call from
        /// both <see cref="Application.OnCreate"/> and <see cref="Activity.OnCreate(Bundle)"/>;
        /// the underlying handler dedupes via its own <c>g_installed</c> flag.
        /// </summary>
        /// <param name="context">Any <see cref="Context"/> — typically the Application or Activity.</param>
        public static void InstallNativeHandler(Context context)
        {
            try
            {
                resolveDirs(context);

                string? logPath = internalDir != null ? Path.Combine(internalDir, CRASH_LOG_NAME) : null;

                // The native handler is best-effort. Wrap so a DllNotFoundException
                // (libosu_native.so missing from the APK) cannot itself crash us.
                try
                {
                    OboeAudioBridge.nInstallCrashHandler(logPath);

                    // Sentinel: only written when nInstallCrashHandler returned without throwing.
                    if (internalDir != null)
                    {
                        try
                        {
                            File.WriteAllText(
                                Path.Combine(internalDir, SENTINEL_NAME),
                                $"installed_at={DateTime.UtcNow:O}\nlog_path={logPath ?? "<none>"}\n");
                        }
                        catch (Exception e) { Debug.WriteLine($"[osu!] Could not write crash-handler sentinel: {e.Message}"); }
                    }
                }
                catch (Exception e)
                {
                    // Most likely DllNotFoundException. Already handled defensively elsewhere
                    // — log to Debug and carry on so startup is unaffected.
                    Debug.WriteLine($"[osu!] nInstallCrashHandler P/Invoke failed: {e.Message}");
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] CrashDiagnostics.InstallNativeHandler outer failure: {e.Message}");
            }

            Interlocked.Exchange(ref initialised, 1);
        }

        /// <summary>
        /// Append a single-line "I am alive" marker to the internal crash log so that, when
        /// we later inspect a truncated/empty file after a crash, the last-written marker
        /// pinpoints which startup phase died.
        /// </summary>
        public static void WriteAliveMarker(string phase)
        {
            try
            {
                if (internalDir == null) return;

                string path = Path.Combine(internalDir, CRASH_LOG_NAME);
                string line = $"=== ALIVE [{DateTime.UtcNow:O}] {phase} ===\n";

                // Append using a bounded write — never throw, never block.
                using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var sw = new StreamWriter(fs);
                sw.Write(line);
                sw.Flush();
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] WriteAliveMarker({phase}) failed: {e.Message}");
            }
        }

        /// <summary>
        /// One-shot post-crash mirror: if an internal <c>native_crash.log</c> exists and is
        /// non-empty, copy it to external app storage (so the user can pull it via the Files
        /// app on an unrooted device) and truncate the internal copy so subsequent runs only
        /// surface fresh crashes.
        /// </summary>
        public static void MirrorInternalLogToExternal()
        {
            try
            {
                if (internalDir == null || externalDir == null) return;

                string internalPath = Path.Combine(internalDir, CRASH_LOG_NAME);
                if (!File.Exists(internalPath)) return;

                var info = new FileInfo(internalPath);
                if (info.Length == 0) return;

                string externalPath = Path.Combine(externalDir, CRASH_LOG_NAME);

                try
                {
                    // Append, not overwrite — keep external as the running historical log.
                    using (var src = new FileStream(internalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var dst = new FileStream(externalPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    {
                        src.CopyTo(dst);
                        dst.Flush();
                    }
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"[osu!] Could not mirror internal crash log to external storage: {e.Message}");
                    return;
                }

                // Truncate internal so next-startup markers start fresh.
                try { File.WriteAllText(internalPath, string.Empty); }
                catch (Exception e) { Debug.WriteLine($"[osu!] Could not truncate internal crash log: {e.Message}"); }
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] MirrorInternalLogToExternal failed: {e.Message}");
            }
        }

        /// <summary>
        /// Hook the .NET last-chance exception paths. Mono's default unhandled-exception
        /// behaviour prints to logcat and aborts; on user devices that printout is lost.
        /// Catching it ourselves and writing to disk gives us the full managed stack —
        /// which is what we actually need for the uptime-5s SDLThread crash class.
        /// </summary>
        public static void InstallManagedExceptionHooks()
        {
            if (Interlocked.Exchange(ref managedHooksInstalled, 1) != 0)
                return;

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                writeManagedException("AppDomain.UnhandledException", e.ExceptionObject as Exception);
            };

            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                writeManagedException("TaskScheduler.UnobservedTaskException", e.Exception);
                // Don't mark observed — the framework / sentry pipeline still wants to see it.
            };
        }

        private static void writeManagedException(string source, Exception? ex)
        {
            try
            {
                if (internalDir == null) return;

                string path = Path.Combine(internalDir, CRASH_LOG_NAME);
                using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var sw = new StreamWriter(fs);

                sw.WriteLine();
                sw.WriteLine("=========================================================");
                sw.WriteLine("=== MANAGED UNHANDLED EXCEPTION ===");
                sw.WriteLine($"  source     = {source}");
                sw.WriteLine($"  utc_time   = {DateTime.UtcNow:O}");
                sw.WriteLine($"  thread_id  = {Environment.CurrentManagedThreadId}");
                sw.WriteLine();
                sw.WriteLine(ex?.ToString() ?? "<no exception object>");
                sw.WriteLine("=== END OF MANAGED EXCEPTION ===");
                sw.WriteLine();
                sw.Flush();
                fs.Flush(true);
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] writeManagedException failed: {e.Message}");
            }
        }

        private static void resolveDirs(Context context)
        {
            try
            {
                if (internalDir == null)
                {
                    var f = context.FilesDir;
                    if (f != null && !string.IsNullOrEmpty(f.AbsolutePath))
                        internalDir = f.AbsolutePath;
                }
            }
            catch (Exception e) { Debug.WriteLine($"[osu!] Could not resolve internal FilesDir: {e.Message}"); }

            try
            {
                if (externalDir == null)
                {
                    var e = context.GetExternalFilesDir(null);
                    if (e != null && !string.IsNullOrEmpty(e.AbsolutePath))
                        externalDir = e.AbsolutePath;
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[osu!] Could not resolve external files dir: {ex.Message}"); }
        }
    }
}
