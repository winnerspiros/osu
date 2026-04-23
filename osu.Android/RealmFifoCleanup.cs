// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using Debug = System.Diagnostics.Debug;

namespace osu.Android
{
    /// <summary>
    /// Best-effort cleanup of stale Realm cross-process notification fifos that
    /// can survive a previous-process crash and block the next process's
    /// <c>Realm.GetInstance()</c> in native code.
    ///
    /// <para>
    /// Background: <c>RealmAccess</c> sets <c>FallbackPipePath</c> to
    /// <c>Path.GetTempPath()/lazer</c> (see osu.Game/Database/RealmAccess.cs).
    /// Realm-core uses that directory for its inter-process change-notification
    /// FIFOs (named <c>realm_*.cv</c>, <c>realm_*.note</c>, <c>realm_*.lock</c>).
    /// If a previous process crashed mid-startup the fifo file is left behind on
    /// disk. The next launch may then block in <c>open()</c> on that fifo while
    /// holding a Realm runtime lock — exactly the all-threads-parked-in-sigsuspend
    /// pattern observed in the field. We delete fifos older than
    /// <see cref="STALE_AGE_SECONDS"/> seconds (a comfortable margin past anything
    /// that could be in active concurrent use by a still-running sibling process).
    /// </para>
    ///
    /// <para>
    /// Failure of any individual file delete is silently swallowed — diagnostics
    /// reliability semantics. Never throws.
    /// </para>
    /// </summary>
    internal static class RealmFifoCleanup
    {
        // Files newer than this are skipped to avoid racing a concurrently-active
        // Realm in another process. 5s comfortably exceeds the longest gap between
        // an open() and a write() on these fifos in normal Realm operation.
        private const int STALE_AGE_SECONDS = 5;

        // The patterns Realm uses for its cross-process notifier sockets/FIFOs.
        // Match exactly what realm-core writes to disk.
        private static readonly string[] fifo_patterns =
        {
            "realm_*.cv",
            "realm_*.note",
            "realm_*.lock",
        };

        /// <summary>
        /// Remove stale Realm fifos under <c>Path.GetTempPath()/lazer</c>.
        /// </summary>
        /// <returns>Number of files deleted (0 on any failure).</returns>
        public static int Run()
        {
            int deleted = 0;
            try
            {
                string tempPath = Path.GetTempPath();
                if (string.IsNullOrEmpty(tempPath))
                    return 0;

                string lazerPath = Path.Combine(tempPath, "lazer");
                if (!Directory.Exists(lazerPath))
                    return 0;

                DateTime cutoffUtc = DateTime.UtcNow.AddSeconds(-STALE_AGE_SECONDS);

                foreach (string pattern in fifo_patterns)
                {
                    string[] files;
                    try
                    {
                        files = Directory.GetFiles(lazerPath, pattern, SearchOption.TopDirectoryOnly);
                    }
                    catch (Exception e)
                    {
                        Debug.WriteLine($"[osu!] RealmFifoCleanup.Run enumerate({pattern}) failed: {e.Message}");
                        continue;
                    }

                    foreach (string file in files)
                    {
                        try
                        {
                            // Use FileInfo.LastWriteTimeUtc (not File.GetLastWriteTimeUtc) so a
                            // missing-file race after enumeration just throws and we move on.
                            var info = new FileInfo(file);
                            if (info.LastWriteTimeUtc > cutoffUtc)
                                continue; // young — possibly in active use by a concurrent process

                            info.Delete();
                            deleted++;
                        }
                        catch (Exception e)
                        {
                            // Most likely "file in use" / EBUSY because another process
                            // does have it open. Skip silently — this is best-effort.
                            Debug.WriteLine($"[osu!] RealmFifoCleanup.Run delete({file}) failed: {e.Message}");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] RealmFifoCleanup.Run outer failure: {e.Message}");
            }
            return deleted;
        }
    }
}
