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
using System.Collections.Concurrent;

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

        // Subdirectory under each storage root where we write native_crash.log
        // and the install-state sentinel. Mirrors the framework logger's own
        // "logs" subdir (osu.Game/IO/OsuStorage.cs:140 — Logger.Storage =
        // UnderlyingStorage.GetStorageForDirectory("logs")) so users find ALL
        // diagnostic files in the same external folder when they pull files
        // for a bug report. Pre-2026.04.27 builds wrote native_crash.log
        // directly in the storage root; resolveDirs() migrates those files
        // into the new subdir on first run.
        public const string LOGS_SUBDIR = "logs";

        // Hard size cap on a single native_crash.log file. When reached we rotate the
        // file to "<name>.1" (overwriting any previous backup) and start a fresh log.
        // This bounds *each* of the internal and external locations to ~2× the cap
        // worst-case, regardless of how many crash-restart cycles the device endures.
        //
        // The cap exists to defeat the failure mode observed in the field where a
        // tight ANR-restart loop produced ~480 MB of native_crash.log on the user's
        // device storage in a few hours — every restart appended the previous
        // process's HangWatchdog dumps to the external log via
        // MirrorInternalLogToExternal, with no upper bound. 3 MiB is enough to hold
        // ~6 full HangWatchdog hang dumps including the per-thread /proc snapshot,
        // i.e. comfortably more than one process's worth of evidence after the new
        // HangWatchdog cap (max_dumps_per_process=20) is applied.
        private const long native_crash_log_max_bytes = 3L * 1024 * 1024;
        private const string crash_log_backup_suffix = ".1";

        private static int initialised;
        private static int managedHooksInstalled;

        // Global cap on FirstChanceException dumps written per process. A hot-path
        // throw loop (e.g. Veldrid "surface lost" thrown every Draw frame while the
        // Android Vulkan surface is unavailable during a slow startup) can otherwise
        // produce hundreds of full-stack dumps, each one a synchronous file write
        // on the throwing thread — which itself stalls the Draw thread and worsens
        // the very condition causing the throws.
        private const int first_chance_global_cap = 50;

        // Per-unique-stack cap. Higher (10) for true fatal kinds caught via
        // FirstChanceException-fallback or AppDomain.UnhandledException; lower (3)
        // for first-chance noise where seeing the first few occurrences is enough
        // to diagnose and the rest are pure log bloat.
        private const int per_key_cap_default = 10;
        private const int per_key_cap_first_chance = 3;

        private static string? internalDir;
        private static string? externalDir;
        private static readonly ConcurrentDictionary<string, int> exceptionCounts = new ConcurrentDictionary<string, int>();
        private static int firstChanceWriteCount;
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
            // Idempotent at the managed level: the native handler dedupes via its own
            // g_installed flag, but we also avoid re-writing the sentinel and re-running
            // the directory-resolution / P-Invoke path on repeat calls.
            if (Interlocked.Exchange(ref initialised, 1) != 0)
                return;

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
        /// Arm the native pthread liveness watchdog. Writes its hang dumps to the same
        /// internal-storage <c>native_crash.log</c> the rest of the diagnostics pipeline uses.
        ///
        /// <para>
        /// The native watchdog is the only diagnostic that survives a Mono stop-the-world GC
        /// pause: it runs as a pthread that never attaches to the runtime, so Mono cannot
        /// suspend it during STW. This is essential for diagnosing the "every managed thread
        /// parked in <c>__rt_sigsuspend</c>" startup hangs we have been chasing — under that
        /// failure mode the managed <see cref="osu.Android.HangWatchdog"/> is itself frozen
        /// and produces no dump.
        /// </para>
        ///
        /// <para>
        /// Idempotent on the native side; safe to call from any thread; never throws.
        /// Caller is expected to gate on <c>OsuSetting.AndroidNativeWatchdogEnabled</c> so
        /// the user can disable the diagnostic from in-game settings if it ever interferes
        /// with normal operation.
        /// </para>
        /// </summary>
        /// <param name="hangSeconds">Threshold (clamped to [3, 120] on the native side).</param>
        public static void StartNativeWatchdog(int hangSeconds)
        {
            try
            {
                NativeWatchdog.Start(installedLogPath, hangSeconds);
                WriteAliveMarker($"CrashDiagnostics.StartNativeWatchdog (threshold={hangSeconds}s)");
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] StartNativeWatchdog failed: {e.Message}");
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

                // Defeat the unbounded-growth failure mode: a tight ANR-restart
                // loop calls MirrorInternalLogToExternal on every startup, each
                // of which appends the previous process's full HangWatchdog
                // dump set to the external log. Without this rotation the
                // external file grew to hundreds of MB on the user's device
                // (one report: 480 MB across a single afternoon, and a
                // 500 MB native_crash.log.1 captured during a 2026.04.27
                // test session). Rotating *before* the append guarantees the
                // resulting file is at most <native_crash_log_max_bytes +
                // this_payload_size>, and a single ".1" backup retains the
                // previous generation.
                //
                // We also rotate the SOURCE (internal) log before mirroring,
                // because a pre-existing oversized internal log left behind by
                // an older build (or by the native handler appending past the
                // managed cap during a crash) could otherwise be copied
                // verbatim into the external path on the very next startup —
                // re-introducing the unbounded growth this method exists to
                // prevent. After rotation, the internal payload we mirror is
                // bounded to native_crash_log_max_bytes.
                rotateIfTooLarge(internalPath);
                rotateIfTooLarge(externalPath);

                try
                {
                    // Append, not overwrite — keep external as the running historical log.
                    // CopyTo() is bounded by the post-rotation internal size
                    // cap above, so the external file cannot grow by more than
                    // ~native_crash_log_max_bytes per mirror call. Even so, we
                    // belt-and-braces cap the per-call payload here so a
                    // future change to the rotation threshold cannot quietly
                    // remove this guarantee.
                    using (var src = new FileStream(internalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var dst = new FileStream(externalPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    {
                        copyBounded(src, dst, native_crash_log_max_bytes);
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
            if (ex is EntryPointNotFoundException && ex.Message.Contains("CFStringCreateWithCharacters"))
                return;

            bool isFirstChance = source.StartsWith("FirstChanceException", StringComparison.Ordinal);

            // Global cap on first-chance dumps: a hot-path throw loop on the Draw
            // thread can otherwise produce unbounded synchronous file writes, which
            // themselves stall the Draw thread and worsen the surface-acquisition
            // problem that caused the throws.
            if (isFirstChance && Interlocked.Increment(ref firstChanceWriteCount) > first_chance_global_cap)
                return;

            int perKeyCap = isFirstChance ? per_key_cap_first_chance : per_key_cap_default;
            string key = $"{source}_{ex?.GetType().Name}_{ex?.StackTrace?.GetHashCode() ?? 0}";
            if (exceptionCounts.AddOrUpdate(key, 1, (_, count) => count + 1) > perKeyCap)
                return;

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

                // For FirstChanceException we deliberately skip the external/FUSE
                // write — those writes are tens of milliseconds each and run on the
                // throwing thread (often the Draw thread). MirrorInternalLogToExternal
                // copies the internal log to external on the next startup, which is
                // sufficient for user-facing diagnostics without risking a Draw-thread
                // stall in the live process.
                if (isFirstChance)
                    tryAppend(internalDir, block);
                else
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

        /// <summary>
        /// Public entry point for other components (e.g. <c>HangWatchdog</c>) to append
        /// a diagnostic block into the same internal+external <c>native_crash.log</c>
        /// targets that the native handler and managed exception hooks write to.
        /// Never throws.
        /// </summary>
        public static void AppendDiagnosticBlock(string payload) => appendToBoth(payload);

        private static void tryAppend(string? dir, string payload)
        {
            if (dir == null) return;

            try
            {
                string path = Path.Combine(dir, CRASH_LOG_NAME);

                // Bound the file size before opening for append. A pathological
                // crash-restart loop would otherwise write hundreds of MB into
                // this single file — the rotation cap (one historical backup,
                // each ≤ native_crash_log_max_bytes) keeps the worst-case at
                // ~2× the cap regardless of how long the loop runs.
                rotateIfTooLarge(path);

                // Belt-and-braces: cap the per-call payload at half the file
                // cap so a single oversized write (e.g. a HangWatchdog dump
                // emitted from a process with hundreds of attached threads)
                // cannot itself exceed the rotation budget. The payload is
                // sliced from the FRONT — the head of a diagnostic block has
                // the per-event metadata + reason which is the actionable
                // signal; the tail is typically a continuation of the
                // /proc/self/task snapshot which truncates gracefully.
                long maxPayload = native_crash_log_max_bytes / 2;
                if (payload.Length > maxPayload)
                {
                    payload = payload.Substring(0, (int)maxPayload)
                              + $"\n  (… payload truncated at {maxPayload} bytes; full size was {payload.Length})\n";
                }

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

        // Bounded src→dst copy. Stops after `maxBytes`, appending a single
        // truncation marker so the consumer can tell the difference between a
        // file that ended naturally and one that ran out of budget. Never
        // throws — diagnostics paths must be failsafe.
        private static void copyBounded(Stream src, Stream dst, long maxBytes)
        {
            try
            {
                byte[] buffer = new byte[64 * 1024];
                long remaining = maxBytes;
                int n;
                while (remaining > 0 && (n = src.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining))) > 0)
                {
                    dst.Write(buffer, 0, n);
                    remaining -= n;
                }

                if (remaining == 0 && src.CanRead && src.Position < src.Length)
                {
                    byte[] marker = System.Text.Encoding.UTF8.GetBytes(
                        $"\n  (… mirror truncated at {maxBytes} bytes; source was {src.Length})\n");
                    dst.Write(marker, 0, marker.Length);
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] CrashDiagnostics.copyBounded failed: {e.Message}");
            }
        }

        // Pathological-size threshold: if the live file is more than this
        // multiple of the cap, rotation would just preserve unactionable bulk
        // forever in the .1 backup, so we delete instead. Picked at 4× so
        // ordinary "slightly over the cap" (a single oversized HangWatchdog
        // dump or a partial mirror copy) still rotates normally and retains
        // its history, while a 50 MB / 500 MB file from an older buggy build
        // gets nuked on first sight of the new code.
        private const int rotation_runaway_multiplier = 4;

        // If <path> exists and is at or above the size cap, move it to
        // "<path>.1" (overwriting any previous backup) so the next write starts
        // a fresh file. Best-effort and never throws — diagnostics paths must
        // not introduce new failure modes.
        private static void rotateIfTooLarge(string path)
        {
            try
            {
                if (!File.Exists(path)) return;

                long length;
                try { length = new FileInfo(path).Length; }
                catch { return; }

                if (length < native_crash_log_max_bytes) return;

                string backup = path + crash_log_backup_suffix;

                try { if (File.Exists(backup)) File.Delete(backup); }
                catch (Exception e) { Debug.WriteLine($"[osu!] CrashDiagnostics.rotateIfTooLarge: could not delete prior backup {backup}: {e.Message}"); }

                // Runaway-size short-circuit: if the live file is many multiples
                // of the cap (e.g. a 500 MB native_crash.log.1 left behind by
                // an older build), rotating it to <path>.1 would just preserve
                // the runaway payload as the new backup. The rest of the
                // diagnostics pipeline already capped per-write payloads, so
                // anything THIS large is by definition pre-existing garbage we
                // cannot make actionable. Truncate in place instead.
                if (length > native_crash_log_max_bytes * rotation_runaway_multiplier)
                {
                    Debug.WriteLine($"[osu!] CrashDiagnostics.rotateIfTooLarge: {path} is runaway ({length} bytes); truncating in place rather than rotating");
                    try { File.WriteAllText(path, string.Empty); }
                    catch (Exception e) { Debug.WriteLine($"[osu!] CrashDiagnostics.rotateIfTooLarge: runaway truncate failed: {e.Message}"); }
                    return;
                }

                try { File.Move(path, backup); }
                catch (Exception e)
                {
                    // If rename fails (e.g. cross-device on some FUSE setups),
                    // fall back to in-place truncation rather than leaving the
                    // file unbounded.
                    Debug.WriteLine($"[osu!] CrashDiagnostics.rotateIfTooLarge: rename failed ({e.Message}); truncating in place");
                    try { File.WriteAllText(path, string.Empty); }
                    catch (Exception inner) { Debug.WriteLine($"[osu!] CrashDiagnostics.rotateIfTooLarge: truncate also failed: {inner.Message}"); }
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] CrashDiagnostics.rotateIfTooLarge outer failure for {path}: {e.Message}");
            }
        }

        private static void resolveDirs(Context context)
        {
            string? rawInternal = null;
            string? rawExternal = null;

            try
            {
                var f = context.FilesDir;
                if (f != null && !string.IsNullOrEmpty(f.AbsolutePath))
                    rawInternal = f.AbsolutePath;
            }
            catch (Exception e) { Debug.WriteLine($"[osu!] Could not resolve internal FilesDir: {e.Message}"); }

            try
            {
                var e = context.GetExternalFilesDir(null);
                if (e != null && !string.IsNullOrEmpty(e.AbsolutePath))
                    rawExternal = e.AbsolutePath;
            }
            catch (Exception ex) { Debug.WriteLine($"[osu!] Could not resolve external files dir: {ex.Message}"); }

            // Resolve the per-storage `logs/` subdirs (matching the framework
            // logger's own location) and best-effort migrate any pre-existing
            // native_crash.log[/.1] from the storage root into the subdir.
            if (internalDir == null && rawInternal != null)
                internalDir = ensureLogsSubdir(rawInternal);
            if (externalDir == null && rawExternal != null)
                externalDir = ensureLogsSubdir(rawExternal);
        }

        // Resolve `<root>/logs`, create it if missing, and one-shot migrate any
        // pre-existing native_crash.log[.1] from `<root>` into `<root>/logs`.
        // Falls back to `<root>` if the subdir cannot be created so we never
        // lose the diagnostics target completely.
        private static string ensureLogsSubdir(string root)
        {
            try
            {
                string logs = Path.Combine(root, LOGS_SUBDIR);

                try { Directory.CreateDirectory(logs); }
                catch (Exception e)
                {
                    Debug.WriteLine($"[osu!] Could not create logs subdir under {root}: {e.Message}");
                    return root;
                }

                // Best-effort migration of pre-2026.04.27 layouts. Move (not
                // copy) so we don't double-count toward the prune budget.
                migrateLegacyFile(Path.Combine(root, CRASH_LOG_NAME), Path.Combine(logs, CRASH_LOG_NAME));
                migrateLegacyFile(Path.Combine(root, CRASH_LOG_NAME + crash_log_backup_suffix), Path.Combine(logs, CRASH_LOG_NAME + crash_log_backup_suffix));
                migrateLegacyFile(Path.Combine(root, SENTINEL_NAME), Path.Combine(logs, SENTINEL_NAME));

                return logs;
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] ensureLogsSubdir({root}) failed: {e.Message}");
                return root;
            }
        }

        private static void migrateLegacyFile(string oldPath, string newPath)
        {
            try
            {
                if (!File.Exists(oldPath)) return;
                if (File.Exists(newPath)) { try { File.Delete(oldPath); } catch { /* keep both rather than throw */ } return; }
                File.Move(oldPath, newPath);
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] migrateLegacyFile {oldPath} → {newPath} failed: {e.Message}");
            }
        }
    }
}
