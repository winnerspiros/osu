// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Android.App;
using Debug = System.Diagnostics.Debug;
using osu.Framework.Logging;

namespace osu.Android
{
    /// <summary>
    /// One-shot Android startup hooks that bound the on-disk log footprint.
    ///
    /// <para>
    /// The framework's runtime logger writes one file per launch under the game
    /// storage root (<c>&lt;external-files-dir&gt;/logs/&lt;timestamp&gt;.runtime.log</c>)
    /// and only prunes by *age* (>7 days). On a device that is rapidly
    /// relaunching after ANRs (the user reported 8 launches inside one minute,
    /// totalling ~90 MB), this is unbounded for practical purposes. We prune by
    /// total bytes here at every startup, deleting the oldest <c>*.log</c> files
    /// first until the directory fits into <see cref="MAX_LOG_BYTES"/>.
    /// </para>
    ///
    /// <para>
    /// Historically this class also lowered <see cref="Logger.Level"/> to
    /// <see cref="LogLevel.Important"/> on Android. That has been reverted —
    /// the framework's default verbosity is restored so osu.log captures the
    /// full per-thread startup narrative needed to diagnose hangs. On-disk
    /// log size is still bounded by <see cref="pruneLogDirectory"/> at every
    /// startup (oldest-first eviction down to <see cref="MAX_LOG_BYTES"/>),
    /// so verbose logs cannot regress the ~480 MB footprint that originally
    /// motivated this file.
    /// </para>
    /// </summary>
    internal static class LogManagement
    {
        // Hard cap on the total bytes consumed by *.log files in the log directory.
        // 6 MiB is chosen so that the user's overall on-disk diagnostics budget
        // (~20 MiB target) divides into ~6 MiB runtime logs + ~6 MiB internal
        // native_crash.log (capped via CrashDiagnostics rotation) + ~6 MiB
        // external native_crash.log (same cap). Important-level logs are very
        // small per launch (~327 bytes for a clean cold start observed in the
        // field), so 6 MiB still retains thousands of successive launches.
        public const long MAX_LOG_BYTES = 6L * 1024 * 1024;

        // Subdirectory under the game storage root where the framework logger
        // writes per-session log files. Mirrors osu.Game/IO/OsuStorage.cs:140
        // (`Logger.Storage = UnderlyingStorage.GetStorageForDirectory("logs")`).
        private const string log_subdir = "logs";

        /// <summary>
        /// Apply the global Android logging policy (level cap + on-disk prune).
        /// Idempotent and tolerates either step failing — diagnostics changes
        /// must never themselves block startup.
        /// </summary>
        public static void Apply()
        {
            // Verbose-logging toggle (default OFF). The framework writes
            // ~330 KB of runtime.log + ~28 KB of input.log per launch at the
            // default Verbose level, dominated by OpenTabletDriver per-tablet
            // detection and SDL platform-feature probe chatter — useful when
            // diagnosing a hang, not useful in the steady state. Default to
            // Important so on-disk log volume drops to a few KB per launch
            // and audio/render hot paths spend zero time formatting log
            // messages. Users can re-enable verbose logging from
            // Settings → Graphics → Android Performance to capture a full
            // log when they need to share one.
            //
            // Sentinel-driven (not config-driven) because LogManagement.Apply
            // runs in OsuGameActivity.OnCreate, LONG before the
            // OsuConfigManager exists — same pattern as the other Android
            // startup-safety flags. OsuGameAndroid mirrors the in-game
            // bindable into the sentinel via mirrorStartupFlag.
            try
            {
                bool verbose = AndroidStartupFlags.IsSet(AndroidStartupFlags.FLAG_VERBOSE_LOGGING_ENABLED);
                Logger.Level = verbose ? LogLevel.Verbose : LogLevel.Important;
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] LogManagement: could not apply Logger.Level: {e.Message}");
            }

            try
            {
                pruneLogDirectory();
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] LogManagement: pruneLogDirectory failed: {e.Message}");
            }
        }

        private static void pruneLogDirectory()
        {
            string? logsDir = resolveLogsDir();
            if (logsDir == null || !Directory.Exists(logsDir))
                return;

            // Order *.log files by last-write time ascending so we always delete
            // the oldest first. We use LastWriteTime (not CreationTime) because
            // CreationTime on Android FUSE can return stat0 (epoch) for files
            // created via certain APIs, which would defeat the ordering.
            FileInfo[] files;

            try
            {
                files = new DirectoryInfo(logsDir).GetFiles("*.log*", SearchOption.TopDirectoryOnly);
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] LogManagement: enumerate failed: {e.Message}");
                return;
            }

            long total = files.Sum(f => safeLength(f));
            if (total <= MAX_LOG_BYTES)
                return;

            foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc))
            {
                if (total <= MAX_LOG_BYTES)
                    break;

                long size = safeLength(file);

                try
                {
                    file.Delete();
                    total -= size;
                }
                catch (Exception e)
                {
                    // A file may legitimately be unwritable (e.g. another
                    // process holds it open on shutdown). Skip and continue —
                    // the next launch will retry it.
                    Debug.WriteLine($"[osu!] LogManagement: could not delete {file.Name}: {e.Message}");
                }
            }
        }

        private static long safeLength(FileInfo file)
        {
            try { return file.Length; }
            catch { return 0; }
        }

        private static string? resolveLogsDir()
        {
            try
            {
                // External files dir is the same directory tree the framework
                // uses as its UserStoragePath on Android (see
                // osu.Framework.Android/AndroidGameHost.UserStoragePaths).
                var ext = Application.Context.GetExternalFilesDir(null);
                if (ext == null || string.IsNullOrEmpty(ext.AbsolutePath))
                    return null;

                return Path.Combine(ext.AbsolutePath, log_subdir);
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] LogManagement: could not resolve external files dir: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Pin <c>ExecutionMode = MultiThreaded</c> in the on-disk
        /// <c>framework.ini</c> before the framework loads it.
        ///
        /// <para>
        /// Background: <c>osu.Framework.Android.AndroidGameHost.SetupConfig</c>
        /// historically registered SingleThread as the framework default
        /// (removed upstream in <c>ppy.osu.Framework 2026.427.2</c>; see commit
        /// <c>e756469</c>). The first launch of any older build therefore
        /// persisted SingleThread to disk, and any user with an existing
        /// <c>framework.ini</c> from a pre-fix build can still be running with
        /// the SingleThread loop on a single OS thread — a single slow Vulkan
        /// submit then blocks Android-lifecycle JNI callbacks and produces a
        /// black-screen ANR (the failure mode this method exists to prevent).
        /// </para>
        ///
        /// <para>
        /// Hardening: rather than only rewriting the literal value
        /// <c>SingleThread</c>, this method now ensures the file ALWAYS contains
        /// <c>ExecutionMode = MultiThreaded</c> — it appends the line if the key
        /// is missing entirely (e.g. a fresh install whose framework.ini was
        /// pre-created by <see cref="NormaliseFrameworkIniRendererDefault"/> and
        /// only contains the Renderer line) and rewrites any non-MultiThreaded
        /// value (SingleThread, DeferredThread, …). The MultiThreaded execution
        /// model is the only one tested + supported on Android, so unconditional
        /// pinning is safe.
        /// </para>
        ///
        /// <para>
        /// Best-effort and never throws — if the file is missing, malformed, or
        /// the rewrite fails, startup proceeds with the existing value (the
        /// in-memory force-set in <c>OsuGameBase.load()</c> is the safety net).
        /// </para>
        /// </summary>
        public static void NormaliseFrameworkIniExecutionMode()
        {
            try
            {
                string? root = resolveStorageRoot();
                if (root == null) return;

                string iniPath = Path.Combine(root, "framework.ini");
                if (!File.Exists(iniPath)) return;

                string[] lines;

                try
                {
                    lines = File.ReadAllLines(iniPath);
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"[osu!] LogManagement: could not read framework.ini: {e.Message}");
                    return;
                }

                bool changed = false;
                bool seenExecutionModeLine = false;

                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();

                    if (!string.Equals(key, "ExecutionMode", StringComparison.Ordinal))
                        continue;

                    seenExecutionModeLine = true;

                    if (!string.Equals(value, "MultiThreaded", StringComparison.Ordinal))
                    {
                        lines[i] = "ExecutionMode = MultiThreaded";
                        changed = true;
                    }

                    break;
                }

                if (!seenExecutionModeLine)
                {
                    // No ExecutionMode line at all — append one at the end of the file.
                    var newLines = new string[lines.Length + 1];
                    Array.Copy(lines, newLines, lines.Length);
                    newLines[lines.Length] = "ExecutionMode = MultiThreaded";
                    lines = newLines;
                    changed = true;
                }

                if (!changed) return;

                try
                {
                    File.WriteAllLines(iniPath, lines);
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"[osu!] LogManagement: could not rewrite framework.ini: {e.Message}");
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] LogManagement: NormaliseFrameworkIniExecutionMode failed: {e.Message}");
            }
        }

        // Sentinel file dropped after the one-shot Android renderer-default
        // normalisation has run. Stored in the storage root next to framework.ini
        // so a single existence check governs whether startup should touch the
        // renderer default at all.
        private const string renderer_migration_sentinel = "android_renderer_default_migrated.flag";

        /// <summary>
        /// One-shot Android renderer-default normalisation.
        ///
        /// <para>
        /// Earlier builds rewrote the Android default renderer to <c>OpenGL</c>
        /// on first launch in an attempt to dodge a Vulkan startup hang.
        /// Current field logs show the opposite failure mode as well:
        /// some devices now black-screen before the first managed heartbeat while
        /// booting the OpenGL/ANGLE path. Rewriting the default in either
        /// direction is therefore too risky; the framework's own default should
        /// be left untouched and safe-mode should only intervene after an actual
        /// failed launch.
        /// </para>
        ///
        /// <para>
        /// Best-effort and never throws. The method now only drops its sentinel
        /// so future launches know the normalisation has already been considered.
        /// Must be invoked from <c>OsuGameActivity.OnCreate</c> BEFORE the
        /// framework reads framework.ini, alongside the existing
        /// <see cref="NormaliseFrameworkIniExecutionMode"/> hook.
        /// </para>
        /// </summary>
        public static void NormaliseFrameworkIniRendererDefault()
        {
            try
            {
                string? root = resolveStorageRoot();
                if (root == null) return;

                string sentinelPath = Path.Combine(root, renderer_migration_sentinel);
                if (File.Exists(sentinelPath)) return;

                // On fresh installs (no framework.ini yet), default to OpenGL instead of Vulkan.
                // Vulkan causes black screens on several Adreno GPU families (7xx series in
                // particular) because the Veldrid Vulkan backend either times out its 5s
                // SurfaceHandle poll or hands a stale ANativeWindow to vkCreateAndroidSurfaceKHR.
                // OpenGL ES is the safer default; users can switch to Vulkan in Settings.
                string iniPath = Path.Combine(root, "framework.ini");
                if (!File.Exists(iniPath))
                {
                    try
                    {
                        File.WriteAllText(iniPath, "Renderer = OpenGL" + Environment.NewLine);
                        Debug.WriteLine("[osu!] Fresh install — defaulted renderer to OpenGL.");
                    }
                    catch (Exception e)
                    {
                        Debug.WriteLine($"[osu!] Could not write fresh-install renderer default: {e.Message}");
                        // Don't drop the sentinel — allow retry on next launch.
                        return;
                    }
                }

                tryDropSentinel(sentinelPath);
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] LogManagement: NormaliseFrameworkIniRendererDefault failed: {e.Message}");
            }
        }

        private static void tryDropSentinel(string sentinelPath)
        {
            try
            {
                File.WriteAllText(sentinelPath, string.Empty);
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] LogManagement: could not write renderer-migration sentinel: {e.Message}");
            }
        }

        /// <summary>
        /// Safe-mode renderer fallback: when the previous launch died before reaching the
        /// post-LoadComplete clear point (i.e. <see cref="AndroidStartupSafeMode.IsActive"/>
        /// is true), switch the on-disk <c>Renderer</c> away from the previous startup path
        /// for this launch only.
        ///
        /// <para>
        /// Why: older builds primarily failed on the Vulkan startup path, so safe-mode
        /// forced <c>OpenGL</c>. Current crash logs also show devices that wedge before
        /// the first managed heartbeat on the OpenGL/ANGLE path. Safe-mode therefore
        /// needs to escape whichever renderer was persisted previously instead of always
        /// retrying the same one.
        /// </para>
        ///
        /// <para>
        /// Persistence: when safe-mode switches <em>to</em> <c>OpenGL</c>, the original
        /// renderer value is saved to
        /// <see cref="AndroidStartupFlags.FLAG_SAFE_MODE_RENDERER_RESTORE"/> before
        /// being overwritten. <see cref="RestoreRendererAfterSafeMode"/> reads this on
        /// the next successful launch and restores the renderer automatically — making
        /// Vulkan→OpenGL a single-launch rescue rather than a permanent override.
        /// OpenGL→Automatic fallbacks deliberately do <em>not</em> auto-restore, because
        /// restoring the same failing OpenGL path would recreate the startup loop.
        /// </para>
        ///
        /// <para>
        /// Bypasses the <c>renderer_migration_sentinel</c> deliberately —
        /// <see cref="NormaliseFrameworkIniRendererDefault"/> is one-shot and intentionally
        /// respects user intent on subsequent launches; this method's job is precisely the
        /// opposite (override user intent when their previous startup path just failed).
        /// </para>
        ///
        /// <para>
        /// Best-effort and never throws — if the file is missing, malformed, or the rewrite
        /// fails, startup proceeds with the existing value.
        /// </para>
        /// </summary>
        public static void ForceOpenGLRendererIfSafeMode()
        {
            try
            {
                if (!AndroidStartupSafeMode.IsActive)
                    return;

                string? root = resolveStorageRoot();
                if (root == null) return;

                // The previous launch died (Vulkan ANR or native crash) before the
                // shader compilation burst finished.  The on-disk pipeline cache can
                // contain:
                //   • SPIR-V blobs compiled against the old GlobalUniformData layout
                //     (before the UniformPadding12 alignment fix in 2026.519.1) if
                //     the WipeShaderCacheOnceForVersion sentinel was already written
                //     but the Vulkan session was killed mid-compile.
                //   • Partially-written or incomplete pipeline objects from the
                //     interrupted Vulkan compile pass.
                //
                // Either case causes visual corruption on the rescue renderer session:
                //   – Argon hit circles render as white rectangles (masking uniform
                //     at wrong struct offset → CornerRadius clipping broken).
                //   – TrianglesV2 buttons show the wrong hue (gradient colour data
                //     at wrong offset → DrawColourInfo.Colour.Interpolate returns
                //     garbage channel values).
                //
                // Wipe the shader cache unconditionally here — bypassing the
                // version-code sentinel — so the rescue renderer always starts
                // from a clean slate. The sentinel is NOT reset: the next normal
                // (non-safe-mode) launch will still skip the version wipe and reuse
                // whatever cache the successful rescue session just rebuilt.
                string shaderCacheDir = Path.Combine(root, "cache", "shaders");

                if (Directory.Exists(shaderCacheDir))
                {
                    try
                    {
                        Directory.Delete(shaderCacheDir, recursive: true);
                        Logger.Log("[osu!] Android safe-mode: shader cache wiped to ensure a clean renderer fallback recompilation.", LoggingTarget.Runtime);
                    }
                    catch (Exception e)
                    {
                        Debug.WriteLine($"[osu!] LogManagement: safe-mode shader cache wipe failed ({e.Message}); falling back to per-entry sweep");
                        sweepDirectoryBestEffort(shaderCacheDir);
                    }
                }

                string iniPath = Path.Combine(root, "framework.ini");

                if (!File.Exists(iniPath))
                {
                    // No framework.ini yet (brand-new install whose previous launch
                    // died before any framework code ran) — pre-create with the
                    // safe renderer choice so the framework picks it up on first read.
                    try
                    {
                        const string initialFallbackRenderer = "OpenGL";
                        File.WriteAllText(iniPath, $"Renderer = {initialFallbackRenderer}" + System.Environment.NewLine);
                        Logger.Log($"[osu!] Android safe-mode renderer fallback: pre-created framework.ini with Renderer = {initialFallbackRenderer}", LoggingTarget.Performance);
                    }
                    catch (Exception e)
                    {
                        Debug.WriteLine($"[osu!] LogManagement: could not pre-create framework.ini for safe-mode: {e.Message}");
                    }
                    return;
                }

                string[] lines;

                try
                {
                    lines = File.ReadAllLines(iniPath);
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"[osu!] LogManagement: could not read framework.ini for safe-mode renderer fallback: {e.Message}");
                    return;
                }

                bool changed = false;
                bool seenRendererLine = false;
                string? previousValue = null;
                string fallbackRenderer = "OpenGL";

                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();

                    if (!string.Equals(key, "Renderer", StringComparison.Ordinal))
                        continue;

                    seenRendererLine = true;
                    previousValue = value;
                    fallbackRenderer = chooseSafeModeRenderer(previousValue);

                    if (!string.Equals(value, fallbackRenderer, StringComparison.Ordinal))
                    {
                        lines[i] = $"Renderer = {fallbackRenderer}";
                        changed = true;
                    }

                    break;
                }

                if (!seenRendererLine)
                {
                    var newLines = new string[lines.Length + 1];
                    Array.Copy(lines, newLines, lines.Length);
                    newLines[lines.Length] = $"Renderer = {fallbackRenderer}";
                    lines = newLines;
                    changed = true;
                }

                if (!changed) return;

                // Save the original renderer value so RestoreRendererAfterSafeMode()
                // can put it back after the next successful launch. Only save once —
                // if a restore flag is already present from a previous safe-mode that
                // has not yet been cleared (e.g. two consecutive crash launches), keep
                // the first saved value so we restore what the user actually chose, not
                // "OpenGL" from the previous safe-mode write.
                string? existingRestore = AndroidStartupFlags.ReadValue(AndroidStartupFlags.FLAG_SAFE_MODE_RENDERER_RESTORE);

                if (existingRestore == null
                    && previousValue != null
                    && string.Equals(fallbackRenderer, "OpenGL", StringComparison.Ordinal)
                    && !string.Equals(previousValue, "OpenGL", StringComparison.Ordinal))
                {
                    AndroidStartupFlags.WriteValue(AndroidStartupFlags.FLAG_SAFE_MODE_RENDERER_RESTORE, previousValue);
                }

                try
                {
                    File.WriteAllLines(iniPath, lines);
                    string reason = AndroidStartupSafeMode.DrawThreadNativeCrashTriggered
                        ? "Draw-thread native crash detected"
                        : "previous launch died before LoadComplete clear point";
                    string restoreText = string.Equals(fallbackRenderer, "OpenGL", StringComparison.Ordinal) && previousValue != null
                        ? $"temporary; will restore to {previousValue} after next successful launch"
                        : "sticky until the user changes it again";
                    Logger.Log($"[osu!] Android safe-mode renderer fallback ({reason}): Renderer {previousValue ?? "(unset)"} → {fallbackRenderer} ({restoreText})", LoggingTarget.Performance);
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"[osu!] LogManagement: could not rewrite framework.ini for safe-mode renderer fallback: {e.Message}");
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] LogManagement: ForceOpenGLRendererIfSafeMode failed: {e.Message}");
            }
        }

        private static string chooseSafeModeRenderer(string? previousRenderer)
        {
            return string.Equals(previousRenderer, "OpenGL", StringComparison.OrdinalIgnoreCase)
                ? "Automatic"
                : "OpenGL";
        }

        /// <summary>
        /// Called from <see cref="AndroidStartupSafeMode.ClearStartupInProgress"/> once the
        /// current launch is healthy. If <see cref="AndroidStartupFlags.FLAG_SAFE_MODE_RENDERER_RESTORE"/>
        /// contains a saved renderer value, restores it in <c>framework.ini</c> and deletes the flag
        /// so the restore only fires once. This makes the safe-mode OpenGL rewrite a single-launch
        /// rescue: a user who has Vulkan working correctly is automatically returned to Vulkan after
        /// the safe-mode launch succeeds.
        /// Best-effort and never throws.
        /// </summary>
        public static void RestoreRendererAfterSafeMode()
        {
            try
            {
                string? savedRenderer = AndroidStartupFlags.ReadValue(AndroidStartupFlags.FLAG_SAFE_MODE_RENDERER_RESTORE);

                if (string.IsNullOrEmpty(savedRenderer))
                    return;

                // Delete the restore flag first so a crash during the restore attempt
                // does not loop indefinitely.
                AndroidStartupFlags.Set(AndroidStartupFlags.FLAG_SAFE_MODE_RENDERER_RESTORE, false);

                string? root = resolveStorageRoot();
                if (root == null) return;

                string iniPath = Path.Combine(root, "framework.ini");

                if (!File.Exists(iniPath))
                    return;

                string[] lines;

                try
                {
                    lines = File.ReadAllLines(iniPath);
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"[osu!] LogManagement: could not read framework.ini for safe-mode renderer restore: {e.Message}");
                    return;
                }

                bool changed = false;
                bool seenRendererLine = false;

                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    string key = line.Substring(0, eq).Trim();
                    string currentValue = line.Substring(eq + 1).Trim();

                    if (!string.Equals(key, "Renderer", StringComparison.Ordinal))
                        continue;

                    seenRendererLine = true;

                    // Only restore if the framework.ini currently says OpenGL (i.e. the
                    // safe-mode write is still in place). If the user has already changed
                    // the renderer from Settings after the safe-mode launch, respect that.
                    if (string.Equals(currentValue, "OpenGL", StringComparison.Ordinal))
                    {
                        lines[i] = $"Renderer = {savedRenderer}";
                        changed = true;
                    }

                    break;
                }

                if (!changed && !seenRendererLine)
                {
                    // Renderer line missing entirely — append.
                    var newLines = new string[lines.Length + 1];
                    Array.Copy(lines, newLines, lines.Length);
                    newLines[lines.Length] = $"Renderer = {savedRenderer}";
                    lines = newLines;
                    changed = true;
                }

                if (!changed)
                {
                    Logger.Log($"[osu!] Android safe-mode renderer restore: skipped — renderer has already been changed from OpenGL (user likely changed it manually after safe-mode launch)", LoggingTarget.Performance);
                    return;
                }

                try
                {
                    File.WriteAllLines(iniPath, lines);
                    Logger.Log($"[osu!] Android safe-mode renderer restore: OpenGL → {savedRenderer} (safe-mode launch succeeded; restoring original renderer choice)", LoggingTarget.Performance);
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"[osu!] LogManagement: could not rewrite framework.ini for safe-mode renderer restore: {e.Message}");
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] LogManagement: RestoreRendererAfterSafeMode failed: {e.Message}");
            }
        }

        private static string? resolveStorageRoot()
        {
            try
            {
                var ext = Application.Context.GetExternalFilesDir(null);
                if (ext == null || string.IsNullOrEmpty(ext.AbsolutePath))
                    return null;

                return ext.AbsolutePath;
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] LogManagement: could not resolve external files dir: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Reads the persisted <c>Renderer</c> value from <c>framework.ini</c> on
        /// disk WITHOUT consulting the framework (which is not constructed yet at
        /// the call sites that need this). Returns the value verbatim
        /// (e.g. <c>"Vulkan"</c>, <c>"OpenGL"</c>, <c>"Automatic"</c>) or
        /// <see langword="null"/> if the file/key is missing.
        /// </summary>
        /// <remarks>
        /// Used by the cold-start CPU-affinity / thread-taming code in
        /// <c>OsuGameAndroid.LoadComplete</c> to back off from aggressive
        /// background-worker affinity pinning when the user has chosen Vulkan —
        /// the Adreno / Mali driver spawns its own internal worker threads
        /// during <c>vkCreateInstance</c> / <c>vkCreateSwapchainKHR</c>, and
        /// pinning them to LITTLE cores at any priority reliably stalls
        /// <c>vkQueuePresentKHR</c> on the Draw thread (visible in field logs as
        /// "Update tick 1, Draw tick 0" → black-screen ANR).
        /// </remarks>
        public static string? ReadConfiguredRenderer()
        {
            try
            {
                string? root = resolveStorageRoot();
                if (root == null) return null;

                string iniPath = Path.Combine(root, "framework.ini");
                if (!File.Exists(iniPath)) return null;

                string[] lines;

                try { lines = File.ReadAllLines(iniPath); }
                catch (Exception e)
                {
                    Debug.WriteLine($"[osu!] LogManagement: could not read framework.ini for ReadConfiguredRenderer: {e.Message}");
                    return null;
                }

                foreach (string line in lines)
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    string key = line.Substring(0, eq).Trim();
                    if (!string.Equals(key, "Renderer", StringComparison.Ordinal))
                        continue;

                    return line.Substring(eq + 1).Trim();
                }

                return null;
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] LogManagement: ReadConfiguredRenderer failed: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Convenience wrapper — true if the persisted renderer is exactly
        /// <c>Vulkan</c> (case-insensitive). False for OpenGL/Automatic/missing.
        /// </summary>
        public static bool IsVulkanConfigured()
        {
            string? renderer = ReadConfiguredRenderer();
            return renderer != null && string.Equals(renderer, "Vulkan", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Temporarily forces <c>FrameSync = VSync</c> in <c>framework.ini</c> when
        /// Vulkan is configured and the persisted value maps to IMMEDIATE present mode
        /// (i.e. <c>ActualUnlimited</c> or <c>Unlimited</c>).
        ///
        /// <para>
        /// <b>Why:</b> On Adreno 7xx (Snapdragon 8 Gen 2/3), applying
        /// <c>VK_PRESENT_MODE_IMMEDIATE_KHR</c> during the cold-start texture-upload burst
        /// triggers a swapchain recreation (FIFO → IMMEDIATE) while hundreds of textures
        /// are being uploaded. The Vulkan driver stalls in <c>vkQueuePresentKHR</c>,
        /// blocking the Draw thread indefinitely and producing a black screen + ANR.
        /// </para>
        ///
        /// <para>
        /// <b>How:</b> Before the framework reads <c>framework.ini</c> (in OnCreate,
        /// before <c>base.OnCreate</c>), we rewrite <c>FrameSync</c> to <c>VSync</c> so
        /// the swapchain is created in FIFO mode (safe). The original value is saved to
        /// <see cref="AndroidStartupFlags.FLAG_VULKAN_COLD_START_FRAME_SYNC_RESTORE"/>
        /// and restored by <see cref="OsuGameAndroid"/> after the Draw thread presents
        /// its first frame (via <c>FrameworkConfigManager.SetValue</c>, which applies
        /// in-memory AND persists to disk).
        /// </para>
        ///
        /// <para>
        /// <b>Safety:</b> If the process dies before restoration, next launch finds
        /// <c>FrameSync = VSync</c> in the ini (safe FIFO cold start) plus the restore
        /// flag still on disk, so the same deferred-switch cycle repeats. No user-visible
        /// permanent change to the config.
        /// </para>
        /// </summary>
        public static void ForceVSyncDuringVulkanColdStart()
        {
            try
            {
                if (!IsVulkanConfigured())
                    return;

                // If safe-mode is active, ForceOpenGLRendererIfSafeMode already switched
                // to OpenGL — no Vulkan swapchain will be created, so no override needed.
                if (AndroidStartupSafeMode.IsActive)
                    return;

                string? root = resolveStorageRoot();
                if (root == null) return;

                string iniPath = Path.Combine(root, "framework.ini");
                if (!File.Exists(iniPath)) return;

                string[] lines;

                try
                {
                    lines = File.ReadAllLines(iniPath);
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"[osu!] LogManagement: could not read framework.ini for Vulkan cold-start VSync override: {e.Message}");
                    return;
                }

                bool changed = false;
                string? originalValue = null;

                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();

                    if (!string.Equals(key, "FrameSync", StringComparison.Ordinal))
                        continue;

                    // Only override values that map to IMMEDIATE present mode.
                    // VSync and Limit2x use FIFO, which is safe during cold start.
                    if (string.Equals(value, "ActualUnlimited", StringComparison.Ordinal)
                        || string.Equals(value, "Unlimited", StringComparison.Ordinal))
                    {
                        originalValue = value;
                        lines[i] = "FrameSync = VSync";
                        changed = true;
                    }

                    break;
                }

                if (!changed || originalValue == null) return;

                // Save the original value so OsuGameAndroid can restore it after first frame.
                AndroidStartupFlags.WriteValue(AndroidStartupFlags.FLAG_VULKAN_COLD_START_FRAME_SYNC_RESTORE, originalValue);

                try
                {
                    File.WriteAllLines(iniPath, lines);
                    Logger.Log($"[osu!] Vulkan cold-start protection: FrameSync {originalValue} → VSync (FIFO) until first frame presents", LoggingTarget.Performance);
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"[osu!] LogManagement: could not rewrite framework.ini for Vulkan cold-start VSync override: {e.Message}");
                    // Clean up the flag since we couldn't apply the override
                    AndroidStartupFlags.Set(AndroidStartupFlags.FLAG_VULKAN_COLD_START_FRAME_SYNC_RESTORE, false);
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] LogManagement: ForceVSyncDuringVulkanColdStart failed: {e.Message}");
            }
        }

        // Sentinel file dropped after a successful one-shot shader-cache wipe.
        // Stored alongside the cache itself (not in the cache directory, which
        // we delete) so the marker survives the wipe. The file payload is the
        // package versionCode that triggered the wipe, so a future APK upgrade
        // bumps the code and re-arms the wipe automatically without requiring
        // any code change.
        private const string shader_cache_wipe_sentinel = "shader_cache_wipe.versioncode";

        /// <summary>
        /// One-shot wipe of the framework's on-disk shader pipeline cache,
        /// performed once per APK <c>versionCode</c>.
        ///
        /// <para>
        /// Background: the framework persists Veldrid pipeline-cache blobs
        /// under <c>&lt;external-files-dir&gt;/cache/shaders/</c>. On Adreno
        /// drivers (Samsung S24 / S25 family in particular) a stale or
        /// partially-written entry from a previous launch can cause
        /// <c>vkCreateGraphicsPipelines</c> to block for tens of seconds —
        /// long enough to trip Android's 10s input-dispatch ANR — every
        /// time a draw-thread shader-load reaches that entry. The user-visible
        /// symptom is "splash → black screen → ANR" with the runtime log
        /// stalled at a different shader-compile line on each launch (which
        /// is exactly the fingerprint we have been chasing).
        /// </para>
        ///
        /// <para>
        /// Wiping the directory is safe: the framework regenerates entries on
        /// first use after the wipe, with a one-time cold-start cost in the
        /// sub-second range (the cache is purely an optimisation; SPIR-V
        /// compile-from-source is the source of truth). Doing the wipe on
        /// every APK upgrade is the minimum-risk policy: it is unconditional
        /// for users who upgrade to a build containing this change, gated by
        /// versionCode for users who relaunch the same build.
        /// </para>
        ///
        /// <para>
        /// Best-effort and never throws. If the sentinel cannot be written
        /// after a successful wipe we just re-wipe on the next launch — that
        /// is wasteful but harmless.
        /// </para>
        /// </summary>
        public static void WipeShaderCacheOnceForVersion()
        {
            try
            {
                string? root = resolveStorageRoot();
                if (root == null) return;

                long versionCode = readPackageVersionCode();
                if (versionCode <= 0)
                {
                    // No reliable version identifier available — don't wipe
                    // (would re-wipe on every launch, which defeats the cache
                    // entirely). Surface the situation but proceed silently.
                    Debug.WriteLine("[osu!] LogManagement: skipping shader-cache wipe (no versionCode)");
                    return;
                }

                string sentinelPath = Path.Combine(root, shader_cache_wipe_sentinel);

                if (File.Exists(sentinelPath))
                {
                    string existing;
                    try { existing = File.ReadAllText(sentinelPath).Trim(); }
                    catch { existing = string.Empty; }

                    if (long.TryParse(existing, NumberStyles.Integer, CultureInfo.InvariantCulture, out long previous)
                        && previous == versionCode)
                    {
                        // Already wiped for this APK install — nothing to do.
                        return;
                    }
                }

                string shaderCacheDir = Path.Combine(root, "cache", "shaders");

                if (Directory.Exists(shaderCacheDir))
                {
                    try
                    {
                        Directory.Delete(shaderCacheDir, recursive: true);
                    }
                    catch (Exception e)
                    {
                        // A single locked file inside the directory should not
                        // veto the entire wipe — fall back to a per-entry sweep.
                        Debug.WriteLine($"[osu!] LogManagement: recursive shader-cache delete failed ({e.Message}); falling back to per-entry sweep");
                        sweepDirectoryBestEffort(shaderCacheDir);
                    }
                }

                try
                {
                    File.WriteAllText(sentinelPath, versionCode.ToString(CultureInfo.InvariantCulture));
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"[osu!] LogManagement: could not write shader-cache wipe sentinel: {e.Message}");
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] LogManagement: WipeShaderCacheOnceForVersion failed: {e.Message}");
            }
        }

        private static void sweepDirectoryBestEffort(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return;

                foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { File.Delete(file); }
                    catch (Exception e) { Debug.WriteLine($"[osu!] LogManagement: could not delete cache file {file}: {e.Message}"); }
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] LogManagement: sweepDirectoryBestEffort outer failure: {e.Message}");
            }
        }

        private static long readPackageVersionCode()
        {
            try
            {
                var ctx = Application.Context;
                var pm = ctx.PackageManager;
                if (pm == null || string.IsNullOrEmpty(ctx.PackageName))
                    return 0;

                var info = pm.GetPackageInfo(ctx.PackageName!, 0);
                if (info == null) return 0;

                // PackageInfo.LongVersionCode is the Android-API-28+ replacement
                // for the deprecated 32-bit VersionCode; we target API 33 so
                // it's always available.
                return info.LongVersionCode;
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] LogManagement: readPackageVersionCode failed: {e.Message}");
                return 0;
            }
        }
    }
}
