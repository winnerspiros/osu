// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Native pthread-based liveness watchdog for osu!lazer Android.
//
// Why this exists
// ===============
// The managed `HangWatchdog` (osu.Android/HangWatchdog.cs) runs as a regular
// `System.Threading.Thread`.  Mono's stop-the-world GC suspends every managed
// thread by sending `SIGRTMIN+N` and parking the target in `sigsuspend`.  If a
// Mono thread is stuck inside a long native call (Vulkan present-queue futex,
// Realm fifo open, BASS_Init blocking on AAudio, Adreno shader-cache mmap on a
// 100k-file directory, …) and never reaches a GC safepoint, the STW request
// never completes, and *every* other managed thread — including our managed
// watchdog's monitor — is suspended indefinitely.  The result is exactly what
// we observe in the field: 14+ s of complete silence after `HangWatchdog.Start`
// logs its banner, followed by an Android ANR with every visible thread parked
// in `__rt_sigsuspend`, and no `HANG WATCHDOG TRIGGER` block ever produced.
//
// This file installs a pthread that runs entirely outside the Mono universe.
// It does not call into managed code, does not allocate, does not take any
// Mono lock, and does not register itself with `mono_thread_attach`.  Mono has
// no list entry for it, so STW never sends it the suspend signal — meaning it
// continues to wake every second regardless of GC state and can write a dump
// telling us *which* thread holds the safepoint.
//
// Async-signal safety
// -------------------
// We deliberately use only the same primitive set as `crash_handler.cpp`:
//   open(2)/read(2)/write(2)/close(2)/lseek(2)/getdents64(2)/clock_gettime(2)/
//   clock_nanosleep(2)/__android_log_write.  No malloc, no stdio, no string.h
//   except `memcpy`.  All formatting is done with the same tiny `writeHex`/
//   `writeDec` helpers used by `crash_handler.cpp`, but reimplemented locally
//   so this TU has no link dependency on that one.
//
// Bounded output
// --------------
// We cap dumps at `kMaxDumps` per process and rate-limit re-dumps to one per
// `kRedumpCooldownSec`, so a permanent hang does not blow the
// `native_crash.log` budget (which `CrashDiagnostics.cs` rotates at 3 MiB).
// `/proc/self/maps` is appended only on the first trigger because it is by far
// the largest payload (≈300 KB on a typical osu! Android process).

#include "native_watchdog.h"

#include <pthread.h>
#include <unistd.h>
#include <fcntl.h>
#include <signal.h>
#include <sys/syscall.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <stdint.h>
#include <string.h>
#include <time.h>
#include <errno.h>
#include <android/log.h>

#define WATCHDOG_LOG_TAG "osu!watchdog"

namespace {

// --------------------------------------------------------------------------
// Configuration / state.
// --------------------------------------------------------------------------

constexpr size_t kMaxLogPathLen = 1024;
char g_logPath[kMaxLogPathLen] = {};

// Default 10s hang threshold; overridden by the value the C# side passes to
// osu_native_watchdog_start().  Clamped to [3, 120] s in start().
volatile int32_t g_hangSeconds = 10;

// Monotonic seconds at which the most recent managed heartbeat was observed.
// Written from C# via osu_native_watchdog_heartbeat() (atomic store) and read
// from the watchdog pthread (atomic load).  Initialised to 0 by the loader,
// which the watchdog interprets as "not yet ticked" and uses the start time
// as a reference instead.
volatile uint64_t g_lastHeartbeatMonotonicSec = 0;

// Monotonic seconds at which the watchdog started.  Used as a reference point
// for "armed but never ticked" hangs (e.g. the Update thread never runs at
// all because Mono is stuck mid-GC before tick() can be scheduled).
volatile uint64_t g_startMonotonicSec = 0;

// One-shot guard for osu_native_watchdog_start (idempotent).
volatile sig_atomic_t g_started = 0;

// Hard cap on the number of dumps the watchdog will ever produce in one
// process lifetime.  Mirrors the managed HangWatchdog cap.
constexpr int kMaxDumps = 5;
volatile int g_dumpCount = 0;

// Minimum gap between two consecutive dumps.  Without this a permanent hang
// would trigger every (g_hangSeconds + 1) s.
constexpr uint64_t kRedumpCooldownSec = 30;
volatile uint64_t g_lastDumpMonotonicSec = 0;

// Kill threshold multiplier: process is killed after hangSeconds * this when
// no managed heartbeat has EVER been observed (renderer init hung).
constexpr uint64_t kKillThresholdMultiplier = 2;

// --------------------------------------------------------------------------
// Async-signal-safe formatters / I/O.
// --------------------------------------------------------------------------

inline ssize_t safeWrite(int fd, const void* buf, size_t len)
{
    if (fd < 0 || buf == nullptr || len == 0) return 0;
    const uint8_t* p = static_cast<const uint8_t*>(buf);
    size_t remaining = len;
    while (remaining > 0)
    {
        ssize_t n = write(fd, p, remaining);
        if (n < 0)
        {
            if (errno == EINTR) continue;
            return -1;
        }
        if (n == 0) break;
        p += n;
        remaining -= (size_t)n;
    }
    return (ssize_t)(len - remaining);
}

inline void writeStr(int fd, const char* s)
{
    if (s == nullptr) return;
    size_t len = 0;
    while (s[len] != '\0') ++len;
    (void)safeWrite(fd, s, len);
}

inline void writeDec(int fd, long long v)
{
    char buf[32];
    int pos = (int)sizeof(buf);
    bool negative = false;
    if (v < 0) { negative = true; v = -v; }
    if (v == 0) buf[--pos] = '0';
    while (v > 0 && pos > 0) { buf[--pos] = (char)('0' + (v % 10)); v /= 10; }
    if (negative && pos > 0) buf[--pos] = '-';
    (void)safeWrite(fd, buf + pos, sizeof(buf) - (size_t)pos);
}

// Hard cap on `g_logPath` size enforced from the native side. Mirrors the
// 3 MiB cap CrashDiagnostics.cs uses on its managed write paths. The
// native watchdog runs OUTSIDE Mono, so it cannot share the managed
// rotation logic — without an independent cap, a pre-existing oversized
// log left by an older build (or a tight ANR-restart loop that never gives
// the managed side a chance to rotate) grows without bound through the
// O_APPEND writes below. 4× cap is the same runaway threshold
// CrashDiagnostics.rotation_runaway_multiplier uses.
constexpr off_t kNativeLogCapBytes = 3LL * 1024 * 1024;
constexpr off_t kNativeLogRunawayCapBytes = kNativeLogCapBytes * 4;

// Open the configured log path for append.  Returns -1 if no path is set or
// open fails.  Mode 0644 mirrors what the rest of the diagnostics pipeline
// uses for native_crash.log.
//
// Before opening, fstat the existing file: if it is over the runaway cap,
// unlink it so the subsequent open(O_CREAT) starts a fresh file. We pick the
// runaway cap (rather than the soft cap) because under ordinary operation
// CrashDiagnostics.rotateIfTooLarge handles rotation at the soft cap; we only
// need to act when the managed side cannot or has not run yet.
//
// Async-signal safe: open/unlink/fstat are all safe to call from a signal
// handler (which is where this is invoked from crash_handler.cpp).
inline int openLogAppend()
{
    if (g_logPath[0] == '\0') return -1;

    // Best-effort runaway check. Errors fall through silently to the open()
    // below: a missing file (ENOENT) is the normal case before first write,
    // and any other stat error means we cannot judge the size — let the
    // open proceed and rely on the managed rotation on the next startup.
    struct stat st{};
    if (stat(g_logPath, &st) == 0 && S_ISREG(st.st_mode) && st.st_size > kNativeLogRunawayCapBytes)
    {
        // Best-effort unlink. If we cannot unlink (EACCES on a FUSE mount),
        // we still proceed to open in append mode — at worst we add another
        // bounded write to the existing oversized file rather than producing
        // no diagnostic at all.
        (void)unlink(g_logPath);
    }

    int fd = open(g_logPath, O_WRONLY | O_APPEND | O_CREAT | O_CLOEXEC, 0644);
    return fd; // -1 ok, caller checks
}

inline uint64_t monotonicSec()
{
    struct timespec ts{};
    if (clock_gettime(CLOCK_MONOTONIC, &ts) != 0) return 0;
    return (uint64_t)ts.tv_sec;
}

// Copy a small file (e.g. /proc/<tid>/comm) into out[], truncating any
// trailing newline/control chars.  Returns number of bytes written into out
// (no NUL terminator).  Never throws.
inline ssize_t readSmallProcFile(const char* path, char* out, size_t cap)
{
    int fd = open(path, O_RDONLY | O_CLOEXEC);
    if (fd < 0) return -1;
    ssize_t n;
    do { n = read(fd, out, cap); } while (n < 0 && errno == EINTR);
    close(fd);
    if (n <= 0) return n;

    // Strip trailing whitespace/newlines and replace internal newlines/tabs
    // with spaces so each emitted record stays single-line.
    while (n > 0 && (out[n - 1] == '\n' || out[n - 1] == '\r' || out[n - 1] == '\t' || out[n - 1] == ' '))
        --n;
    for (ssize_t i = 0; i < n; ++i)
    {
        if (out[i] == '\n' || out[i] == '\r' || out[i] == '\t') out[i] = ' ';
    }
    return n;
}

// Write a buffer of size n to fd, replacing it with "<empty>" when n <= 0.
inline void writeBufOrEmpty(int fd, const char* buf, ssize_t n)
{
    if (n <= 0) writeStr(fd, "<empty>");
    else (void)safeWrite(fd, buf, (size_t)n);
}

// /proc/<tid>/stat:  "<pid> (comm) <state> <ppid> ..."
// `comm` may contain spaces/parens, so we extract `state` by finding the
// LAST ')' in the file and reading the next non-space char.
inline char parseStateFromStat(const char* buf, ssize_t n)
{
    if (n <= 0) return '?';
    ssize_t rp = -1;
    for (ssize_t i = n - 1; i >= 0; --i) { if (buf[i] == ')') { rp = i; break; } }
    if (rp < 0 || rp + 2 >= n) return '?';
    return buf[rp + 2];
}

// Read /proc/self/task and emit one line per tid:
//   tid=<tid> state=<X> comm=<...> wchan=<...> syscall=<...>
//
// We use getdents64 directly because /proc directories are populated lazily
// and `opendir` would still have to read them in chunks; doing it ourselves
// is signal-safer (no malloc) and equivalent in practice.
struct Dirent64
{
    uint64_t d_ino;
    int64_t  d_off;
    uint16_t d_reclen;
    uint8_t  d_type;
    char     d_name[];
};

void appendProcTaskSnapshot(int fd)
{
    int dfd = open("/proc/self/task", O_RDONLY | O_DIRECTORY | O_CLOEXEC);
    if (dfd < 0)
    {
        writeStr(fd, "  (failed to open /proc/self/task: errno=");
        writeDec(fd, errno);
        writeStr(fd, ")\n");
        return;
    }

    char dentBuf[4096];
    char comm[64];
    char wchan[128];
    char syscallBuf[256];
    char stat[256];
    char path[64];

    for (;;)
    {
        long n = syscall(SYS_getdents64, dfd, dentBuf, (int)sizeof(dentBuf));
        if (n < 0)
        {
            if (errno == EINTR) continue;
            writeStr(fd, "  (getdents64 failed: errno=");
            writeDec(fd, errno);
            writeStr(fd, ")\n");
            break;
        }
        if (n == 0) break;

        long off = 0;
        while (off < n)
        {
            auto* e = reinterpret_cast<Dirent64*>(dentBuf + off);
            off += e->d_reclen;

            const char* name = e->d_name;
            if (name[0] == '.' && (name[1] == '\0' || (name[1] == '.' && name[2] == '\0')))
                continue;
            // Only numeric tids.
            bool isTid = name[0] != '\0';
            for (size_t i = 0; name[i] != '\0' && isTid; ++i)
                if (name[i] < '0' || name[i] > '9') isTid = false;
            if (!isTid) continue;

            // Build "/proc/self/task/<tid>/<file>" into `path`.
            // Tids are at most 7 chars on Linux, so this comfortably fits.
            size_t pos = 0;
            const char* base = "/proc/self/task/";
            while (base[pos] != '\0') { path[pos] = base[pos]; ++pos; }
            for (size_t i = 0; name[i] != '\0' && pos < sizeof(path) - 16; ++i, ++pos)
                path[pos] = name[i];
            // Save the position after "<tid>" so we can append /<file> below.
            size_t tidEnd = pos;

            // /comm
            path[tidEnd] = '/'; path[tidEnd + 1] = 'c'; path[tidEnd + 2] = 'o';
            path[tidEnd + 3] = 'm'; path[tidEnd + 4] = 'm'; path[tidEnd + 5] = '\0';
            ssize_t commN = readSmallProcFile(path, comm, sizeof(comm));

            // /wchan
            path[tidEnd] = '/'; path[tidEnd + 1] = 'w'; path[tidEnd + 2] = 'c';
            path[tidEnd + 3] = 'h'; path[tidEnd + 4] = 'a'; path[tidEnd + 5] = 'n';
            path[tidEnd + 6] = '\0';
            ssize_t wchanN = readSmallProcFile(path, wchan, sizeof(wchan));

            // /syscall
            path[tidEnd] = '/'; path[tidEnd + 1] = 's'; path[tidEnd + 2] = 'y';
            path[tidEnd + 3] = 's'; path[tidEnd + 4] = 'c'; path[tidEnd + 5] = 'a';
            path[tidEnd + 6] = 'l'; path[tidEnd + 7] = 'l'; path[tidEnd + 8] = '\0';
            ssize_t scN = readSmallProcFile(path, syscallBuf, sizeof(syscallBuf));

            // /stat (for state byte only)
            path[tidEnd] = '/'; path[tidEnd + 1] = 's'; path[tidEnd + 2] = 't';
            path[tidEnd + 3] = 'a'; path[tidEnd + 4] = 't'; path[tidEnd + 5] = '\0';
            ssize_t stN = readSmallProcFile(path, stat, sizeof(stat));
            char state = parseStateFromStat(stat, stN);

            writeStr(fd, "  tid=");
            writeStr(fd, name);
            writeStr(fd, " state=");
            char stateStr[2] = { state, '\0' };
            writeStr(fd, stateStr);
            writeStr(fd, " comm=");
            writeBufOrEmpty(fd, comm, commN);
            writeStr(fd, " wchan=");
            writeBufOrEmpty(fd, wchan, wchanN);
            writeStr(fd, " syscall=");
            writeBufOrEmpty(fd, syscallBuf, scN);
            writeStr(fd, "\n");

            // Optional kernel stack for game threads.  /proc/<tid>/stack is
            // root-only on most production Android builds; we attempt the open
            // best-effort and silently skip on EACCES.
            if (commN > 0)
            {
                bool isGameThread = false;
                // Match common Mono / SDL / framework thread comms.  comm is
                // truncated to TASK_COMM_LEN (16) by the kernel.
                static const char* kInteresting[] = {
                    "SDLActivity",   // sys-tid 1, the SDL/managed UI thread
                    "Thread-",       // generic Mono managed threads (also Update/Draw/Audio)
                    "UpdateThread",
                    "DrawThread",
                    "AudioThread",
                    "InputThread",
                    "mono",
                    "FinalizerDaem",
                    nullptr,
                };
                for (size_t i = 0; kInteresting[i] != nullptr && !isGameThread; ++i)
                {
                    size_t needleLen = 0; while (kInteresting[i][needleLen] != '\0') ++needleLen;
                    if ((size_t)commN >= needleLen)
                    {
                        bool match = true;
                        for (size_t j = 0; j < needleLen; ++j)
                            if (comm[j] != kInteresting[i][j]) { match = false; break; }
                        if (match) isGameThread = true;
                    }
                }
                if (isGameThread)
                {
                    char stackBuf[2048];
                    path[tidEnd] = '/'; path[tidEnd + 1] = 's'; path[tidEnd + 2] = 't';
                    path[tidEnd + 3] = 'a'; path[tidEnd + 4] = 'c'; path[tidEnd + 5] = 'k';
                    path[tidEnd + 6] = '\0';
                    int sfd = open(path, O_RDONLY | O_CLOEXEC);
                    if (sfd >= 0)
                    {
                        ssize_t sn;
                        do { sn = read(sfd, stackBuf, sizeof(stackBuf)); } while (sn < 0 && errno == EINTR);
                        close(sfd);
                        if (sn > 0)
                        {
                            writeStr(fd, "    stack:\n");
                            // Indent each line of the kernel stack to keep it
                            // grep-able alongside the per-tid summary above.
                            ssize_t lineStart = 0;
                            for (ssize_t i = 0; i < sn; ++i)
                            {
                                if (stackBuf[i] == '\n')
                                {
                                    writeStr(fd, "      ");
                                    (void)safeWrite(fd, stackBuf + lineStart, (size_t)(i - lineStart + 1));
                                    lineStart = i + 1;
                                }
                            }
                            if (lineStart < sn)
                            {
                                writeStr(fd, "      ");
                                (void)safeWrite(fd, stackBuf + lineStart, (size_t)(sn - lineStart));
                                writeStr(fd, "\n");
                            }
                        }
                    }
                    // EACCES is normal — kernel.yama / production builds.
                }
            }
        }
    }

    close(dfd);
}

// Stream /proc/self/maps verbatim into fd.  Bounded by a 64 KiB cap so a
// process with an unusually fragmented address space cannot blow the
// native_crash.log budget on its own.
void appendProcSelfMaps(int fd)
{
    int mfd = open("/proc/self/maps", O_RDONLY | O_CLOEXEC);
    if (mfd < 0)
    {
        writeStr(fd, "  (failed to open /proc/self/maps: errno=");
        writeDec(fd, errno);
        writeStr(fd, ")\n");
        return;
    }

    constexpr size_t kCap = 64 * 1024;
    size_t written = 0;
    char buf[4096];
    for (;;)
    {
        ssize_t n;
        do { n = read(mfd, buf, sizeof(buf)); } while (n < 0 && errno == EINTR);
        if (n <= 0) break;
        size_t toWrite = (size_t)n;
        if (written + toWrite > kCap) toWrite = kCap - written;
        if (toWrite > 0)
        {
            (void)safeWrite(fd, buf, toWrite);
            written += toWrite;
        }
        if (written >= kCap)
        {
            writeStr(fd, "\n  (… /proc/self/maps truncated at 64 KiB)\n");
            break;
        }
    }
    close(mfd);
}

// Produce one full hang dump to logcat + log file.
void writeHangDump(uint64_t nowSec, uint64_t lastTickSec, uint64_t ageSec, bool firstDump)
{
    int currentDump = ++g_dumpCount; // not strictly atomic — single-writer thread

    int fd = openLogAppend();

    writeStr(fd, "\n=========================================================\n");
    writeStr(fd, "=== NATIVE WATCHDOG TRIGGER ===\n");
    writeStr(fd, "  monotonic_s    = "); writeDec(fd, (long long)nowSec);          writeStr(fd, "\n");
    writeStr(fd, "  last_tick_s    = "); writeDec(fd, (long long)lastTickSec);     writeStr(fd, "\n");
    writeStr(fd, "  age_s          = "); writeDec(fd, (long long)ageSec);          writeStr(fd, "\n");
    writeStr(fd, "  start_s        = "); writeDec(fd, (long long)g_startMonotonicSec); writeStr(fd, "\n");
    writeStr(fd, "  hang_threshold = "); writeDec(fd, (long long)g_hangSeconds);   writeStr(fd, "\n");
    writeStr(fd, "  dump_index     = "); writeDec(fd, currentDump);                writeStr(fd, "/");
    writeDec(fd, kMaxDumps);                                                       writeStr(fd, "\n");
    writeStr(fd, "  process_pid    = "); writeDec(fd, (long long)getpid());        writeStr(fd, "\n");
    writeStr(fd, "  watchdog_tid   = "); writeDec(fd, (long long)gettid());        writeStr(fd, "\n");
    writeStr(fd, "  reason         = ");
    if (lastTickSec == 0)
        writeStr(fd, "no managed heartbeat ever observed (Update thread did not tick)");
    else
        writeStr(fd, "managed heartbeat is stale");
    writeStr(fd, "\n");

    writeStr(fd, "\n--- /proc/self/task snapshot ---\n");
    appendProcTaskSnapshot(fd);

    if (firstDump)
    {
        writeStr(fd, "\n--- /proc/self/maps (first trigger only) ---\n");
        appendProcSelfMaps(fd);
    }
    else
    {
        writeStr(fd, "\n  (/proc/self/maps omitted on subsequent triggers — see first dump)\n");
    }

    writeStr(fd, "=== END OF NATIVE WATCHDOG TRIGGER ===\n\n");

    if (fd >= 0) close(fd);

    __android_log_write(ANDROID_LOG_ERROR, WATCHDOG_LOG_TAG,
        "NATIVE WATCHDOG TRIGGER — see native_crash.log for /proc dump");
}

// --------------------------------------------------------------------------
// The watchdog pthread.
// --------------------------------------------------------------------------

void* watchdogMain(void* /*arg*/)
{
    // Make sure no Mono signal can land on us.  Block all real-time signals
    // (SIGRTMIN..SIGRTMAX) plus SIGUSR1/2 — that's the range Mono uses for
    // its STW suspend/resume.  This is belt-and-braces: because we never
    // attach to Mono, it does not know about us, so it should not target us
    // anyway, but blocking the signals locally makes that guarantee
    // independent of any future Mono behaviour change.
    sigset_t blocked;
    sigemptyset(&blocked);
    for (int s = SIGRTMIN; s <= SIGRTMAX; ++s) sigaddset(&blocked, s);
    sigaddset(&blocked, SIGUSR1);
    sigaddset(&blocked, SIGUSR2);
    pthread_sigmask(SIG_BLOCK, &blocked, nullptr);

    __android_log_print(ANDROID_LOG_INFO, WATCHDOG_LOG_TAG,
        "watchdog thread up (tid=%d, hang_threshold=%ds)",
        (int)gettid(), (int)g_hangSeconds);

    // Kill threshold: if no managed heartbeat has EVER been observed (meaning
    // the game threads were never created — renderer init hung) and this
    // condition persists for 2× the hang threshold (default: 20s with 10s
    // threshold), kill the process. This triggers the safe-mode system on the
    // next launch (FLAG_STARTUP_IN_PROGRESS remains on disk → next launch
    // forces OpenGL via ForceOpenGLRendererIfSafeMode). The 2× multiplier
    // gives the renderer a generous window: the first dump fires at 1×
    // threshold for diagnostics, then we wait one more threshold period before
    // concluding the hang is unrecoverable.
    const uint64_t killThresholdSec = (uint64_t)g_hangSeconds * kKillThresholdMultiplier;

    for (;;)
    {
        struct timespec req{};
        req.tv_sec = 1;
        req.tv_nsec = 0;
        // CLOCK_MONOTONIC TIMER_RELATIVE → not affected by wall-clock jumps.
        // We accept early wakeups (EINTR) silently and re-loop.
        (void)clock_nanosleep(CLOCK_MONOTONIC, 0, &req, nullptr);

        uint64_t now = monotonicSec();
        if (now == 0) continue;

        uint64_t lastTick = __atomic_load_n(&g_lastHeartbeatMonotonicSec, __ATOMIC_ACQUIRE);

        // Reference is the most recent heartbeat if we ever saw one;
        // otherwise the watchdog start time (so an "armed but never ticked"
        // managed runtime still trips after hang_threshold seconds).
        uint64_t reference = lastTick > 0 ? lastTick : g_startMonotonicSec;
        if (reference == 0 || now < reference) continue;

        uint64_t age = now - reference;
        if (age < (uint64_t)g_hangSeconds) continue;

        // Kill the process if no heartbeat was EVER observed and we have
        // exceeded the kill threshold. This means the renderer initialization
        // (typically GraphicsDevice.CreateVulkan) hung in native driver code
        // and game threads were never created. Killing triggers safe-mode on
        // the next launch, which falls back to OpenGL.
        if (lastTick == 0 && age >= killThresholdSec)
        {
            // Write a final diagnostic before killing.
            int fd = openLogAppend();
            if (fd >= 0)
            {
                writeStr(fd, "\n=========================================================\n");
                writeStr(fd, "=== NATIVE WATCHDOG KILL ===\n");
                writeStr(fd, "  reason         = renderer init hang (no heartbeat ever observed after ");
                writeDec(fd, (long long)age);
                writeStr(fd, "s)\n");
                writeStr(fd, "  action         = killing process for safe-mode restart (OpenGL fallback)\n");
                writeStr(fd, "  kill_threshold = ");
                writeDec(fd, (long long)killThresholdSec);
                writeStr(fd, "s\n");
                writeStr(fd, "=== END NATIVE WATCHDOG KILL ===\n\n");
                close(fd);
            }

            __android_log_write(ANDROID_LOG_ERROR, WATCHDOG_LOG_TAG,
                "NATIVE WATCHDOG KILL — renderer init hung, killing for safe-mode OpenGL restart");

            // Use _exit to terminate immediately without running atexit handlers
            // or C++ destructors — the process is in an unrecoverable state.
            _exit(1);
        }

        if (g_dumpCount >= kMaxDumps) continue;

        if (g_lastDumpMonotonicSec != 0 && now - g_lastDumpMonotonicSec < kRedumpCooldownSec)
            continue;

        bool firstDump = g_lastDumpMonotonicSec == 0;
        g_lastDumpMonotonicSec = now;
        writeHangDump(now, lastTick, age, firstDump);
    }
    // Unreachable: watchdog runs for the life of the process.
}

} // namespace

// --------------------------------------------------------------------------
// Public entry points (declared in native_watchdog.h).
// --------------------------------------------------------------------------

extern "C" {

__attribute__((visibility("default")))
void osu_native_watchdog_start(const char* logPath, int32_t hangSeconds)
{
    if (g_started) return;

    // Clamp threshold to a sane range.
    if (hangSeconds < 3) hangSeconds = 3;
    if (hangSeconds > 120) hangSeconds = 120;
    g_hangSeconds = hangSeconds;

    if (logPath != nullptr)
    {
        size_t i = 0;
        while (i < kMaxLogPathLen - 1 && logPath[i] != '\0')
        {
            g_logPath[i] = logPath[i];
            ++i;
        }
        g_logPath[i] = '\0';
    }

    g_startMonotonicSec = monotonicSec();

    pthread_attr_t attr;
    if (pthread_attr_init(&attr) != 0)
    {
        __android_log_print(ANDROID_LOG_WARN, WATCHDOG_LOG_TAG,
            "pthread_attr_init failed: errno=%d", errno);
        return;
    }
    // Detached: we do not need to join.  Stack: 64 KiB is comfortably enough
    // for our small fixed buffers.
    pthread_attr_setdetachstate(&attr, PTHREAD_CREATE_DETACHED);
    pthread_attr_setstacksize(&attr, 64 * 1024);

    pthread_t tid;
    int rc = pthread_create(&tid, &attr, &watchdogMain, nullptr);
    pthread_attr_destroy(&attr);

    if (rc != 0)
    {
        __android_log_print(ANDROID_LOG_WARN, WATCHDOG_LOG_TAG,
            "pthread_create failed: rc=%d errno=%d", rc, errno);
        return;
    }

    g_started = 1;

    __android_log_print(ANDROID_LOG_INFO, WATCHDOG_LOG_TAG,
        "native watchdog armed (logPath=%s hang_threshold=%ds)",
        g_logPath[0] ? g_logPath : "<none, logcat only>",
        (int)g_hangSeconds);
}

__attribute__((visibility("default")))
void osu_native_watchdog_heartbeat()
{
    // Single relaxed-acquire/release pair on a 64-bit slot: writer = managed
    // Update thread, reader = watchdog pthread.  No locks, no allocation.
    uint64_t now = monotonicSec();
    if (now == 0) return;
    __atomic_store_n(&g_lastHeartbeatMonotonicSec, now, __ATOMIC_RELEASE);
}

} // extern "C"
