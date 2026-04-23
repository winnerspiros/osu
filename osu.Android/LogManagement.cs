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
            // NOTE: we used to force Logger.Level = LogLevel.Important here to
            // shrink runtime log output during the "log explosion" debugging
            // window. That has been reverted at user request — the default
            // framework log verbosity is now restored so osu.log captures the
            // full per-thread startup narrative we need to diagnose hangs.
            // Log size is still bounded by pruneLogDirectory() below
            // (MAX_LOG_BYTES cap with oldest-first eviction), so re-enabling
            // verbose logging cannot regress the on-disk footprint that the
            // 480 MB report originally exposed.

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
                files = new DirectoryInfo(logsDir).GetFiles("*.log", SearchOption.TopDirectoryOnly);
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
        /// If the on-disk <c>framework.ini</c> still has the framework's stale
        /// Android default of <c>ExecutionMode = SingleThread</c>, rewrite it
        /// to <c>MultiThreaded</c> in place before the framework loads it.
        ///
        /// <para>
        /// Background: <c>osu.Framework.Android.AndroidGameHost.SetupConfig</c>
        /// historically registered SingleThread as the framework default. The
        /// first launch of any older build therefore persisted SingleThread to
        /// disk. <c>OsuGameBase.load()</c> later force-sets MultiThreaded, but
        /// only *after* the host has already started in SingleThread for ~1
        /// second — the runtime log shows two consecutive
        /// "Execution mode changed to ..." entries, with the GameThread set
        /// being torn down and re-created in between. Rewriting the on-disk
        /// value here closes the gap on already-installed clients without
        /// requiring a "delete framework.ini" support instruction.
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

                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();

                    if (!string.Equals(key, "ExecutionMode", StringComparison.Ordinal))
                        continue;

                    if (string.Equals(value, "SingleThread", StringComparison.Ordinal))
                    {
                        lines[i] = "ExecutionMode = MultiThreaded";
                        changed = true;
                    }

                    break;
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

        // Sentinel file dropped after the one-shot Renderer-default migration has
        // run. Stored in the storage root next to framework.ini so a single
        // existence check governs whether we should respect the user's currently
        // persisted Renderer choice (sentinel present) or perform the one-time
        // Automatic→OpenGL nudge (sentinel absent).
        private const string renderer_migration_sentinel = "android_renderer_default_migrated.flag";

        /// <summary>
        /// One-shot migration that flips the framework default <c>Renderer</c>
        /// choice from <c>Automatic</c> (which resolves to Vulkan on Android,
        /// requiring runtime SPIR-V compilation via glslang) to <c>OpenGL</c>
        /// (which uses the Adreno driver's native GLSL compiler — no glslang,
        /// no SPIR-V, and therefore no shader-compile burst on Toolbar load).
        ///
        /// <para>
        /// Why this matters: every recent black-screen ANR fingerprint in the
        /// field tombstones (PIDs 27798 / 29226 / 499) shows a Veldrid worker
        /// stuck inside <c>glslang::TParseContext::executeInitializer</c> /
        /// <c>TShader::parse</c> at <c>nice=-10</c> on a big core, monopolising
        /// the CPU during the Toolbar texture-upload burst and starving the
        /// Update thread past the 10-second MotionEvent ANR deadline. Switching
        /// the default away from the Vulkan-via-glslang path eliminates the
        /// entire failure class on stock installs. Users who specifically want
        /// Vulkan can still select it from Settings → Graphics → Renderer; the
        /// migration only nudges the *default* and is recorded by an on-disk
        /// sentinel so subsequent launches never overwrite an explicit choice.
        /// </para>
        ///
        /// <para>
        /// Best-effort and never throws — if the file is missing or the rewrite
        /// fails, startup proceeds with the existing value. Must be invoked from
        /// <c>OsuGameActivity.OnCreate</c> BEFORE the framework reads
        /// framework.ini, alongside the existing
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

                string iniPath = Path.Combine(root, "framework.ini");

                if (!File.Exists(iniPath))
                {
                    // Brand-new install: no framework.ini yet. Pre-create a minimal
                    // file with just the Renderer line set; the framework will fill
                    // in its other defaults on first save.
                    try
                    {
                        File.WriteAllText(iniPath, "Renderer = OpenGL" + System.Environment.NewLine);
                        tryDropSentinel(sentinelPath);
                    }
                    catch (Exception e)
                    {
                        Debug.WriteLine($"[osu!] LogManagement: could not pre-create framework.ini: {e.Message}");
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
                    Debug.WriteLine($"[osu!] LogManagement: could not read framework.ini for renderer migration: {e.Message}");
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
                    string value = line.Substring(eq + 1).Trim();

                    if (!string.Equals(key, "Renderer", StringComparison.Ordinal))
                        continue;

                    seenRendererLine = true;

                    // Only nudge the default. If the user has explicitly chosen
                    // Vulkan / OpenGLLegacy / Direct3D11 / Metal / Deferred, leave
                    // it alone — the migration's job is to change the *default*,
                    // not overwrite intent.
                    if (string.Equals(value, "Automatic", StringComparison.Ordinal))
                    {
                        lines[i] = "Renderer = OpenGL";
                        changed = true;
                    }

                    break;
                }

                if (!seenRendererLine)
                {
                    // No Renderer line at all — append one at the end of the file.
                    var newLines = new string[lines.Length + 1];
                    Array.Copy(lines, newLines, lines.Length);
                    newLines[lines.Length] = "Renderer = OpenGL";
                    lines = newLines;
                    changed = true;
                }

                if (changed)
                {
                    try
                    {
                        File.WriteAllLines(iniPath, lines);
                        Logger.Log("[osu!] Android first-launch Renderer-default migration: Automatic → OpenGL", LoggingTarget.Performance);
                    }
                    catch (Exception e)
                    {
                        Debug.WriteLine($"[osu!] LogManagement: could not rewrite framework.ini for renderer migration: {e.Message}");
                        return;
                    }
                }

                // Drop sentinel regardless of whether we changed anything: the
                // migration has now had its one chance to run, and any subsequent
                // user choice (including a deliberate "Automatic") must be
                // respected.
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
