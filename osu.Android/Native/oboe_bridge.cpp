// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#include "oboe_bridge.h"
#include <oboe/OboeExtensions.h>
#include <oboe/AudioClock.h>
#include <sched.h>
#include <unistd.h>
#include <sys/syscall.h>
#include <sys/resource.h>
#include <sys/types.h>
#include <dirent.h>
#include <fcntl.h>
#include <android/log.h>
#include <dlfcn.h>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <cstdlib>
#include <vector>
#include <algorithm>
typedef uint8_t byte;

#define LOG_TAG "osu!native"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)

// ============================================================
// Smart CPU topology detection via sysfs
// ============================================================
// Reads /sys/devices/system/cpu/cpuN/cpufreq/cpuinfo_max_freq for each core
// to identify actual performance cores. This is far more accurate than the
// generic "upper half" heuristic across all SoC vendors:
//   - Snapdragon 8 Gen 2/3: correctly identifies Gold + Prime (skips Silver)
//   - Exynos 2200/2400: correctly identifies A710/A720 + X2/X4 (skips A510/A520)
//   - Dimensity 9000/9300: correctly identifies high-freq clusters
//   - Google Tensor G3: correctly identifies A715 + X3 (skips A510)
// Threshold: cores with max freq >= 70% of the fastest core are "big".
static std::atomic<int> cachedBigCoreMask{-1}; // -1 = not yet computed

static int computeBigCoreMask() {
    int numCores = sysconf(_SC_NPROCESSORS_CONF);
    if (numCores <= 0 || numCores > 32) return 0;

    long freqs[32] = {};
    long maxFreq = 0;

    for (int i = 0; i < numCores; i++) {
        char path[96];
        snprintf(path, sizeof(path),
                 "/sys/devices/system/cpu/cpu%d/cpufreq/cpuinfo_max_freq", i);
        FILE* f = fopen(path, "r");
        if (f) {
            if (fscanf(f, "%ld", &freqs[i]) != 1)
                freqs[i] = 0;
            fclose(f);
            if (freqs[i] > maxFreq) maxFreq = freqs[i];
        }
    }

    if (maxFreq == 0) return 0;

    // Include cores whose max freq is >= 70% of the fastest core.
    // This captures Prime + Gold on all major SoC families.
    long threshold = maxFreq * 70 / 100;
    int mask = 0;

    for (int i = 0; i < numCores; i++) {
        if (freqs[i] >= threshold)
            mask |= (1 << i);
    }

    LOGI("CPU topology: %d cores, max=%ldkHz, threshold=%ldkHz, bigMask=0x%x",
         numCores, maxFreq, threshold, mask);

    for (int i = 0; i < numCores; i++) {
        LOGI("  cpu%d: %ldkHz %s", i, freqs[i],
             (freqs[i] >= threshold) ? "(BIG)" : "(little)");
    }

    return mask;
}

OboeBridge::OboeBridge() {
    LOGI("OboeBridge created");
}

OboeBridge::~OboeBridge() {
    stop();
    LOGI("OboeBridge destroyed");
}

bool OboeBridge::open(int32_t sampleRate) {
    std::lock_guard<std::mutex> lock(streamLock_);
    requestedSampleRate_ = sampleRate;

    // Low-latency MMAP path requires explicit enabling in Oboe.
    // MMAP provides direct access to audio hardware buffers, shaving ~1-2ms off latency.
    oboe::OboeExtensions::setMMapEnabled(true);

    // Non-owning shared_ptr for error callback — OboeBridge outlives the stream.
    auto errorCb = std::shared_ptr<oboe::AudioStreamErrorCallback>(
        std::shared_ptr<void>(), static_cast<oboe::AudioStreamErrorCallback*>(this));

    // -----------------------------------------------------------------------
    // Pass 1: open with a raw 'this' callback (no StabilizedCallback).
    //
    // On AAudio MMAP paths (Pixel 3+, Snapdragon 8 Gen 1+, most modern
    // Android), the kernel delivers audio callbacks with near-perfect timing
    // via the hardware FIFO interrupt.  StabilizedCallback works by sleeping
    // on the callback thread to normalise jitter — on MMAP that sleep is pure
    // overhead: it delays the write to the hardware ring buffer, adding latency
    // without removing any real jitter.
    //
    // We probe by opening with the raw callback first.  If MMAP is confirmed
    // we keep this stream.  If not, we close it and reopen with
    // StabilizedCallback (see Pass 2 below) to cover the non-MMAP / OpenSL ES
    // fallback path where OS scheduler jitter is real.
    // -----------------------------------------------------------------------
    stabilizedCallback_.reset();
    auto rawCb = std::shared_ptr<oboe::AudioStreamDataCallback>(
        std::shared_ptr<void>(), static_cast<oboe::AudioStreamDataCallback*>(this));

    oboe::AudioStreamBuilder builder;
    builder.setDirection(oboe::Direction::Output)
           ->setPerformanceMode(oboe::PerformanceMode::LowLatency)
           ->setSharingMode(oboe::SharingMode::Exclusive)
           ->setFormat(oboe::AudioFormat::Float)
           ->setChannelCount(oboe::ChannelCount::Stereo)
           ->setSampleRate(sampleRate > 0 ? sampleRate : oboe::kUnspecified)
           ->setSampleRateConversionQuality(oboe::SampleRateConversionQuality::None)
           ->setContentType(oboe::ContentType::Music)
           ->setUsage(oboe::Usage::Game)
           ->setAudioApi(oboe::AudioApi::AAudio)
           ->setFramesPerDataCallback(oboe::kUnspecified)
           ->setBufferCapacityInFrames(oboe::kUnspecified)
           ->setChannelConversionAllowed(false)
           ->setFormatConversionAllowed(false)
           // Audio is pre-mixed by BASS — tell Android not to spatialize it again.
           ->setIsContentSpatialized(true)
           // Prevent other apps from capturing our audio stream (competitive integrity).
           ->setAllowedCapturePolicy(oboe::AllowedCapturePolicy::None)
           ->setDataCallback(rawCb)
           ->setErrorCallback(errorCb);

    oboe::Result result = builder.openStream(stream_);

    if (result == oboe::Result::OK) {
        bool mmapActive = oboe::OboeExtensions::isMMapUsed(stream_.get());
        LOGI("Oboe pass-1 open: MMAP=%s", mmapActive ? "yes" : "no");

        if (!mmapActive) {
            // ---------------------------------------------------------------
            // Pass 2: MMAP unavailable — close and reopen with StabilizedCallback.
            // StabilizedCallback adds a compensating sleep to normalise the
            // variable latency introduced by the OS scheduler on non-MMAP paths,
            // reducing buffer underruns on devices that rely on AAudio binder IPC
            // or the OpenSL ES compatibility layer.
            // ---------------------------------------------------------------
            stream_->close();
            stream_.reset();

            stabilizedCallback_ = std::make_shared<oboe::StabilizedCallback>(this);

            builder.setDataCallback(stabilizedCallback_);
            result = builder.openStream(stream_);

            if (result != oboe::Result::OK) {
                LOGE("AAudio + StabilizedCallback open failed (%s), falling back to unspecified API",
                     oboe::convertToText(result));
                { std::lock_guard<std::mutex> eLock(errorLock_); lastError_ = std::string("AAudio: ") + oboe::convertToText(result); }
                builder.setAudioApi(oboe::AudioApi::Unspecified);
                builder.setSharingMode(oboe::SharingMode::Shared);
                result = builder.openStream(stream_);
            }
        }
    } else {
        // AAudio exclusive failed outright — try unspecified API + shared mode.
        // Always wrap with StabilizedCallback on this fallback path since we
        // almost certainly won't have MMAP on an OpenSL ES device.
        LOGE("AAudio open failed (%s), falling back to unspecified API",
             oboe::convertToText(result));
        { std::lock_guard<std::mutex> eLock(errorLock_); lastError_ = std::string("AAudio: ") + oboe::convertToText(result); }

        stabilizedCallback_ = std::make_shared<oboe::StabilizedCallback>(this);
        builder.setDataCallback(stabilizedCallback_);
        builder.setAudioApi(oboe::AudioApi::Unspecified);
        builder.setSharingMode(oboe::SharingMode::Shared);
        result = builder.openStream(stream_);
    }

    if (result != oboe::Result::OK) {
        LOGE("Failed to open Oboe stream: %s", oboe::convertToText(result));
        { std::lock_guard<std::mutex> eLock(errorLock_); lastError_ = std::string("Open failed: ") + oboe::convertToText(result); }
        return false;
    }

    // Enable ADPF (Android Dynamic Performance Framework) hint support.
    // This allows the system to prioritize our audio thread for stable low latency.
    stream_->setPerformanceHintEnabled(true);

    // Start at the minimum possible buffer: exactly 1× burst.
    //
    // On devices with AAudio MMAP support (Pixel 3+, Snapdragon 8 Gen 1+, most
    // modern Android), the MMAP path writes directly to the hardware ring buffer.
    // Starting at 1× burst achieves the minimum possible end-to-end audio latency
    // immediately — no convergence period needed.
    //
    // Previously we started at 2× burst and relied on LatencyTuner to shrink it
    // over ~512ms (128 callbacks × 4ms/callback at 48kHz/192-frame burst).
    // That delay meant users experienced ~8ms extra audio latency for the first
    // half-second of every gameplay session.
    //
    // LatencyTuner is still active and will automatically increase the buffer
    // if underruns occur (backing off to 2× or more as needed), so stability
    // is not compromised on devices that cannot sustain 1× burst.
    stream_->setBufferSizeInFrames(stream_->getFramesPerBurst());

    // Initialise LatencyTuner for dynamic buffer management.
    tuner_ = std::make_unique<oboe::LatencyTuner>(*stream_);

    LOGI("Oboe stream opened: api=%s, sampleRate=%d, framesPerBurst=%d, "
         "bufferSize=%d, bufferCapacity=%d, sharingMode=%s, mmap=%s, stabilized=%s",
         stream_->getAudioApi() == oboe::AudioApi::AAudio ? "AAudio" : "OpenSLES",
         stream_->getSampleRate(),
         stream_->getFramesPerBurst(),
         stream_->getBufferSizeInFrames(),
         stream_->getBufferCapacityInFrames(),
         stream_->getSharingMode() == oboe::SharingMode::Exclusive ? "Exclusive" : "Shared",
         oboe::OboeExtensions::isMMapUsed(stream_.get()) ? "yes" : "no",
         stabilizedCallback_ ? "yes" : "no");

    return true;
}

bool OboeBridge::start() {
    std::lock_guard<std::mutex> lock(streamLock_);

    if (!stream_) {
        LOGE("Cannot start: stream not opened");
        return false;
    }

    oboe::Result result = stream_->requestStart();

    if (result != oboe::Result::OK) {
        LOGE("Failed to start Oboe stream: %s", oboe::convertToText(result));
        { std::lock_guard<std::mutex> eLock(errorLock_); lastError_ = std::string("Start failed: ") + oboe::convertToText(result); }
        return false;
    }

    active_.store(true);
    affinitySet_.store(false);

    // Eagerly compute and cache the big-core mask BEFORE the audio callback runs.
    // This ensures computeBigCoreMask() (which does file I/O via fopen on sysfs)
    // never executes on the real-time audio thread where it could cause latency
    // spikes or priority inversion.
    int mask = cachedBigCoreMask.load(std::memory_order_relaxed);
    if (mask < 0) {
        mask = computeBigCoreMask();
        cachedBigCoreMask.store(mask, std::memory_order_relaxed);
    }

    LOGI("Oboe stream started");
    return true;
}

void OboeBridge::stop() {
    // Signal any in-flight error-callback recovery (onErrorAfterClose →
    // reopenAndRestart) to bail out, so the bridge cannot be reopened from
    // Oboe's internal thread while we are tearing it down from .NET.
    disposing_.store(true);
    active_.store(false);

    std::lock_guard<std::mutex> lock(streamLock_);

    if (stream_) {
        stream_->stop();
        stream_->close();
        stream_.reset();
    }

    tuner_.reset();
    stabilizedCallback_.reset();
    latencyMs_.store(-1.0);
    callbackCount_.store(0);
    LOGI("Oboe stream stopped");
}

double OboeBridge::getOutputLatencyMs() const {
    return latencyMs_.load();
}

double OboeBridge::getInstantLatencyMs() const {
    std::lock_guard<std::mutex> lock(streamLock_);
    if (!stream_) return -1.0;
    // calculateLatencyMillis() is documented as thread-safe in Oboe and is
    // backed by AAudioStream_getTimestamp(). Calling it here (scheduler thread,
    // outside of onAudioReady) is safe: we hold streamLock_ so stream_ cannot
    // be reset underneath us, and the AAudio syscall itself is re-entrant.
    auto result = stream_->calculateLatencyMillis();
    return result ? result.value() : -1.0;
}

bool OboeBridge::isActive() const {
    return active_.load();
}

int32_t OboeBridge::getSampleRate() const {
    std::lock_guard<std::mutex> lock(streamLock_);
    return stream_ ? stream_->getSampleRate() : 0;
}

int32_t OboeBridge::getFramesPerBurst() const {
    std::lock_guard<std::mutex> lock(streamLock_);
    return stream_ ? stream_->getFramesPerBurst() : 0;
}

int32_t OboeBridge::getBufferSizeInFrames() const {
    std::lock_guard<std::mutex> lock(streamLock_);
    return stream_ ? stream_->getBufferSizeInFrames() : 0;
}

bool OboeBridge::isAAudio() const {
    std::lock_guard<std::mutex> lock(streamLock_);
    return stream_ && stream_->getAudioApi() == oboe::AudioApi::AAudio;
}

bool OboeBridge::isMMap() const {
    std::lock_guard<std::mutex> lock(streamLock_);
    return stream_ && oboe::OboeExtensions::isMMapUsed(stream_.get());
}

void OboeBridge::setProvider(OboeAudioProvider provider) {
    provider_.store(provider, std::memory_order_release);
}

std::string OboeBridge::getLastError() const {
    std::lock_guard<std::mutex> lock(errorLock_);
    return lastError_;
}

oboe::DataCallbackResult OboeBridge::onAudioReady(
    oboe::AudioStream* stream, void* audioData, int32_t numFrames) {


    OboeAudioProvider provider = provider_.load(std::memory_order_acquire);

    if (provider) {
        int32_t framesRead = provider(audioData, numFrames);

        // Clamp to valid range: negative or out-of-range values from the provider
        // would wrap to a huge size_t, causing a buffer overrun in the memset below.
        framesRead = std::clamp(framesRead, 0, numFrames);

        if (framesRead < numFrames) {
            // Cache channel count in a local to avoid two virtual dispatches.
            int32_t ch = stream->getChannelCount();
            size_t bytesDone = static_cast<size_t>(framesRead) * ch * sizeof(float);
            size_t totalBytes = static_cast<size_t>(numFrames) * ch * sizeof(float);
            memset(static_cast<char*>(audioData) + bytesDone, 0, totalBytes - bytesDone);
        }
    } else {
        size_t byteCount = static_cast<size_t>(numFrames)
                         * static_cast<size_t>(stream->getChannelCount())
                         * sizeof(float);
        memset(audioData, 0, byteCount);
    }


    uint32_t count = callbackCount_.fetch_add(1, std::memory_order_relaxed);

    // LatencyTuner once every 128 callbacks (~1.5s @ 192 burst, 48 kHz).
    // updateLatency() issues an AAudio syscall, so throttle it further to every
    // 256 callbacks — Tab still sees stable values, but we cut audio-thread
    // syscall pressure in half.
    if ((count & 127) == 0) {
        // Dynamically tune the buffer size to the lowest stable value.
        if (tuner_) {
            tuner_->tune();
        }

        if ((count & 255) == 0)
            updateLatency();

        // Attempt to set CPU affinity to high-performance cores.
        // We do this inside the audio callback to ensure we target the AAudio thread.
        // Uses sysfs-based topology detection for accurate big-core identification
        // across all SoC vendors (Snapdragon, Exynos, Dimensity, Tensor).
        if (!affinitySet_.load(std::memory_order_relaxed)) {
            // cachedBigCoreMask is eagerly computed in start(), so this load
            // should always return >= 0.  The < 0 branch is a defensive fallback
            // that avoids file I/O — it uses the generic upper-half heuristic
            // instead of calling computeBigCoreMask() on the audio thread.
            int bigMask = cachedBigCoreMask.load(std::memory_order_relaxed);
            if (bigMask < 0) {
                // Defensive: sysfs was never read (should not happen).
                // Use upper-half heuristic instead of doing file I/O here.
                int num_cores = sysconf(_SC_NPROCESSORS_CONF);
                bigMask = 0;
                if (num_cores > 1) {
                    for (int i = num_cores / 2; i < std::min(num_cores, 32); ++i)
                        bigMask |= (1 << i);
                }
                cachedBigCoreMask.store(bigMask, std::memory_order_relaxed);
            }

            if (bigMask > 0) {
                cpu_set_t cpuset;
                CPU_ZERO(&cpuset);
                for (int i = 0; i < 32; i++) {
                    if ((bigMask >> i) & 1)
                        CPU_SET(i, &cpuset);
                }

                if (sched_setaffinity(0, sizeof(cpu_set_t), &cpuset) == 0) {
                    LOGI("Oboe audio thread pinned to big cores (mask=0x%x)", bigMask);
                } else {
                    LOGE("Failed to set audio thread affinity: %d", errno);
                }
            } else {
                // Fallback: try upper half of cores if sysfs was unreadable
                int num_cores = sysconf(_SC_NPROCESSORS_CONF);
                if (num_cores > 1) {
                    cpu_set_t cpuset;
                    CPU_ZERO(&cpuset);
                    for (int i = num_cores / 2; i < num_cores; ++i)
                        CPU_SET(i, &cpuset);
                    sched_setaffinity(0, sizeof(cpu_set_t), &cpuset);
                    LOGI("Oboe audio thread: sysfs unavailable, used upper-half fallback");
                }
            }
            affinitySet_.store(true);
        }
    }

    return oboe::DataCallbackResult::Continue;
}

void OboeBridge::onErrorBeforeClose(oboe::AudioStream* stream, oboe::Result error) {
    LOGE("Oboe error before close: %s", oboe::convertToText(error));
    active_.store(false);
}

void OboeBridge::onErrorAfterClose(oboe::AudioStream* stream, oboe::Result error) {
    LOGE("Oboe error after close: %s — attempting automatic recovery",
         oboe::convertToText(error));
    active_.store(false);

    // Bail out immediately if a teardown is in flight: stop() has signalled
    // that the bridge is being destroyed by .NET, and proceeding with the
    // recovery path could leave us inside open()/requestStart() while the
    // OboeBridge object is freed by the destructor.
    if (disposing_.load()) {
        std::lock_guard<std::mutex> lock(streamLock_);
        stream_.reset();
        return;
    }

    if (error == oboe::Result::ErrorDisconnected) {
        {
            std::lock_guard<std::mutex> lock(streamLock_);
            stream_.reset();
        }

        if (reopenAndRestart()) {
            LOGI("Oboe stream recovered successfully after disconnect");
        } else {
            LOGE("Oboe stream recovery failed");
        }
    } else {
        std::lock_guard<std::mutex> lock(streamLock_);
        stream_.reset();
    }
}

bool OboeBridge::reopenAndRestart() {
    // Re-check teardown after acquiring no-lock fast path: stop() may have
    // been called between onErrorAfterClose's check and now.
    if (disposing_.load()) return false;

    if (open(requestedSampleRate_)) {
        std::lock_guard<std::mutex> lock(streamLock_);

        if (disposing_.load()) {
            stream_.reset();
            return false;
        }

        if (stream_) {
            oboe::Result result = stream_->requestStart();

            if (result == oboe::Result::OK) {
                active_.store(true);
                return true;
            }

            LOGE("Failed to restart recovered stream: %s", oboe::convertToText(result));
        }
    }

    return false;
}

void OboeBridge::updateLatency() {
    if (!stream_) return;

    auto result = stream_->calculateLatencyMillis();

    if (result) {
        latencyMs_.store(result.value());
    }
}

// ============================================================
// C exports for P/Invoke from .NET
// ============================================================

#define OSU_EXPORT __attribute__((visibility("default")))

extern "C" {

OSU_EXPORT intptr_t nOboeCreate(int sampleRate) {
    auto* bridge = new (std::nothrow) OboeBridge();

    if (!bridge) return 0;

    if (!bridge->open(sampleRate)) {
        delete bridge;
        return 0;
    }

    return reinterpret_cast<intptr_t>(bridge);
}

OSU_EXPORT void nOboeDestroy(intptr_t ptr) {
    if (ptr) delete reinterpret_cast<OboeBridge*>(ptr);
}

OSU_EXPORT byte nOboeStart(intptr_t ptr) {
    auto* bridge = reinterpret_cast<OboeBridge*>(ptr);
    return (bridge && bridge->start()) ? 1 : 0;
}

OSU_EXPORT void nOboeStop(intptr_t ptr) {
    auto* bridge = reinterpret_cast<OboeBridge*>(ptr);
    if (bridge) bridge->stop();
}

OSU_EXPORT double nOboeGetLatencyMs(intptr_t ptr) {
    auto* bridge = reinterpret_cast<OboeBridge*>(ptr);
    return bridge ? bridge->getOutputLatencyMs() : -1.0;
}

OSU_EXPORT double nOboeGetInstantLatencyMs(intptr_t ptr) {
    auto* bridge = reinterpret_cast<OboeBridge*>(ptr);
    return bridge ? bridge->getInstantLatencyMs() : -1.0;
}

OSU_EXPORT byte nOboeIsActive(intptr_t ptr) {
    auto* bridge = reinterpret_cast<OboeBridge*>(ptr);
    return (bridge && bridge->isActive()) ? 1 : 0;
}

OSU_EXPORT int nOboeGetSampleRate(intptr_t ptr) {
    auto* bridge = reinterpret_cast<OboeBridge*>(ptr);
    return bridge ? bridge->getSampleRate() : 0;
}

OSU_EXPORT int nOboeGetFramesPerBurst(intptr_t ptr) {
    auto* bridge = reinterpret_cast<OboeBridge*>(ptr);
    return bridge ? bridge->getFramesPerBurst() : 0;
}

OSU_EXPORT int nOboeGetBufferSizeInFrames(intptr_t ptr) {
    auto* bridge = reinterpret_cast<OboeBridge*>(ptr);
    return bridge ? bridge->getBufferSizeInFrames() : 0;
}

OSU_EXPORT byte nOboeIsAAudio(intptr_t ptr) {
    auto* bridge = reinterpret_cast<OboeBridge*>(ptr);
    return (bridge && bridge->isAAudio()) ? 1 : 0;
}

OSU_EXPORT byte nOboeIsMMap(intptr_t ptr) {
    auto* bridge = reinterpret_cast<OboeBridge*>(ptr);
    return (bridge && bridge->isMMap()) ? 1 : 0;
}

OSU_EXPORT void nOboeSetProvider(intptr_t ptr, OboeAudioProvider provider) {
    auto* bridge = reinterpret_cast<OboeBridge*>(ptr);
    if (bridge) bridge->setProvider(provider);
}

OSU_EXPORT const char* nOboeGetLastErrorMessage(intptr_t ptr) {
    auto* bridge = reinterpret_cast<OboeBridge*>(ptr);
    if (!bridge) return nullptr;

    // Hold a thread_local snapshot so the pointer we hand back to managed code
    // remains valid for the duration of the P/Invoke marshalling step, even if
    // another thread (Oboe error callback) overwrites `lastError_` immediately
    // after we return.  Each managed thread gets its own buffer.
    thread_local std::string snapshot;
    snapshot = bridge->getLastError();
    return snapshot.empty() ? nullptr : snapshot.c_str();
}

} // extern "C"

extern "C" {
OSU_EXPORT void nLog(int level, const char* tag, const char* msg) {
    __android_log_print(level, tag, "%s", msg);
}
}

extern "C" {
OSU_EXPORT byte nSetThreadAffinity(int coreMask) {
    cpu_set_t cpuset;
    CPU_ZERO(&cpuset);
    for (int i = 0; i < 32; i++) {
        if ((coreMask >> i) & 1) {
            CPU_SET(i, &cpuset);
        }
    }
    return (sched_setaffinity(0, sizeof(cpu_set_t), &cpuset) == 0) ? 1 : 0;
}

OSU_EXPORT int nGetBigCoreMask() {
    int mask = cachedBigCoreMask.load(std::memory_order_relaxed);
    if (mask < 0) {
        mask = computeBigCoreMask();
        cachedBigCoreMask.store(mask, std::memory_order_relaxed);
    }
    return mask;
}

// ============================================================
// Background-thread taming (field-crash mitigation — see below)
// ============================================================
// Android cold-start black-screen / MotionEvent ANR (v177) was root-caused to
// Veldrid's shader-compile worker running glslang::TPpContext::tokenize deep
// inside glslang::SetupBuiltinSymbolTable at nice=-10 on a big core, starving
// the Android main UI thread of CPU at the same moment the Draw thread is
// draining a 300+-item texture-upload queue. Mono maps .NET
// ThreadPriority.Highest to nice=-10 for every worker thread it spawns
// (shader compile, finalizer, network, etc.), which is the display-
// compositor priority class — inappropriate for CPU-heavy background work.
//
// This function walks /proc/self/task, reads each thread's kernel comm, and
// for any comm matching the Mono-threadpool-worker naming pattern drops the
// nice value to 0 and (if little_core_mask != 0) pins the thread to the
// given LITTLE-core subset. Game-loop threads (Update/Draw/Audio/Input), the
// Android main UI thread (tid == tgid), the calling thread, and a small
// list of critical ART/system daemons are explicitly left alone.
//
// Safe to call repeatedly; subsequent calls are idempotent. Cross-thread
// setpriority / sched_setaffinity within the same process is allowed for a
// same-euid caller without CAP_SYS_NICE, so no root required.
//
// Returns: number of threads demoted (for diagnostic logging).
static bool isCommToLeaveAlone(const char* comm) {
    if (!comm || !*comm) return false;

    // Game-loop threads created by osu-framework GameThread. These MUST stay
    // at their elevated priority so the render/update/audio/input pipelines
    // aren't starved by Android's scheduler during play.
    static const char* const keep[] = {
        "Update",       "Draw",        "Audio",        "Input",
        "SDLActivity",  "HangWatchdog",
        // Known-critical ART / Android daemons; leave to platform defaults.
        "FinalizerDae", "FinalizerWat", "ReferenceQueu", "HeapTaskDaemo",
        "Signal Catche", "Jit thread po", "Profile Saver", "binder:",
        "perfetto",     "main",
        // Oboe / AAudio callback threads (priority-critical for audio).
        "AAudio",       "OboeAudio",
        // GPU driver workers — MUST be left alone for Vulkan to ever present a
        // frame on Adreno / Mali / Xclipse. The driver spawns its own worker
        // pool around vkCreateInstance / vkCreateSwapchainKHR, and demoting any
        // of these to LITTLE cores at nice=0 reliably stalls vkQueuePresentKHR
        // on the Draw thread (visible in field logs as "Update tick 1, Draw
        // tick 0" → black-screen ANR on Vulkan launches). Names below cover the
        // observed comms across vendors:
        //   - Qualcomm Adreno user-space driver: "qcom-",  "kgsl-",
        //     "Adreno",   "GraphicsWor",  "queue-msm",  "QSEECOMD",
        //     "RenderEngine", "MaliCmdStream"
        //   - ARM Mali blob driver:            "mali-",  "MaliWorker",
        //     "ARM-MaliGPU"
        //   - Samsung Xclipse / AMD RDNA3:     "xclipse",  "RGP-",  "AMDVLK"
        //   - Generic Vulkan loader / SwiftShader: "Swift",  "vk-loader"
        // Match by short prefix to be tolerant of vendor suffix differences.
        "qcom-",        "kgsl-",        "Adreno",       "GraphicsWor",
        "queue-msm",    "QSEECOMD",     "RenderEngine", "MaliCmdStream",
        "mali-",        "MaliWorker",   "ARM-MaliGPU",
        "xclipse",      "RGP-",         "AMDVLK",
        "Swift",        "vk-loader",
        // Veldrid + glslang/SPIRV-cross workers MUST also keep big-core
        // affinity when Vulkan is the active renderer — they sit in the
        // critical path of vkCreateGraphicsPipelines, which the Draw thread
        // blocks on waiting for the first frame. We can't reliably tell
        // renderer at this layer, so the cheap fix is to leave them alone
        // unconditionally — on OpenGL/ANGLE the same workers are dormant
        // (ANGLE compiles GLSL directly) so this is a no-op there.
        "glslang",      "SPIRV-",       "Veldrid",
    };

    for (const char* p : keep) {
        if (std::strncmp(comm, p, std::strlen(p)) == 0)
            return true;
    }

    return false;
}

static bool isCommToDemote(const char* comm) {
    if (!comm) return false;

    // Empty comm (unnamed thread) — definitely safe to demote; these are
    // ad-hoc pthread_create workers that inherited nice=-10 from a parent.
    if (*comm == '\0') return true;

    // Mono threadpool worker default name: "Thread-<n>". This is the thread
    // that was stuck in glslang::SetupBuiltinSymbolTable in the field
    // tombstone; it's also used for network / shader / JIT helpers.
    if (std::strncmp(comm, "Thread-", 7) == 0) return true;

    // OkHttp / Okio network threads — nice=-8 observed in tombstones, no
    // reason to outrank the main UI thread during cold start.
    if (std::strncmp(comm, "OkHttp",     6) == 0) return true;
    if (std::strncmp(comm, "Okio",       4) == 0) return true;

    // .NET threadpool default pattern ("pool-", ".NET Thread", "TP").
    if (std::strncmp(comm, "pool-",      5) == 0) return true;
    if (std::strncmp(comm, ".NET",       4) == 0) return true;

    return false;
}

OSU_EXPORT int nTameBackgroundThreads(int little_core_mask) {
    DIR* dir = opendir("/proc/self/task");
    if (!dir) return 0;

    const pid_t self_tid = (pid_t)syscall(SYS_gettid);
    const pid_t tgid = getpid();

    cpu_set_t cpuset;
    CPU_ZERO(&cpuset);
    bool haveCpuset = false;
    if (little_core_mask != 0) {
        for (int i = 0; i < 32; i++) {
            if ((little_core_mask >> i) & 1)
                CPU_SET(i, &cpuset);
        }
        haveCpuset = CPU_COUNT(&cpuset) > 0;
    }

    int demoted = 0;
    struct dirent* ent;
    while ((ent = readdir(dir)) != nullptr) {
        if (ent->d_name[0] < '0' || ent->d_name[0] > '9') continue;

        pid_t tid = (pid_t)std::atoi(ent->d_name);
        if (tid <= 0) continue;
        if (tid == self_tid) continue;
        // Never touch the Android main UI thread — Android's input dispatcher
        // reads from it, and any priority/affinity mutation here is exactly
        // the class of change that causes a 10s MotionEvent ANR.
        if (tid == tgid) continue;

        char comm_path[64];
        std::snprintf(comm_path, sizeof(comm_path), "/proc/self/task/%d/comm", (int)tid);
        int fd = open(comm_path, O_RDONLY | O_CLOEXEC);
        if (fd < 0) continue;

        char comm[32] = {0};
        ssize_t n = read(fd, comm, sizeof(comm) - 1);
        close(fd);
        if (n <= 0) continue;
        // Strip trailing newline that the kernel appends.
        if (comm[n - 1] == '\n') comm[n - 1] = '\0';

        if (isCommToLeaveAlone(comm)) continue;
        if (!isCommToDemote(comm)) continue;

        bool changed = false;

        // Raise nice from whatever it is (often -10 for Mono ThreadPriority.Highest)
        // to 0. setpriority(PRIO_PROCESS, tid, 0) is a de-elevation and
        // therefore does not require CAP_SYS_NICE for a same-euid caller.
        if (setpriority(PRIO_PROCESS, tid, 0) == 0)
            changed = true;

        if (haveCpuset) {
            if (sched_setaffinity(tid, sizeof(cpu_set_t), &cpuset) == 0)
                changed = true;
        }

        if (changed) demoted++;
    }
    closedir(dir);
    return demoted;
}
}
