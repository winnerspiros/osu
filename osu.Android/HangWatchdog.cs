// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Debug = System.Diagnostics.Debug;
using osu.Framework.Platform;
using osu.Framework.Threading;

namespace osu.Android
{
    /// <summary>
    /// Per-GameThread liveness watchdog that detects multi-second stalls on the
    /// Update / Draw / Audio / Input threads and dumps a rich snapshot of every
    /// Linux thread in the process (comm, wchan, syscall, status) into the
    /// existing <c>native_crash.log</c>.
    ///
    /// <para>
    /// The dump is the actionable signal: <c>/proc/self/task/&lt;tid&gt;/wchan</c>
    /// names the kernel function each thread is waiting in, and
    /// <c>/proc/self/task/&lt;tid&gt;/syscall</c> gives the active syscall number
    /// plus the user-space PC. Together these pinpoint Vulkan present-queue
    /// stalls (futex on the GPU driver), Realm fifo waits, AAudio polls, GC
    /// pauses, etc., without needing adb access.
    /// </para>
    ///
    /// <para>
    /// The hang threshold is intentionally short (5s): the runtime log can grow
    /// to ~70MB on the user's device, so we'd rather over-dump than miss a
    /// stall, but we still rate-limit re-dumps of the same hang to one every
    /// 10s so we don't fill the log in a single second of frozen state.
    /// </para>
    /// </summary>
    internal static class HangWatchdog
    {
        // Threshold above which a thread is considered hung. Any GameThread that
        // fails to drain a queued no-op for this long triggers a snapshot.
        private const int hang_threshold_ms = 5_000;

        // Heartbeat scheduling cadence. Each game thread executes a no-op every
        // ~1s via Scheduler.AddDelayed(repeat: true) which updates its last-tick
        // timestamp; the monitor wakes at the same cadence to evaluate ages.
        private const int heartbeat_interval_ms = 1_000;

        // Minimum gap between two consecutive snapshots while still hung. Without
        // this, a 60s hang would generate 12 full /proc/self/task dumps and
        // potentially blow the log size cap in a few seconds.
        private const int redump_cooldown_ms = 10_000;

        // Maximum number of distinct hang dumps written for the lifetime of the
        // process. Prevents pathological "permanent hang plus runaway watchdog"
        // from filling the log indefinitely if the cooldown logic ever misbehaves.
        private const int max_dumps_per_process = 200;

        private static int started;
        private static Thread? monitorThread;
        private static readonly Heartbeat[] heartbeats = new Heartbeat[4];
        private static int dumpCount;

        // libc.gettid: returns the Linux kernel thread id of the calling thread.
        // We need this (not managed Thread.ManagedThreadId) to map heartbeats to
        // /proc/self/task/&lt;tid&gt;/* entries.
        [DllImport("libc", EntryPoint = "gettid", SetLastError = false)]
        private static extern int gettid();

        /// <summary>
        /// Begin watchdog monitoring against the four standard <see cref="GameHost"/>
        /// threads. Idempotent: a second call after the monitor is already running
        /// is a no-op. Safe to call from any thread; the monitor itself runs on a
        /// dedicated background OS thread that never enters managed game code.
        /// </summary>
        public static void Start(GameHost? host)
        {
            if (host == null) return;

            if (Interlocked.Exchange(ref started, 1) != 0)
                return;

            try
            {
                heartbeats[0] = new Heartbeat("Update", host.UpdateThread);
                heartbeats[1] = new Heartbeat("Draw", host.DrawThread);
                heartbeats[2] = new Heartbeat("Audio", host.AudioThread);
                heartbeats[3] = new Heartbeat("Input", host.InputThread);

                foreach (var hb in heartbeats)
                    hb.Arm();

                monitorThread = new Thread(monitorLoop)
                {
                    Name = "HangWatchdog",
                    IsBackground = true,
                };
                monitorThread.Start();

                CrashDiagnostics.WriteAliveMarker($"HangWatchdog.Start (threshold={hang_threshold_ms}ms, cooldown={redump_cooldown_ms}ms)");
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] HangWatchdog.Start failed: {e.Message}");
                Interlocked.Exchange(ref started, 0);
            }
        }

        private static void monitorLoop()
        {
            // Per-thread cooldown so each thread can dump independently without
            // starving the others (e.g. Audio hung 30s while Draw hangs at 50s
            // should still produce two distinct snapshots).
            long[] lastDumpUtcMs = new long[heartbeats.Length];

            while (true)
            {
                try
                {
                    Thread.Sleep(heartbeat_interval_ms);

                    if (dumpCount >= max_dumps_per_process)
                        continue;

                    long nowMs = nowUtcMs();

                    for (int i = 0; i < heartbeats.Length; i++)
                    {
                        var hb = heartbeats[i];
                        if (hb == null) continue;

                        long lastTickMs = Interlocked.Read(ref hb.LastTickUtcMs);
                        long armedAtMs = Interlocked.Read(ref hb.ArmedAtUtcMs);

                        // A thread that has never ticked yet is treated as hung
                        // once it has been armed for longer than the threshold —
                        // this catches startup deadlocks where the GameThread
                        // never actually starts running its Scheduler.
                        long referenceMs = lastTickMs > 0 ? lastTickMs : armedAtMs;
                        if (referenceMs <= 0) continue;

                        long ageMs = nowMs - referenceMs;
                        if (ageMs < hang_threshold_ms) continue;

                        if (nowMs - lastDumpUtcMs[i] < redump_cooldown_ms) continue;

                        lastDumpUtcMs[i] = nowMs;
                        dumpHang(hb, ageMs, lastTickMs > 0);

                        // Re-arm so that if the thread eventually recovers we
                        // start counting from the recovery point, not the start
                        // of the original hang.
                        hb.Arm();
                    }
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"[osu!] HangWatchdog monitor loop iteration failed: {e.Message}");
                }
            }
            // ReSharper disable once FunctionNeverReturns -- by design; monitor lives for the process.
        }

        private static void dumpHang(Heartbeat hb, long ageMs, bool everTicked)
        {
            int currentDump = Interlocked.Increment(ref dumpCount);

            try
            {
                var sb = new StringBuilder(16 * 1024);
                sb.Append("\n=========================================================\n");
                sb.Append("=== HANG WATCHDOG TRIGGER ===\n");
                sb.Append($"  utc_time     = {DateTime.UtcNow:O}\n");
                sb.Append($"  thread       = {hb.Name} (GameThread)\n");
                sb.Append($"  age_ms       = {ageMs}\n");
                sb.Append($"  ever_ticked  = {everTicked}\n");
                sb.Append($"  game_tid     = {Interlocked.Read(ref hb.LinuxTid)}\n");
                sb.Append($"  dump_index   = {currentDump}/{max_dumps_per_process}\n");
                sb.Append("\n--- Heartbeats ---\n");

                long now = nowUtcMs();
                foreach (var other in heartbeats)
                {
                    if (other == null) continue;

                    long t = Interlocked.Read(ref other.LastTickUtcMs);
                    long a = Interlocked.Read(ref other.ArmedAtUtcMs);
                    long otherAge = t > 0 ? now - t : (a > 0 ? now - a : -1);
                    sb.Append($"  {other.Name,-7} tid={Interlocked.Read(ref other.LinuxTid),-7} age_ms={otherAge,-7} ticks={Interlocked.Read(ref other.TickCount)}\n");
                }

                sb.Append("\n--- /proc/self/task snapshot ---\n");
                appendProcTaskSnapshot(sb);

                sb.Append("=== END OF HANG WATCHDOG TRIGGER ===\n\n");

                CrashDiagnostics.AppendDiagnosticBlock(sb.ToString());
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[osu!] HangWatchdog.dumpHang failed: {e.Message}");
            }
        }

        private static void appendProcTaskSnapshot(StringBuilder sb)
        {
            try
            {
                // /proc/self/task entries are subdirectories, not files, so
                // enumerate via Directory.EnumerateDirectories and extract the
                // numeric tid from each path leaf.
                var collected = new List<string>(64);
                try
                {
                    foreach (string dir in Directory.EnumerateDirectories("/proc/self/task"))
                    {
                        string leaf = Path.GetFileName(dir);
                        if (!string.IsNullOrEmpty(leaf))
                            collected.Add(leaf);
                    }
                }
                catch (Exception e)
                {
                    sb.Append($"  (failed to enumerate /proc/self/task: {e.Message})\n");
                    return;
                }

                collected.Sort(StringComparer.Ordinal);

                foreach (string tid in collected)
                {
                    string basePath = "/proc/self/task/" + tid;

                    string comm = readProcLine(basePath + "/comm", 64);
                    string wchan = readProcLine(basePath + "/wchan", 128);
                    string syscall = readProcLine(basePath + "/syscall", 256);
                    string state = parseStateFromStat(readProcLine(basePath + "/stat", 256));

                    sb.Append("  tid=").Append(tid)
                      .Append(" state=").Append(state)
                      .Append(" comm=").Append(comm)
                      .Append(" wchan=").Append(wchan)
                      .Append(" syscall=").Append(syscall)
                      .Append('\n');
                }
            }
            catch (Exception e)
            {
                sb.Append($"  (proc snapshot outer failure: {e.Message})\n");
            }
        }

        private static string readProcLine(string path, int maxLen)
        {
            try
            {
                // /proc files can change between open and read; tolerate short
                // reads and EAGAIN, and never throw out to the caller.
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                byte[] buf = new byte[maxLen];
                int n = fs.Read(buf, 0, buf.Length);
                if (n <= 0) return "<empty>";

                string s = Encoding.UTF8.GetString(buf, 0, n).Trim();
                // Replace newlines/control chars so we keep one tid per line in the dump.
                return s.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
            }
            catch (Exception e)
            {
                return "<err:" + e.GetType().Name + ">";
            }
        }

        private static string parseStateFromStat(string stat)
        {
            // /proc/<tid>/stat: "<pid> (comm) <state> ..."  The comm field can
            // contain parentheses and spaces, so locate the LAST ')' and read
            // the next non-space char as the state code (R/S/D/Z/T/...).
            if (string.IsNullOrEmpty(stat)) return "?";

            int rp = stat.LastIndexOf(')');
            if (rp < 0 || rp + 2 >= stat.Length) return "?";

            return stat.Substring(rp + 2, 1);
        }

        private static long nowUtcMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Captures all heartbeat state for one game thread. Fields are mutated
        // from both the monitor (read) and the game thread (write), all via
        // Interlocked to avoid torn 64-bit reads on 32-bit ABIs (we only ship
        // arm64-v8a today, but the explicit Interlocked also documents the
        // cross-thread contract).
        private sealed class Heartbeat
        {
            public readonly string Name;
            private readonly GameThread thread;
            public long LastTickUtcMs;
            public long ArmedAtUtcMs;
            public long LinuxTid;
            public long TickCount;

            public Heartbeat(string name, GameThread thread)
            {
                Name = name;
                this.thread = thread;
            }

            // Schedule a self-pinging recurring delegate that bumps the
            // heartbeat from the game thread itself. If the game thread is
            // hung, this delegate simply does not run, and LastTickUtcMs
            // stays stale — exactly the signal the monitor consumes.
            public void Arm()
            {
                Interlocked.Exchange(ref ArmedAtUtcMs, nowUtcMs());

                try
                {
                    thread.Scheduler.AddDelayed(tick, heartbeat_interval_ms, true);
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"[osu!] HangWatchdog.Heartbeat({Name}).Arm failed: {e.Message}");
                }
            }

            private void tick()
            {
                Interlocked.Exchange(ref LastTickUtcMs, nowUtcMs());

                // gettid is cheap (single syscall) and only meaningfully
                // changes on the very first tick — but we re-record it on
                // every tick so a thread restart (e.g. ExecutionMode swap)
                // is reflected without needing a re-Arm.
                try { Interlocked.Exchange(ref LinuxTid, gettid()); }
                catch { /* libc unavailable: leave as 0, dump still useful */ }

                Interlocked.Increment(ref TickCount);
            }
        }
    }
}
