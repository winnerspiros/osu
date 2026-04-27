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
//   captures the signal IN-PROCESS and writes a structured dump to **both**
//   logcat (tag `osu!crash`) and a plain text file at a path passed in by
//   the C# side (`<external-files-dir>/native_crash.log`).  That path is
//   reachable by the user via Android's Files app without root or adb.
//   After dumping, the previous handler (debuggerd) is invoked so the normal
//   Android tombstone is still produced.
//
//   The dump contains:
//     1. Signal info (signal/code/fault address/tid/thread name/uptime).
//     2. Full register state.
//     3. The faulting thread's backtrace, walked from the saved ucontext via
//        the AArch64 frame-pointer chain.  Each frame is symbolicated by
//        trying, in order:
//          a. dladdr — resolves PCs that fall inside a loaded ELF (.so).
//          b. The Mono `--jitmap` perfmap (`<TMPDIR>/perf-<pid>.map`), which
//             names managed JIT methods.  Enable by setting Mono env vars
//             (see `resolveViaPerfmap` comment below).
//          c. /proc/self/maps — labels the containing region (e.g. JIT trampoline
//             or stripped .so) with offset, so addresses without a symbol still
//             produce actionable output instead of "<unresolved>".
//     4. /proc/self/maps so any addresses still without a symbol after (3c)
//        can be cross-checked manually against the full process memory layout.
//     5. The secondary `_Unwind_Backtrace` output for completeness.
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
#include <sys/mman.h>
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
// Mapping- and JIT-perfmap-based fallback symbolicators.
//
// Why this exists:
//   `dladdr` only resolves PCs that fall inside a loaded ELF (.so) image.
//   It cannot resolve:
//     a. PCs in Mono's JIT/trampoline regions (anonymous `rwxp` mappings) —
//        these are where every `<unresolved>` frame in our existing crash logs
//        sits when the crash is in managed C# code or a Mono trampoline.
//     b. PCs in stripped libraries with no .dynsym entries, where dladdr at
//        best returns the library path with no symbol.
//
//   We add two fallbacks below, tried in order whenever dladdr fails:
//     1. `resolveViaPerfmap` — search a Mono `--jitmap` file (if present) for
//        the managed method that owns this PC.  Mono with `--jitmap` (enabled
//        via `MONO_ENV_OPTIONS=--jitmap`) writes one line per JIT method to
//        `<TMPDIR>/perf-<pid>.map` in the format
//            <hex_start_addr> <hex_size> <method_name>
//        which we parse line-by-line.
//     2. `resolveViaProcMaps` — find the `/proc/self/maps` entry containing
//        the PC and emit `[perms start-end +offset] /path/to/lib` (or a
//        `[Mono JIT/trampoline (anon rwxp)]` tag for anonymous executable
//        mappings).  This always works and turns "<unresolved>" lines into
//        actionable output even when no perfmap is present.
//
// To enable the perfmap output (step 1) for a build:
//   - Add an `AndroidEnvironment` text file to the project containing:
//       MONO_ENV_OPTIONS=--jitmap
//       TMPDIR=/data/user/0/<pkg>/cache
//     TMPDIR MUST point at the app's internal storage (not the external
//     /storage/emulated/... path): Realm calls mkfifo() under TMPDIR for its
//     cross-process notifier, and FUSE-backed external storage rejects
//     mkfifo() with EACCES, which crashes Realm.GetInstance() at startup.
//     Internal storage (ext4/f2fs) supports FIFOs.  The crash handler then
//     reads `getenv("TMPDIR")` at signal time to locate the perfmap and
//     emits the symbolicated frames into native_crash.log (which lives in
//     the external files dir and IS user-retrievable).
//     `crash_handler.cpp` also searches `/tmp`, `/data/local/tmp`, and the
//     directory containing `g_logPath` as fallbacks.
//
// Async-signal safety:
//   - All file I/O uses open/read/close (signal-safe).
//   - We do NOT use malloc.  The perfmap is mmap'd once on first use into a
//     static slot; subsequent frame lookups scan that buffer in-place.
//   - The /proc/self/maps lookup uses a fixed-size stack buffer and
//     re-opens the file once per crash (it is small — typically <1 MB).
// ----------------------------------------------------------------------------

// Lazily-mapped Mono perfmap.  Set on first call to resolveViaPerfmap during
// a crash; never unmapped (we're about to die anyway).
static const char* g_perfmapData = nullptr;
static size_t g_perfmapSize = 0;
static volatile sig_atomic_t g_perfmapTried = 0;

static int hexVal(char c) {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return 10 + (c - 'a');
    if (c >= 'A' && c <= 'F') return 10 + (c - 'A');
    return -1;
}

// Parse "<hex>" up to a non-hex char.  Advances *p past the parsed digits.
// Returns the parsed value (0 if no digits parsed).
static uint64_t parseHexAt(const char* s, size_t len, size_t* p) {
    uint64_t v = 0;
    while (*p < len) {
        int d = hexVal(s[*p]);
        if (d < 0) break;
        v = (v << 4) | (uint64_t)d;
        ++*p;
    }
    return v;
}

// Append a NUL-terminated literal to a fixed buffer; advances *bp.
static void appendLit(char* buf, int cap, int* bp, const char* s) {
    while (*s && *bp < cap - 1) buf[(*bp)++] = *s++;
}

// Append a length-bounded string (may contain '\n' which we stop at).
static void appendBounded(char* buf, int cap, int* bp, const char* s, int slen) {
    for (int i = 0; i < slen && *bp < cap - 1; ++i) {
        char c = s[i];
        if (c == '\n' || c == '\r') break;
        buf[(*bp)++] = c;
    }
}

// Try to mmap the Mono perfmap once.  Returns true if g_perfmapData is set.
// Searches several plausible locations because Mono's `--jitmap` always
// writes to `<TMPDIR>/perf-<pid>.map` and TMPDIR varies by configuration.
static bool ensurePerfmapLoaded() {
    if (g_perfmapTried) return g_perfmapData != nullptr;
    g_perfmapTried = 1;

    // Build "perf-<pid>.map" once.
    char nameBuf[64];
    int np = 0;
    appendLit(nameBuf, sizeof(nameBuf), &np, "perf-");
    {
        char tmp[16]; int tp = 0;
        long long n = (long long)getpid();
        if (n == 0) tmp[tp++] = '0';
        while (n > 0 && tp < 15) { tmp[tp++] = (char)('0' + (n % 10)); n /= 10; }
        while (tp > 0 && np < (int)sizeof(nameBuf) - 1) nameBuf[np++] = tmp[--tp];
    }
    appendLit(nameBuf, sizeof(nameBuf), &np, ".map");
    nameBuf[np] = '\0';

    // Candidate directories, in priority order.  The dir containing g_logPath
    // (external files dir) is checked first as a historical fallback, but
    // current builds set TMPDIR to the app's internal cache dir (FUSE-backed
    // external storage cannot host the FIFOs Realm needs — see mono.env), so
    // the perfmap normally lives at $TMPDIR/perf-<pid>.map.
    const char* tmpEnv = getenv("TMPDIR");
    char logDir[kMaxLogPathLen] = {};
    if (g_logPath[0] != '\0') {
        size_t len = 0;
        while (g_logPath[len] != '\0' && len < sizeof(logDir) - 1) {
            logDir[len] = g_logPath[len];
            ++len;
        }
        // Strip trailing filename component.
        while (len > 0 && logDir[len - 1] != '/') { logDir[--len] = '\0'; }
        if (len > 1 && logDir[len - 1] == '/') logDir[len - 1] = '\0';
    }

    const char* dirs[4] = {
        logDir[0] ? logDir : nullptr,
        tmpEnv,
        "/tmp",
        "/data/local/tmp",
    };

    for (int i = 0; i < 4; ++i) {
        if (!dirs[i] || dirs[i][0] == '\0') continue;
        char path[kMaxLogPathLen + 64];
        int p = 0;
        for (int k = 0; dirs[i][k] && p < (int)sizeof(path) - 1; ++k) path[p++] = dirs[i][k];
        if (p > 0 && path[p - 1] != '/' && p < (int)sizeof(path) - 1) path[p++] = '/';
        for (int k = 0; nameBuf[k] && p < (int)sizeof(path) - 1; ++k) path[p++] = nameBuf[k];
        path[p] = '\0';

        int fd = open(path, O_RDONLY | O_CLOEXEC);
        if (fd < 0) continue;
        struct stat st;
        if (fstat(fd, &st) != 0 || st.st_size <= 0) { close(fd); continue; }
        // Cap the mapped size at 64 MB to bound scan time.  A perfmap larger
        // than that for a single .NET process would be extraordinary.
        size_t sz = (size_t)st.st_size;
        if (sz > 64u * 1024u * 1024u) sz = 64u * 1024u * 1024u;
        void* m = mmap(nullptr, sz, PROT_READ, MAP_PRIVATE, fd, 0);
        close(fd);
        if (m == MAP_FAILED) continue;
        g_perfmapData = static_cast<const char*>(m);
        g_perfmapSize = sz;
        return true;
    }
    return false;
}

// Linearly scan the perfmap for the entry containing pc.  On hit, writes
// "  [JIT] <method_name>+0xOFF" to fd and returns true.
static bool resolveViaPerfmap(int fd, uintptr_t pc) {
    if (!ensurePerfmapLoaded()) return false;
    const char* d = g_perfmapData;
    size_t n = g_perfmapSize;
    size_t i = 0;
    while (i < n) {
        // Each line: "<hex_start> <hex_size> <name>\n"
        size_t lineStart = i;
        size_t p = i;
        uint64_t start = parseHexAt(d, n, &p);
        // skip space
        while (p < n && d[p] == ' ') ++p;
        uint64_t size = parseHexAt(d, n, &p);
        while (p < n && d[p] == ' ') ++p;
        size_t nameStart = p;
        while (p < n && d[p] != '\n') ++p;
        size_t nameLen = p - nameStart;
        if (size != 0 && pc >= start && pc < start + size) {
            char buf[320];
            int bp = 0;
            appendLit(buf, sizeof(buf), &bp, "  [JIT] ");
            appendBounded(buf, sizeof(buf), &bp, d + nameStart, (int)nameLen);
            appendLit(buf, sizeof(buf), &bp, "+0x");
            // hex offset
            uint64_t off = pc - start;
            char hb[17];
            static const char hd[] = "0123456789abcdef";
            int hp = 0;
            if (off == 0) hb[hp++] = '0';
            char rev[17]; int rp = 0;
            while (off > 0) { rev[rp++] = hd[off & 0xf]; off >>= 4; }
            while (rp > 0) hb[hp++] = rev[--rp];
            for (int k = 0; k < hp && bp < (int)sizeof(buf) - 1; ++k) buf[bp++] = hb[k];
            buf[bp] = '\0';
            ssize_t wn = write(fd, buf, bp);
            (void)wn;
            return true;
        }
        // advance past newline
        if (p < n && d[p] == '\n') ++p;
        // safety: if a line is malformed and we didn't advance, force progress
        if (p == lineStart) ++p;
        i = p;
    }
    return false;
}

// Scan /proc/self/maps for the entry containing pc.  On hit writes
// "  [perms start-end +0xOFF] <path-or-tag>" to fd and returns true.
// Reads the file fresh each call (it's small and signal-safe to do so).
static bool resolveViaProcMaps(int fd, uintptr_t pc) {
    int mfd = open("/proc/self/maps", O_RDONLY | O_CLOEXEC);
    if (mfd < 0) return false;

    // We accumulate a single map line into `line` (max 512 chars; lines on
    // Android maps are well below this in practice — long ones are paths to
    // /data/app/.../base.apk plus offset, ~280 chars).
    char line[512];
    int lp = 0;
    char chunk[4096];
    bool hit = false;

    for (;;) {
        ssize_t r = read(mfd, chunk, sizeof(chunk));
        if (r <= 0) break;
        for (ssize_t ci = 0; ci < r; ++ci) {
            char c = chunk[ci];
            if (c == '\n') {
                line[lp] = '\0';
                // Parse "start-end perms offset dev inode <path>"
                size_t p = 0; size_t lineLen = (size_t)lp;
                uint64_t s = parseHexAt(line, lineLen, &p);
                if (p < lineLen && line[p] == '-') {
                    ++p;
                    uint64_t e = parseHexAt(line, lineLen, &p);
                    if (pc >= s && pc < e) {
                        // Skip space, capture perms (4 chars).
                        while (p < lineLen && line[p] == ' ') ++p;
                        char perms[5] = {};
                        for (int k = 0; k < 4 && p < lineLen; ++k, ++p) perms[k] = line[p];
                        // Skip 3 fields (offset dev inode) to reach path.
                        for (int field = 0; field < 3; ++field) {
                            while (p < lineLen && line[p] == ' ') ++p;
                            while (p < lineLen && line[p] != ' ') ++p;
                        }
                        while (p < lineLen && line[p] == ' ') ++p;
                        const char* path = (p < lineLen) ? &line[p] : "";

                        char outBuf[640];
                        int bp = 0;
                        appendLit(outBuf, sizeof(outBuf), &bp, "  [");
                        for (int k = 0; k < 4 && perms[k] && bp < (int)sizeof(outBuf) - 1; ++k)
                            outBuf[bp++] = perms[k];
                        appendLit(outBuf, sizeof(outBuf), &bp, " 0x");
                        // start hex
                        {
                            uint64_t v = s;
                            char hb[17]; int hp = 0;
                            static const char hd[] = "0123456789abcdef";
                            if (v == 0) hb[hp++] = '0';
                            char rev[17]; int rp = 0;
                            while (v > 0) { rev[rp++] = hd[v & 0xf]; v >>= 4; }
                            while (rp > 0) hb[hp++] = rev[--rp];
                            for (int k = 0; k < hp && bp < (int)sizeof(outBuf) - 1; ++k)
                                outBuf[bp++] = hb[k];
                        }
                        appendLit(outBuf, sizeof(outBuf), &bp, "-0x");
                        {
                            uint64_t v = e;
                            char hb[17]; int hp = 0;
                            static const char hd[] = "0123456789abcdef";
                            if (v == 0) hb[hp++] = '0';
                            char rev[17]; int rp = 0;
                            while (v > 0) { rev[rp++] = hd[v & 0xf]; v >>= 4; }
                            while (rp > 0) hb[hp++] = rev[--rp];
                            for (int k = 0; k < hp && bp < (int)sizeof(outBuf) - 1; ++k)
                                outBuf[bp++] = hb[k];
                        }
                        appendLit(outBuf, sizeof(outBuf), &bp, " +0x");
                        {
                            uint64_t v = pc - s;
                            char hb[17]; int hp = 0;
                            static const char hd[] = "0123456789abcdef";
                            if (v == 0) hb[hp++] = '0';
                            char rev[17]; int rp = 0;
                            while (v > 0) { rev[rp++] = hd[v & 0xf]; v >>= 4; }
                            while (rp > 0) hb[hp++] = rev[--rp];
                            for (int k = 0; k < hp && bp < (int)sizeof(outBuf) - 1; ++k)
                                outBuf[bp++] = hb[k];
                        }
                        outBuf[bp < (int)sizeof(outBuf) - 1 ? bp++ : bp] = ']';
                        if (path[0] != '\0') {
                            appendLit(outBuf, sizeof(outBuf), &bp, " ");
                            for (int k = 0; path[k] && bp < (int)sizeof(outBuf) - 1; ++k)
                                outBuf[bp++] = path[k];
                        } else if (perms[2] == 'x') {
                            // Anonymous executable mapping: classic Mono JIT/trampoline region.
                            appendLit(outBuf, sizeof(outBuf), &bp,
                                      " [Mono JIT/trampoline (anon rwxp)]");
                        } else {
                            appendLit(outBuf, sizeof(outBuf), &bp, " [anon]");
                        }
                        outBuf[bp] = '\0';
                        ssize_t wn = write(fd, outBuf, bp);
                        (void)wn;
                        hit = true;
                    }
                }
                lp = 0;
                if (hit) break;
            } else if (lp < (int)sizeof(line) - 1) {
                line[lp++] = c;
            } else {
                // overflow: drop until newline
            }
        }
        if (hit) break;
    }
    close(mfd);
    return hit;
}

// Convenience: try perfmap then /proc/self/maps.  Writes nothing (and returns
// false) if neither resolves.
static bool resolveUnknownPc(int fd, uintptr_t pc) {
    if (pc == 0) return false;
    if (resolveViaPerfmap(fd, pc)) return true;
    return resolveViaProcMaps(fd, pc);
}

// ----------------------------------------------------------------------------
// Async-signal-safe page-readability probe.  Returns true if at least one byte
// at `addr` is mapped & readable.  Uses msync(MS_ASYNC) which is documented
// signal-safe and returns ENOMEM/EFAULT for unmapped regions without faulting.
// Aligns down to the page boundary internally.
//
// This lets us scan arbitrary memory (e.g. raw stack words) without risking
// a SIGSEGV inside the handler — important because re-entry would abort the
// dump entirely (g_inHandler latch chains straight to debuggerd).
// ----------------------------------------------------------------------------
static bool isAddressReadable(uintptr_t addr) {
    if (addr < 0x10000) return false;
    long pageSize = sysconf(_SC_PAGESIZE);
    if (pageSize <= 0) pageSize = 4096;
    uintptr_t page = addr & ~(uintptr_t)(pageSize - 1);
    if (msync(reinterpret_cast<void*>(page), 1, MS_ASYNC) != 0) {
        // ENOMEM => not mapped; EINVAL/other => treat as not safe.
        return false;
    }
    return true;
}

// ----------------------------------------------------------------------------
// Dump up to `wordCount` 8-byte words starting at `sp`, annotating each
// candidate that resolves to a code mapping (via dladdr OR /proc/self/maps).
// This recovers the JIT-method context that the FP-walker misses when `x29`
// has been clobbered (observed on the SIGBUS in PID 24246 where the FP chain
// terminated after one frame because the caller's saved FP was junk).
//
// Format per line:
//   "  [sp+0xNNN] = 0xVVVVVVVVVVVVVVVV  <annotation>"
// where annotation is the dladdr-resolved symbol/library if any, else a
// /proc/self/maps region label if the value points into mapped code, else
// nothing (data words are emitted unannotated for completeness).
//
// Skipped entirely if `sp` itself fails the readability probe.
// ----------------------------------------------------------------------------
static void dumpStackWords(int fd, uintptr_t sp, int wordCount) {
    if (fd < 0 || sp == 0) return;
    if ((sp & 0x7) != 0) {
        writeStr(fd, "  <sp not 8-byte aligned, skipping stack-words dump>\n");
        return;
    }
    if (!isAddressReadable(sp)) {
        writeStr(fd, "  <sp not readable, skipping stack-words dump>\n");
        return;
    }

    for (int i = 0; i < wordCount; ++i) {
        uintptr_t addr = sp + (uintptr_t)i * 8;
        // Re-probe at each page boundary so a stack that ends mid-dump
        // doesn't trip a fault on the very last word.
        if ((addr & 0xfff) == 0 && !isAddressReadable(addr)) break;

        uintptr_t value = *reinterpret_cast<volatile uintptr_t*>(addr);

        writeStr(fd, "  [sp+0x");
        writeHex64(fd, (uint64_t)i * 8, 3);
        writeStr(fd, "] = 0x");
        writeHex64(fd, (uint64_t)value);

        // Try to annotate. Filter cheaply: code values on AArch64 user-space
        // sit between 0x10000 and 0x800000000000. Drop everything else as
        // pure data to keep the dump readable.
        if (value >= 0x10000 && value < 0x0000800000000000ull) {
            Dl_info info;
            if (dladdr(reinterpret_cast<void*>(value), &info) != 0 && info.dli_fname) {
                writeStr(fd, "  ");
                writeStr(fd, info.dli_fname);
                if (info.dli_sname) {
                    uintptr_t off = value - (uintptr_t)info.dli_saddr;
                    writeStr(fd, " (");
                    writeStr(fd, info.dli_sname);
                    writeStr(fd, "+0x");
                    writeHex64(fd, (uint64_t)off, 1);
                    writeStr(fd, ")");
                } else if (info.dli_fbase) {
                    uintptr_t off = value - (uintptr_t)info.dli_fbase;
                    writeStr(fd, " (lib+0x");
                    writeHex64(fd, (uint64_t)off, 1);
                    writeStr(fd, ")");
                }
            } else {
                // dladdr miss — try /proc/self/maps to at least label the
                // containing region (anonymous executable mapping = JIT).
                resolveViaProcMaps(fd, value);
            }
        }
        writeStr(fd, "\n");
    }
}

// ----------------------------------------------------------------------------
// Annotate a single named register's value with its containing /proc/self/maps
// region (if any). Used to classify x30 (LR) explicitly when the FP-walk
// terminates early — the register dump shows only the bare hex, leaving the
// reader to manually cross-reference against the maps section. With this
// helper the LR's library/region appears inline.
// ----------------------------------------------------------------------------
static void annotateRegister(int fd, const char* name, uintptr_t value) {
    if (fd < 0 || value < 0x10000) return;
    writeStr(fd, "  ");
    writeStr(fd, name);
    writeStr(fd, " = 0x");
    writeHex64(fd, (uint64_t)value);
    Dl_info info;
    if (dladdr(reinterpret_cast<void*>(value), &info) != 0 && info.dli_fname) {
        writeStr(fd, "  ");
        writeStr(fd, info.dli_fname);
        if (info.dli_sname) {
            uintptr_t off = value - (uintptr_t)info.dli_saddr;
            writeStr(fd, " (");
            writeStr(fd, info.dli_sname);
            writeStr(fd, "+0x");
            writeHex64(fd, (uint64_t)off, 1);
            writeStr(fd, ")");
        } else if (info.dli_fbase) {
            uintptr_t off = value - (uintptr_t)info.dli_fbase;
            writeStr(fd, " (lib+0x");
            writeHex64(fd, (uint64_t)off, 1);
            writeStr(fd, ")");
        }
        writeStr(fd, "\n");
    } else {
        writeStr(fd, "\n");
        // Fallback: locate the containing maps region (handles JIT/anon).
        resolveViaProcMaps(fd, value);
        writeStr(fd, "\n");
    }
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
    } else if (!resolveUnknownPc(st->fd, pc)) {
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
// Symbolicate a single PC and write a "  #NN pc=0xHEX  /lib (sym+0xOFF)\n" line.
// `tagWhenUnresolved` lets the caller annotate frames whose PC is invalid
// (e.g. NULL function-pointer call → pc == 0).
// Also mirrors a short version to logcat.
// ----------------------------------------------------------------------------
static void writeFrame(int fd, int frameNo, uintptr_t pc, const char* tagWhenUnresolved) {
    writeStr(fd, "  #");
    if (frameNo < 10) writeStr(fd, "0");
    writeDec(fd, frameNo);
    writeStr(fd, " pc 0x");
    writeHex64(fd, (uint64_t)pc);

    Dl_info info;
    bool resolved = (pc != 0) && (dladdr(reinterpret_cast<void*>(pc), &info) != 0);

    if (resolved && info.dli_fname) {
        writeStr(fd, "  ");
        writeStr(fd, info.dli_fname);

        if (info.dli_sname) {
            uintptr_t off = pc - (uintptr_t)info.dli_saddr;
            writeStr(fd, " (");
            writeStr(fd, info.dli_sname);
            writeStr(fd, "+0x");
            writeHex64(fd, (uint64_t)off, 1);
            writeStr(fd, ")");
        } else if (info.dli_fbase) {
            uintptr_t off = pc - (uintptr_t)info.dli_fbase;
            writeStr(fd, " (lib+0x");
            writeHex64(fd, (uint64_t)off, 1);
            writeStr(fd, ")");
        }
    } else if (tagWhenUnresolved) {
        writeStr(fd, "  ");
        writeStr(fd, tagWhenUnresolved);
        // Even when we have a synthetic tag (e.g. "<NULL function pointer call>"
        // or "<LR (return address of NULL call)>"), still try to attach a
        // perfmap/maps annotation so we know which JIT region or library the
        // PC sits in.
        resolveUnknownPc(fd, pc);
    } else if (!resolveUnknownPc(fd, pc)) {
        writeStr(fd, "  <unresolved>");
    }
    writeStr(fd, "\n");

    // Short logcat mirror.
    char line[256];
    int p = 0;
    line[p++] = '#';
    if (frameNo < 10) line[p++] = '0';
    long long n = frameNo;
    char tmp[12]; int tp = 0;
    if (n == 0) tmp[tp++] = '0';
    while (n > 0 && tp < 11) { tmp[tp++] = (char)('0' + (n % 10)); n /= 10; }
    while (tp > 0 && p < (int)sizeof(line) - 1) line[p++] = tmp[--tp];
    const char* sep = " pc=0x";
    for (int i = 0; sep[i] && p < (int)sizeof(line) - 1; ++i) line[p++] = sep[i];
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

// ----------------------------------------------------------------------------
// Walk the *crashing thread's* stack from the saved ucontext.
//
// `_Unwind_Backtrace` (used elsewhere in this file) walks the *current*
// thread's stack — i.e., the stack of the signal handler itself, with the
// libgcc unwinder stopping at the kernel signal trampoline (`__kernel_rt_sigreturn`)
// because there's no CFI across the signal frame.  In practice that produces
// only "crashHandler → libsigchain → vdso", which is useless for diagnosing
// the actual fault.
//
// To recover the real backtrace we walk the AArch64 frame-pointer chain
// starting from the saved registers in ucontext:
//   - frame[0] is `pc` (or, if pc == 0 because of a NULL function pointer
//     call, `lr` — the return address of that call, i.e. the call site).
//   - subsequent frames come from following `x29 (fp)` chain:
//       prev_fp = *(uintptr_t*)fp
//       prev_lr = *(uintptr_t*)(fp + 8)
//
// AArch64 on Android is built with frame pointers preserved (Google ABI
// requirement for Android 10+), so this chain is reliable.
//
// Safety: we validate each `fp` (non-NULL, 16-byte aligned, monotonically
// increasing — the stack grows down so each new fp must be strictly greater
// than the previous) before dereferencing.  A bad fp simply terminates the
// walk; the re-entrancy guard catches a SIGSEGV inside the walk and falls
// straight through to the previous handler.
// ----------------------------------------------------------------------------
#if defined(__aarch64__)
static void walkContextStack(int fd, void* ucv) {
    if (!ucv) return;
    auto* uc = static_cast<ucontext_t*>(ucv);
    auto& mc = uc->uc_mcontext;

    uintptr_t pc = (uintptr_t)mc.pc;
    uintptr_t lr = (uintptr_t)mc.regs[30];
    uintptr_t fp = (uintptr_t)mc.regs[29];

    int frame = 0;

    if (pc == 0) {
        // Faulting site is a NULL function pointer call.  Emit a synthetic
        // frame 0 to make this explicit, then frame 1 is the actual call site
        // pointed to by LR.
        writeFrame(fd, frame++, 0, "<NULL function pointer call>");
        if (lr != 0) {
            writeFrame(fd, frame++, lr, "<LR (return address of NULL call)>");
        }
    } else {
        writeFrame(fd, frame++, pc, "<faulting PC>");
        // After the leaf frame, LR is the return address (caller).  Only emit
        // if it differs from PC and looks plausible.
        if (lr != 0 && lr != pc) {
            writeFrame(fd, frame++, lr, "<LR (caller return address)>");
        }
    }

    // Walk the frame pointer chain.  Cap at 32 frames; bail out on any sign
    // of a corrupt or non-monotonic chain.
    constexpr int kMaxFrames = 32;
    uintptr_t lastFp = 0;
    while (frame < kMaxFrames && fp != 0) {
        // Validate fp: must be 16-byte aligned, non-low-memory, and strictly
        // greater than the previous fp (stack grows down → fp moves *up* as
        // we walk callers).
        if ((fp & 0xf) != 0) break;
        if (fp < 0x10000) break;
        if (lastFp != 0 && fp <= lastFp) break;

        // Read [fp] = saved fp, [fp+8] = saved lr.  Best-effort dereference;
        // if fp is bogus we'll trip the re-entrancy guard and bail.
        uintptr_t prevFp = *reinterpret_cast<volatile uintptr_t*>(fp);
        uintptr_t prevLr = *reinterpret_cast<volatile uintptr_t*>(fp + 8);

        if (prevLr == 0) break;
        writeFrame(fd, frame++, prevLr, nullptr);

        lastFp = fp;
        fp = prevFp;
    }

    if (frame == 0) writeStr(fd, "  <empty — no recoverable context>\n");
}
#else
static void walkContextStack(int fd, void* /*ucv*/) {
    writeStr(fd, "  <context-walk only implemented for aarch64>\n");
}
#endif

// ----------------------------------------------------------------------------
// Dump /proc/self/maps to fd so addresses without dladdr symbols can still
// be matched to a library and offset post-mortem.  Uses only signal-safe
// primitives (open/read/write).
// ----------------------------------------------------------------------------
static void dumpProcMaps(int fd) {
    if (fd < 0) return;
    int mapsFd = open("/proc/self/maps", O_RDONLY | O_CLOEXEC);
    if (mapsFd < 0) {
        writeStr(fd, "  <could not open /proc/self/maps>\n");
        return;
    }
    char buf[4096];
    for (;;) {
        ssize_t n = read(mapsFd, buf, sizeof(buf));
        if (n <= 0) break;
        ssize_t off = 0;
        while (off < n) {
            ssize_t w = write(fd, buf + off, n - off);
            if (w <= 0) break;
            off += w;
        }
    }
    close(mapsFd);
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
    //
    // Pre-rotate runaway: if the existing log is more than 4× the soft cap
    // (12 MiB) — the same threshold CrashDiagnostics.rotateIfTooLarge uses
    // for in-place truncation — unlink it before opening so the crash dump
    // lands in a fresh file. Otherwise a stale 100 MB+ log left behind by an
    // older build (or by a tight ANR-restart loop that never gave the
    // managed rotation a chance to run) would have this dump appended to
    // the end where the user is least likely to find it. fstat is async-
    // signal-safe; unlink is too.
    int fd = -1;
    if (g_logPath[0] != '\0') {
        constexpr off_t kRunawayCapBytes = 4LL * 3 * 1024 * 1024;
        struct stat st{};
        if (stat(g_logPath, &st) == 0 && S_ISREG(st.st_mode) && st.st_size > kRunawayCapBytes) {
            (void)unlink(g_logPath);
        }
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

    // Faulting-thread backtrace, recovered from the saved ucontext.  This is
    // the *important* one — it shows where the crash actually happened.
    // (See walkContextStack for the rationale on why we don't use
    // _Unwind_Backtrace for this purpose.)
    writeStr(fd, "Backtrace (from signal context):\n");
    walkContextStack(fd, ucontext);
    if (fd >= 0) fsync(fd);

#if defined(__aarch64__)
    // Extra context for crashes where the FP chain terminates early — e.g. an
    // indirect branch through a junk vtable that lands in JIT code with x29
    // already clobbered. The register dump alone leaves x30 (LR) and the
    // top-of-stack words unannotated; we attach maps/dladdr resolution here
    // so reviewers don't have to manually cross-reference the maps section.
    if (ucontext != nullptr) {
        auto* uc = static_cast<ucontext_t*>(ucontext);
        const auto& mc = uc->uc_mcontext;
        uintptr_t lr = (uintptr_t)mc.regs[30];
        uintptr_t sp = (uintptr_t)mc.sp;

        if (lr != 0) {
            writeStr(fd, "Register annotations:\n");
            annotateRegister(fd, "x30 (lr)", lr);
        }

        // 64 words = 512 bytes — enough to cover at least one full frame's
        // worth of saved registers/spill slots without flooding the dump.
        writeStr(fd, "Stack words near sp (looking for return addresses):\n");
        dumpStackWords(fd, sp, 64);
        if (fd >= 0) fsync(fd);
    }
#endif

    // Memory map.  Lets us correlate any unresolved frames to library+offset
    // even when dladdr can't find a symbol (e.g. internal-namespace functions
    // or stripped .dynsym entries — both of which produce useless "lib+0xc"
    // output above).
    writeStr(fd, "Memory map (/proc/self/maps):\n");
    dumpProcMaps(fd);
    if (fd >= 0) fsync(fd);

    // Secondary backtrace via _Unwind_Backtrace.  This walks the *handler
    // thread's* stack (typically just crashHandler → libsigchain → vdso) and
    // is mostly informational; kept for parity with the previous behaviour.
    writeStr(fd, "Handler-thread backtrace (_Unwind_Backtrace, for reference):\n");
    UnwindState st{ fd, 0, 64 };
    _Unwind_Backtrace(&unwindCallback, &st);
    if (st.frame == 0) writeStr(fd, "  <empty>\n");

    writeStr(fd, "=========================================================\n");
    writeStr(fd, "=== END OF CRASH DUMP ===\n");

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

// Re-install the signal handlers without short-circuiting on g_installed.
// Mono installs its own SIGSEGV handler later in startup (after activity
// OnCreate), which sits in front of ours and intercepts JIT null-deref
// faults — re-raising via tgkill when it cannot translate them, which
// bypasses our dump.  Calling this from a later phase (e.g. GameHost.Run)
// puts our handler back on top of the chain, with Mono's saved as the
// previous handler so chaining still works.
__attribute__((visibility("default")))
void nReinstallCrashHandler() {
    // Re-install alt stack (cheap; idempotent on the same buffer).
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
        // Overwrite previous-handler slot with whatever is currently
        // installed (typically Mono's handler at this point), so when our
        // handler chains, it forwards to Mono rather than to our own
        // already-saved entry.
        sigaction(kSignals[i], &sa, &g_prevHandlers[i]);
    }

    g_installed = 1;

    __android_log_print(ANDROID_LOG_INFO, CRASH_LOG_TAG,
        "Crash handler re-installed (logPath=%s)",
        g_logPath[0] ? g_logPath : "<none, logcat only>");
}

} // extern "C"
