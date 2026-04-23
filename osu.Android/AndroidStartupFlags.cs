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
    }
}
