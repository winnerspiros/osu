// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Native crash handler for osu!lazer Android.
//
// Why this exists:
//   On unrooted Android phones the user typically cannot access logcat or
//   /data/tombstones/.  Third-party "crash log viewer" apps read only the
//   summarised, *unsymbolicated* tombstone the system shows in App Info,
//   which gives just `pc=<hex>` and at most two frames — useless for
//   diagnosing a SIGSEGV that originated inside libvulkan or another system
//   library.
//
//   This file installs a SIGSEGV/SIGBUS/SIGILL/SIGFPE/SIGABRT handler that
//   captures the signal IN-PROCESS, walks the stack with `_Unwind_Backtrace`,
//   resolves each frame with `dladdr` (library + symbol + offset), and writes
//   the result to **both** logcat (tag `osu!crash`) and a plain text file at a
//   path passed in by the C# side (`<external-files-dir>/native_crash.log`).
//   That path is reachable by the user via Android's Files app without root
//   or adb.  After dumping, the previous handler (debuggerd) is invoked so
//   the normal Android tombstone is still produced.
//
// Async-signal safety:
//   We use only signal-safe primitives in the handler:
//     - write(2), open(2), close(2), lseek(2)              (all signal-safe)
//     - _Unwind_Backtrace                                  (signal-safe in practice;
//                                                           used by Crashpad, Breakpad,
//                                                           Android's own libdebuggerd)
//     - dladdr                                             (uses a process-global rwlock;
//                                                           safe in practice for crash
//                                                           reporting — same trade-off
//                                                           every Android crash reporter
//                                                           makes)
//     - __android_log_write                                (single write() to a pipe;
//                                                           safe in practice)
//   We deliberately avoid: malloc/free, snprintf-style allocators, std::string,
//   stdio streams, locale-aware formatting.  All formatting is done with our
//   own tiny `writeHex` / `writeDec` helpers writing into a stack buffer.
//
// Stack overflow safety:
//   We register a sigaltstack so the handler runs even when the crashing
//   thread has overflowed its primary stack.

#include <signal.h>
#include <unistd.h>
#include <fcntl.h>
#include <sys/syscall.h>
#include <sys/types.h>
#include <sys/stat.h>
#include <stdint.h>
#include <string.h>
#include <stdlib.h>
#include <errno.h>
#include <time.h>
#include <pthread.h>
#include <dlfcn.h>
#include <unwind.h>
#include <ucontext.h>
#include <android/log.h>

#include "crash_handler.h"

#define CRASH_LOG_TAG "osu!crash"

namespace {

// Path to write crash dumps to.  Set by `nInstallCrashHandler` from C#.
// We hold our own copy so the const char* the C# side passes can be freed
// after the call returns.  Maximum path length is bounded — Android paths
// are well below this on real devices.
constexpr size_t kMaxLogPathLen = 1024;
char g_logPath[kMaxLogPathLen] = {};

// Alternate signal stack so the handler runs even on stack overflow.
constexpr size_t kAltStackSize = 64 * 1024; // 64 KB is plenty for our handler
uint8_t g_altStack[kAltStackSize] __attribute__((aligned(16)));

// Saved previous handlers, so we can chain to debuggerd after dumping.
constexpr int kSignals[] = { SIGSEGV, SIGBUS, SIGILL, SIGFPE, SIGABRT };
constexpr size_t kNumSignals = sizeof(kSignals) / sizeof(kSignals[0]);
struct sigaction g_prevHandlers[kNumSignals];

// Set to 1 when install completes successfully.
volatile sig_atomic_t g_installed = 0;

// Re-entrancy guard: if the handler itself crashes, fall straight through to
// the previous handler instead of recursing.
volatile sig_atomic_t g_inHandler = 0;

// ----------------------------------------------------------------------------
// Async-signal-safe formatters (no malloc, no stdio, no locale).
// ----------------------------------------------------------------------------

// Write a NUL-terminated string to fd; does nothing if fd < 0.
static void writeStr(int fd, const char* s) {
    if (fd < 0 || !s) return;
    size_t len = 0;
    while (s[len] != '\0') ++len;
    ssize_t n = write(fd, s, len);
    (void)n; // we intentionally ignore write errors in a crash handler
}

// Write a 64-bit value as zero-padded hex (no "0x" prefix).
static void writeHex64(int fd, uint64_t v, int width = 16) {
    if (fd < 0) return;
    char buf[17];
    static const char digits[] = "0123456789abcdef";
    for (int i = 15; i >= 0; --i) {
        buf[i] = digits[v & 0xf];
        v >>= 4;
    }
    buf[16] = '\0';
    int start = 16 - width;
    if (start < 0) start = 0;
    ssize_t n = write(fd, buf + start, 16 - start);
    (void)n;
}

// Write a signed decimal integer.
static void writeDec(int fd, long long v) {
    if (fd < 0) return;
    char buf[32];
    int pos = (int)sizeof(buf);
    bool negative = false;
    if (v < 0) { negative = true; v = -v; }
    if (v == 0) buf[--pos] = '0';
    while (v > 0 && pos > 0) { buf[--pos] = (char)('0' + (v % 10)); v /= 10; }
    if (negative && pos > 0) buf[--pos] = '-';
    ssize_t n = write(fd, buf + pos, sizeof(buf) - (size_t)pos);
    (void)n;
}

// Append-write to logcat with our crash tag.
static void logcatWrite(const char* msg) {
    __android_log_write(ANDROID_LOG_ERROR, CRASH_LOG_TAG, msg);
}

// ----------------------------------------------------------------------------
// Backtrace via libgcc/compiler-rt _Unwind_Backtrace.
// ----------------------------------------------------------------------------

struct UnwindState {
    int fd;
    int frame;
    int maxFrames;
};

static _Unwind_Reason_Code unwindCallback(struct _Unwind_Context* ctx, void* arg) {
    UnwindState* st = static_cast<UnwindState*>(arg);
    uintptr_t pc = _Unwind_GetIP(ctx);
    if (pc == 0) return _URC_END_OF_STACK;

    // Resolve symbol via dladdr.
    Dl_info info;
    bool resolved = dladdr(reinterpret_cast<void*>(pc), &info) != 0;

    // Format: "  #NN pc 0xPPPPPPPPPPPPPPPP  /path/to/lib.so (sym+0xOFF)"
    writeStr(st->fd, "  #");
    if (st->frame < 10) writeStr(st->fd, "0");
    writeDec(st->fd, st->frame);
    writeStr(st->fd, " pc 0x");
    writeHex64(st->fd, (uint64_t)pc);

    if (resolved && info.dli_fname) {
        writeStr(st->fd, "  ");
        writeStr(st->fd, info.dli_fname);

        if (info.dli_sname) {
            uintptr_t off = pc - (uintptr_t)info.dli_saddr;
            writeStr(st->fd, " (");
            writeStr(st->fd, info.dli_sname);
            writeStr(st->fd, "+0x");
            writeHex64(st->fd, (uint64_t)off, 1);
            writeStr(st->fd, ")");
        } else if (info.dli_fbase) {
            uintptr_t off = pc - (uintptr_t)info.dli_fbase;
            writeStr(st->fd, " (lib+0x");
            writeHex64(st->fd, (uint64_t)off, 1);
            writeStr(st->fd, ")");
        }
    } else {
        writeStr(st->fd, "  <unresolved>");
    }
    writeStr(st->fd, "\n");

    // Also mirror to logcat (truncated).  __android_log_write does its own
    // null-terminated bounded write internally.
    {
        char line[256];
        // Build a short line for logcat: "#NN pc=0xHEX <lib|sym>"
        int p = 0;
        line[p++] = '#';
        if (st->frame < 10) line[p++] = '0';
        // decimal
        long long n = st->frame;
        char tmp[12]; int tp = 0;
        if (n == 0) tmp[tp++] = '0';
        while (n > 0 && tp < 11) { tmp[tp++] = (char)('0' + (n % 10)); n /= 10; }
        while (tp > 0 && p < (int)sizeof(line) - 1) line[p++] = tmp[--tp];
        const char* sep = " pc=0x";
        for (int i = 0; sep[i] && p < (int)sizeof(line) - 1; ++i) line[p++] = sep[i];
        // hex pc
        static const char hd[] = "0123456789abcdef";
        for (int sh = 60; sh >= 0 && p < (int)sizeof(line) - 1; sh -= 4)
            line[p++] = hd[(pc >> sh) & 0xf];
        if (resolved) {
            const char* lib = info.dli_fname ? info.dli_fname : "?";
            const char* sym = info.dli_sname ? info.dli_sname : "";
            if (p < (int)sizeof(line) - 1) line[p++] = ' ';
            for (int i = 0; lib[i] && p < (int)sizeof(line) - 1; ++i) line[p++] = lib[i];
            if (sym[0]) {
                if (p < (int)sizeof(line) - 1) line[p++] = ' ';
                if (p < (int)sizeof(line) - 1) line[p++] = '(';
                for (int i = 0; sym[i] && p < (int)sizeof(line) - 2; ++i) line[p++] = sym[i];
                if (p < (int)sizeof(line) - 1) line[p++] = ')';
            }
        }
        line[p] = '\0';
        logcatWrite(line);
    }

    st->frame++;
    return (st->frame >= st->maxFrames) ? _URC_END_OF_STACK : _URC_NO_REASON;
}

// ----------------------------------------------------------------------------
// Register dump.
// ----------------------------------------------------------------------------

static void dumpRegisters(int fd, void* ucv) {
    if (!ucv) return;
    auto* uc = static_cast<ucontext_t*>(ucv);

#if defined(__aarch64__)
    auto& mc = uc->uc_mcontext;
    for (int i = 0; i < 31; i += 4) {
        writeStr(fd, "  ");
        for (int j = 0; j < 4 && (i + j) < 31; ++j) {
            writeStr(fd, "x");
            writeDec(fd, i + j);
            writeStr(fd, "=0x");
            writeHex64(fd, (uint64_t)mc.regs[i + j]);
            writeStr(fd, " ");
        }
        writeStr(fd, "\n");
    }
    writeStr(fd, "  sp=0x");  writeHex64(fd, (uint64_t)mc.sp);
    writeStr(fd, "  pc=0x");  writeHex64(fd, (uint64_t)mc.pc);
    writeStr(fd, "  pstate=0x"); writeHex64(fd, (uint64_t)mc.pstate);
    writeStr(fd, "\n");
#elif defined(__arm__)
    auto& mc = uc->uc_mcontext;
    writeStr(fd, "  r0=0x"); writeHex64(fd, mc.arm_r0, 8);
    writeStr(fd, " r1=0x"); writeHex64(fd, mc.arm_r1, 8);
    writeStr(fd, " r2=0x"); writeHex64(fd, mc.arm_r2, 8);
    writeStr(fd, " r3=0x"); writeHex64(fd, mc.arm_r3, 8);
    writeStr(fd, "\n  sp=0x"); writeHex64(fd, mc.arm_sp, 8);
    writeStr(fd, " lr=0x"); writeHex64(fd, mc.arm_lr, 8);
    writeStr(fd, " pc=0x"); writeHex64(fd, mc.arm_pc, 8);
    writeStr(fd, "\n");
#else
    (void)fd;
#endif
}

// ----------------------------------------------------------------------------
// Signal name lookup (signal-safe — no strsignal which can allocate).
// ----------------------------------------------------------------------------
static const char* signalName(int sig) {
    switch (sig) {
        case SIGSEGV: return "SIGSEGV";
        case SIGBUS:  return "SIGBUS";
        case SIGILL:  return "SIGILL";
        case SIGFPE:  return "SIGFPE";
        case SIGABRT: return "SIGABRT";
        default:      return "SIG?";
    }
}

static const char* siCodeName(int sig, int code) {
    if (sig == SIGSEGV) {
        switch (code) {
            case SEGV_MAPERR: return "SEGV_MAPERR (address not mapped)";
            case SEGV_ACCERR: return "SEGV_ACCERR (invalid permissions)";
            default:          return "SEGV_?";
        }
    }
    if (sig == SIGBUS) {
        switch (code) {
            case BUS_ADRALN: return "BUS_ADRALN (alignment)";
            case BUS_ADRERR: return "BUS_ADRERR (no physical address)";
            case BUS_OBJERR: return "BUS_OBJERR (object-specific HW error)";
            default:         return "BUS_?";
        }
    }
    return "?";
}

// ----------------------------------------------------------------------------
// The actual handler.
// ----------------------------------------------------------------------------

static void crashHandler(int sig, siginfo_t* info, void* ucontext) {
    // Re-entrancy guard.  If we crashed inside the handler, jump straight to
    // the previous handler.
    if (g_inHandler) {
        signal(sig, SIG_DFL);
        raise(sig);
        return;
    }
    g_inHandler = 1;

    // Open the dump file (append).  If g_logPath is empty we still log to logcat.
    int fd = -1;
    if (g_logPath[0] != '\0') {
        fd = open(g_logPath, O_WRONLY | O_CREAT | O_APPEND | O_CLOEXEC, 0644);
    }

    // Header.
    writeStr(fd, "\n=========================================================\n");
    writeStr(fd, "[osu!] NATIVE CRASH\n");
    writeStr(fd, "  signal      = ");
    writeStr(fd, signalName(sig));
    writeStr(fd, " (");
    writeDec(fd, sig);
    writeStr(fd, ")\n  si_code     = ");
    writeStr(fd, siCodeName(sig, info ? info->si_code : 0));
    writeStr(fd, " (");
    writeDec(fd, info ? info->si_code : 0);
    writeStr(fd, ")\n  fault_addr  = 0x");
    writeHex64(fd, info ? (uint64_t)(uintptr_t)info->si_addr : 0);
    writeStr(fd, "\n  thread_tid  = ");
    writeDec(fd, (long long)gettid());
    writeStr(fd, "\n  pid         = ");
    writeDec(fd, (long long)getpid());
    writeStr(fd, "\n  uptime_ns   = ");
    {
        struct timespec ts;
        clock_gettime(CLOCK_BOOTTIME, &ts);
        writeDec(fd, (long long)ts.tv_sec * 1000000000LL + (long long)ts.tv_nsec);
    }
    writeStr(fd, "\n  thread_name = ");
    {
        char name[32] = {};
        // pthread_getname_np is signal-safe in bionic (it's a thin wrapper
        // over a /proc/self/task/<tid>/comm read).
        if (pthread_getname_np(pthread_self(), name, sizeof(name)) == 0)
            writeStr(fd, name);
        else
            writeStr(fd, "?");
    }
    writeStr(fd, "\n");

    // Logcat header (so users with logcat access also see something useful).
    {
        char hdr[160];
        // "NATIVE CRASH sig=SIGSEGV(11) code=SEGV_MAPERR(1) addr=0xHEX tid=N"
        const char* sname = signalName(sig);
        int p = 0;
        const char* prefix = "NATIVE CRASH sig=";
        for (int i = 0; prefix[i] && p < (int)sizeof(hdr) - 1; ++i) hdr[p++] = prefix[i];
        for (int i = 0; sname[i] && p < (int)sizeof(hdr) - 1; ++i) hdr[p++] = sname[i];
        if (p < (int)sizeof(hdr) - 1) hdr[p++] = '\0';
        logcatWrite(hdr);
    }

    // Registers.
    writeStr(fd, "Registers:\n");
    dumpRegisters(fd, ucontext);

    // Backtrace.
    writeStr(fd, "Backtrace:\n");
    UnwindState st{ fd, 0, 64 };
    _Unwind_Backtrace(&unwindCallback, &st);
    if (st.frame == 0) writeStr(fd, "  <empty>\n");

    writeStr(fd, "=========================================================\n");

    if (fd >= 0) {
        fsync(fd);
        close(fd);
    }

    // Chain to the previous handler (typically debuggerd) so the system
    // tombstone is still produced.  Find this signal's slot.
    for (size_t i = 0; i < kNumSignals; ++i) {
        if (kSignals[i] == sig) {
            const struct sigaction& prev = g_prevHandlers[i];
            // Restore previous handler so it actually runs (we re-raise below).
            sigaction(sig, &prev, nullptr);
            break;
        }
    }
    // Re-raise; either the previous handler or default disposition will run.
    g_inHandler = 0;
    raise(sig);
}

} // namespace

extern "C" {

// Called from C# (P/Invoke) very early in OnCreate, with the absolute path of
// where to write crash dumps (usually `<external-files-dir>/native_crash.log`).
// `logPath` may be NULL or empty — in that case we still install handlers but
// only logcat output is produced.
__attribute__((visibility("default")))
void nInstallCrashHandler(const char* logPath) {
    if (g_installed) return;

    if (logPath != nullptr) {
        size_t i = 0;
        while (i < kMaxLogPathLen - 1 && logPath[i] != '\0') {
            g_logPath[i] = logPath[i];
            ++i;
        }
        g_logPath[i] = '\0';
    }

    // Install alternate signal stack so we can survive stack overflow in the
    // crashing thread.  This is per-thread; the JVM/SDL thread we care about
    // will inherit it via SA_ONSTACK only if it was set on that thread.  In
    // practice, the most common "no logs" failure mode is a NULL deref on a
    // healthy stack, where the alt stack is unnecessary anyway — but for the
    // few cases where it matters (genuine stack overflow), this helps.
    stack_t ss{};
    ss.ss_sp = g_altStack;
    ss.ss_size = kAltStackSize;
    ss.ss_flags = 0;
    sigaltstack(&ss, nullptr);

    struct sigaction sa{};
    sa.sa_sigaction = &crashHandler;
    sa.sa_flags = SA_SIGINFO | SA_ONSTACK | SA_RESTART;
    sigemptyset(&sa.sa_mask);

    for (size_t i = 0; i < kNumSignals; ++i) {
        sigaction(kSignals[i], &sa, &g_prevHandlers[i]);
    }

    g_installed = 1;

    __android_log_print(ANDROID_LOG_INFO, CRASH_LOG_TAG,
        "Crash handler installed (logPath=%s)",
        g_logPath[0] ? g_logPath : "<none, logcat only>");
}

} // extern "C"
