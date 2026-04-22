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
    /// We write everything to <b>both</b> internal app storage (<c>FilesDir</c>) and external
    /// app storage (<c>GetExternalFilesDir(null)</c>) when both are available. Internal is the
    /// reliable target for the very-early window where external storage may not yet be ready;
    /// external is reachable by the user via the Files app on an unrooted device and receives
    /// alive markers / managed-exception dumps in real time so the user does not have to wait
    /// for a successful next startup to mirror the data over.
    ///
    /// Files (relative to each storage dir):
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
        private static string? sentinelPath;
        private static string? installedLogPath;
        private static bool sentinelWritten;

        /// <summary>
        /// Installs the native crash handler against the internal-storage log path, drops the
        /// sentinel, and writes the first "I am alive" marker. Idempotent — safe to call
        /// repeatedly from <c>Activity.OnCreate</c>; the underlying handler dedupes
        /// via its own <c>g_installed</c> flag.
        /// </summary>
        /// <param name="context">Any <see cref="Context"/> — typically the host Activity.</param>
        public static void InstallNativeHandler(Context context)
        {
            try
            {
                resolveDirs(context);

                installedLogPath = internalDir != null ? Path.Combine(internalDir, CRASH_LOG_NAME) : null;

                // The native handler is best-effort. Wrap so a DllNotFoundException
                // (libosu_native.so missing from the APK) cannot itself crash us.
                try
                {
                    OboeAudioBridge.nInstallCrashHandler(installedLogPath);

                    // Sentinel: only written when nInstallCrashHandler returned without throwing.
                    if (internalDir != null)
                    {
                        try
                        {
                            sentinelPath = Path.Combine(internalDir, SENTINEL_NAME);
                            File.WriteAllText(
                                sentinelPath,
                                $"installed_at={DateTime.UtcNow:O}\nlog_path={installedLogPath ?? "<none>"}\n");
                            sentinelWritten = true;
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
        /// Re-install the native signal handlers from a later startup phase, after the Mono
        /// runtime has installed its own SIGSEGV handler. This is what actually lets us catch
        /// JIT-thread null-deref crashes — without it, Mono's handler intercepts the fault
        /// first and re-raises via <c>tgkill</c> (visible in tombstones as
        /// <c>si_code = SI_TKILL</c>) without ever forwarding to us.
        /// </summary>
        public static void ReinstallNativeHandler()
        {
            try
            {
                OboeAudioBridge.nReinstallCrashHandler();
                WriteAliveMarker("CrashDiagnostics.ReinstallNativeHandler (chained on top of Mono)");
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] nReinstallCrashHandler P/Invoke failed: {e.Message}");
            }
        }

        /// <summary>
        /// Append a single-line "I am alive" marker to the crash log so that, when we later
        /// inspect a truncated/empty file after a crash, the last-written marker pinpoints
        /// which startup phase died. Writes to both internal and external storage so the user
        /// can pull the file immediately without waiting for a successful next startup to
        /// mirror it over.
        /// </summary>
        public static void WriteAliveMarker(string phase)
        {
            string line = $"=== ALIVE [{DateTime.UtcNow:O}] {phase} ===\n";
            appendToBoth(line);
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

            // FirstChanceException fires for *every* managed exception, even ones that get
            // caught later. On non-main managed threads (e.g. the Draw thread), Mono on
            // Android does not always route an unhandled exception through
            // AppDomain.UnhandledException before aborting — so without this hook the
            // exception that ultimately kills the process can vanish without trace. We
            // record it here on every throw so the *last* recorded exception before a
            // SIGSEGV/SIGABRT is the candidate culprit.
            //
            // Filtering policy:
            //   * On osu.Framework GameThreads (Draw/Update/Audio/Input): log *every*
            //     exception. An unhandled throw on any of these threads will tear down
            //     the process via Mono's tgkill(SIGSEGV) path with no managed trace
            //     reaching AppDomain.UnhandledException, so we cannot afford to filter.
            //   * On all other threads: keep the legacy "fatal-ish kinds" type filter
            //     so the log is not flooded by routine first-chance noise (e.g. the
            //     HidSharp / CFStringCreateWithCharacters EntryPointNotFoundException
            //     that fires every startup on .NET TP Worker).
            try
            {
                AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
                {
                    bool isGameThread = isOsuGameThread(Thread.CurrentThread.Name);

                    bool isFatalKind = e.Exception is NullReferenceException
                        or AccessViolationException
                        or StackOverflowException
                        or TypeInitializationException
                        or DllNotFoundException
                        or EntryPointNotFoundException
                        or BadImageFormatException
                        or TypeLoadException
                        or MissingMethodException
                        or MissingFieldException
                        or InvalidProgramException;

                    if (isGameThread || isFatalKind)
                    {
                        writeManagedException($"FirstChanceException ({e.Exception.GetType().Name})", e.Exception);
                    }
                };
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] Could not install FirstChanceException hook: {e.Message}");
            }
        }

        // osu.Framework names its game threads with stable prefixes such as
        // "DrawThread", "UpdateThread", "AudioThread", "InputThread", and the
        // tombstone we are debugging shows the comm name "Draw (GameThread)".
        // Match any of these so an exception thrown deep inside the renderer or
        // audio pipeline gets captured before Mono aborts the process.
        private static bool isOsuGameThread(string? name)
        {
            if (string.IsNullOrEmpty(name))
                return false;

            return name.StartsWith("Draw", StringComparison.Ordinal)
                   || name.StartsWith("Update", StringComparison.Ordinal)
                   || name.StartsWith("Audio", StringComparison.Ordinal)
                   || name.StartsWith("Input", StringComparison.Ordinal)
                   || name.Contains("GameThread", StringComparison.Ordinal);
        }

        /// <summary>
        /// Records a one-line summary of the native handler install state (sentinel exists?
        /// log path?) so the very first thing we see in the log on the next inspection tells
        /// us whether the native handler is even in place.
        /// </summary>
        public static void WriteInstallState()
        {
            try
            {
                string sentinelState;

                if (sentinelWritten && sentinelPath != null && File.Exists(sentinelPath))
                    sentinelState = "present";
                else if (sentinelWritten)
                    sentinelState = "written-but-missing";
                else
                    sentinelState = "absent";

                appendToBoth($"=== INSTALL_STATE sentinel={sentinelState} log_path={installedLogPath ?? "<none>"} internal_dir={internalDir ?? "<none>"} external_dir={externalDir ?? "<none>"} ===\n");
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] WriteInstallState failed: {e.Message}");
            }
        }

        private static void writeManagedException(string source, Exception? ex)
        {
            try
            {
                string block =
                    "\n=========================================================\n" +
                    "=== MANAGED EXCEPTION ===\n" +
                    $"  source     = {source}\n" +
                    $"  utc_time   = {DateTime.UtcNow:O}\n" +
                    $"  thread_id  = {Environment.CurrentManagedThreadId}\n" +
                    $"  thread_name= {Thread.CurrentThread.Name ?? "<null>"}\n" +
                    "\n" +
                    (ex?.ToString() ?? "<no exception object>") + "\n" +
                    "=== END OF MANAGED EXCEPTION ===\n\n";
                appendToBoth(block);
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] writeManagedException failed: {e.Message}");
            }
        }

        // Append the same payload to both internal (FilesDir) and external (GetExternalFilesDir)
        // crash logs. Either may legitimately be unavailable; failure of one path must not
        // prevent the other from being written. Each write is bounded, non-blocking, and
        // never throws out of this method — diagnostics must never themselves crash.
        private static void appendToBoth(string payload)
        {
            tryAppend(internalDir, payload);
            tryAppend(externalDir, payload);
        }

        private static void tryAppend(string? dir, string payload)
        {
            if (dir == null) return;

            try
            {
                string path = Path.Combine(dir, CRASH_LOG_NAME);
                using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var sw = new StreamWriter(fs);
                sw.Write(payload);
                sw.Flush();
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] CrashDiagnostics.tryAppend({dir}) failed: {e.Message}");
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
