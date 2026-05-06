// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using Android.App;
using Debug = System.Diagnostics.Debug;

namespace osu.Android
{
    /// <summary>
    /// Tiny on-disk sentinel store that lets pre-<see cref="osu.Game.OsuGameBase"/> code
    /// (e.g. <see cref="OsuGameActivity.OnCreate"/>) consult settings owned by
    /// <see cref="osu.Game.Configuration.OsuConfigManager"/>.
    ///
    /// <para>
    /// The activity runs LONG before the config manager exists — Realm has to be
    /// initialised first, and the activity is the place that needs to RUN safety
    /// behaviours BEFORE Realm runs. The chicken-and-egg solution is a one-flag-
    /// per-file sentinel pattern: when the user changes a startup-safety toggle
    /// in-game, <see cref="OsuGameAndroid"/> writes (or deletes) a sentinel file
    /// whose presence reflects the new value. The activity reads the sentinel
    /// next launch.
    /// </para>
    ///
    /// <para>
    /// Files live under <c>FilesDir</c> (internal app storage). Each flag is a
    /// single empty file named <c>android_startup_disable_&lt;name&gt;.flag</c>.
    /// Presence ⇒ "the user has explicitly disabled this safety net". Absence ⇒
    /// "default behaviour" (per the matching OsuSetting default in
    /// <see cref="osu.Game.Configuration.OsuConfigManager"/>).
    /// </para>
    ///
    /// <para>
    /// All operations are best-effort and never throw out — diagnostics-grade
    /// reliability semantics, like <see cref="CrashDiagnostics"/>.
    /// </para>
    /// </summary>
    internal static class AndroidStartupFlags
    {
        public const string FLAG_CLEANUP_REALM_FIFOS_DISABLED = "android_startup_disable_realm_fifo_cleanup.flag";
        public const string FLAG_DEFER_NATIVE_INIT_DISABLED = "android_startup_disable_defer_native_init.flag";
        public const string FLAG_FRAME_SYNC_MIGRATION_ENABLED = "android_startup_enable_frame_sync_migration.flag";

        /// <summary>
        /// Verbose-logging opt-in sentinel. Presence ⇒ "user has enabled
        /// verbose framework logging". Absence ⇒ "default, quiet logging
        /// (Important+ only)". Quiet is the default because the framework's
        /// runtime/input log is ~330+ KB per launch on Android (mostly
        /// OpenTabletDriver detection + SDL platform chatter) and is not
        /// useful in the steady state. Toggle from
        /// Settings → Graphics → Android Performance.
        /// </summary>
        public const string FLAG_VERBOSE_LOGGING_ENABLED = "android_startup_enable_verbose_logging.flag";

        /// <summary>
        /// BASS AAudio opt-in sentinel. Presence ⇒ "user has enabled BASS AAudio output".
        /// Absence ⇒ "default — BASS uses AudioTrack".
        /// When set, <see cref="OsuGameActivity.OnCreate"/> calls <c>Bass.AndroidAAudio = true</c>
        /// and <c>Bass.DevicePeriod = -512</c> before any BASS initialisation, so BASS opens
        /// an AAudio device instead of AudioTrack. On Android ≥ 8.0 this gives lower intrinsic
        /// BASS output latency; on older devices BASS falls back to AudioTrack automatically.
        /// Orthogonal to the Oboe bridge (<see cref="osu.Game.Configuration.OsuSetting.AndroidLowLatencyAudio"/>):
        /// when Oboe is also enabled it overrides BASS's output backend entirely via the
        /// GlobalMixerHandle decode-only path, so this flag only has a perceptible effect
        /// when Oboe is disabled.
        /// </summary>
        public const string FLAG_BASS_AAUDIO_ENABLED = "android_startup_enable_bass_aaudio.flag";

        /// <summary>
        /// "Startup in progress" sentinel. Dropped near the very top of <see cref="OsuGameActivity.OnCreate"/>
        /// and cleared a few seconds after <c>OsuGame.LoadComplete</c> by <see cref="OsuGameAndroid"/>.
        /// If a fresh <c>OnCreate</c> finds this still present, the previous launch died (ANR / native
        /// crash / OOM kill) before reaching the post-LoadComplete clear point, and we apply one-shot
        /// safe-mode behaviours for THIS launch only.
        /// </summary>
        public const string FLAG_STARTUP_IN_PROGRESS = "android_startup_in_progress.flag";

        /// <summary>
        /// Stores the renderer value that was overwritten by <see cref="LogManagement.ForceOpenGLRendererIfSafeMode"/>.
        /// On the next successful launch, <see cref="LogManagement.RestoreRendererAfterSafeMode"/> reads this,
        /// restores the renderer in <c>framework.ini</c>, and deletes the file so the restore only happens once.
        /// This makes the safe-mode OpenGL fallback a single-launch rescue rather than a permanent override.
        /// </summary>
        public const string FLAG_SAFE_MODE_RENDERER_RESTORE = "android_safe_mode_renderer_restore.flag";

        /// <summary>
        /// Stores the fingerprint (uptime_ns + pid) of the most recent native crash for which
        /// <see cref="AndroidStartupSafeMode"/> already applied a one-shot Draw-thread-crash
        /// safe-mode latch. Lets us detect "previous launch crashed on the Draw thread" via
        /// <c>native_crash.log</c> without re-triggering safe-mode on every subsequent launch
        /// for the same crash event. Value sentinel (file contents matter, not just presence).
        /// </summary>
        public const string FLAG_LAST_NATIVE_CRASH_CONSUMED = "android_last_native_crash_consumed.flag";

        private static string? resolveDir()
        {
            try
            {
                var ctx = Application.Context;
                var files = ctx?.FilesDir;
                if (files == null) return null;
                string? path = files.AbsolutePath;
                return string.IsNullOrEmpty(path) ? null : path;
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] AndroidStartupFlags.resolveDir failed: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Returns true if a sentinel file with the given name exists in internal app storage.
        /// </summary>
        public static bool IsSet(string flagName)
        {
            try
            {
                string? dir = resolveDir();
                if (dir == null) return false;
                return File.Exists(Path.Combine(dir, flagName));
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] AndroidStartupFlags.IsSet({flagName}) failed: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Create or remove the sentinel file according to <paramref name="set"/>.
        /// Idempotent and never throws.
        /// </summary>
        public static void Set(string flagName, bool set)
        {
            try
            {
                string? dir = resolveDir();
                if (dir == null) return;
                string path = Path.Combine(dir, flagName);
                if (set)
                {
                    if (!File.Exists(path))
                        File.WriteAllText(path, string.Empty);
                }
                else
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] AndroidStartupFlags.Set({flagName}, {set}) failed: {e.Message}");
            }
        }

        /// <summary>
        /// Read the textual contents of a value-sentinel file. Returns <c>null</c> if the
        /// file does not exist, the storage directory cannot be resolved, or any I/O error
        /// occurs. Used by callers that need a non-boolean value (e.g. a fingerprint) and
        /// want it to survive across process launches. Never throws.
        /// </summary>
        public static string? ReadValue(string flagName)
        {
            try
            {
                string? dir = resolveDir();
                if (dir == null) return null;
                string path = Path.Combine(dir, flagName);
                if (!File.Exists(path)) return null;
                return File.ReadAllText(path);
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] AndroidStartupFlags.ReadValue({flagName}) failed: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Write <paramref name="value"/> as the contents of a value-sentinel file (overwriting
        /// any prior content). Companion to <see cref="ReadValue"/>. Never throws.
        /// </summary>
        public static void WriteValue(string flagName, string value)
        {
            try
            {
                string? dir = resolveDir();
                if (dir == null) return;
                string path = Path.Combine(dir, flagName);
                File.WriteAllText(path, value);
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] AndroidStartupFlags.WriteValue({flagName}) failed: {e.Message}");
            }
        }
    }
}
