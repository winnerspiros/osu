// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#include "oboe_bridge.h"
#include <android/log.h>
#include <unistd.h>
#include <sched.h>
#include <errno.h>
#include <cstring>
#include <cstdint>

#define LOG_TAG "osu-oboe"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN, LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)

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
    oboe::OboeExtensions::setMMapEnabled(true);

    // Initialise StabilizedCallback to even out callback execution time.
    stabilizedCallback_ = std::make_unique<oboe::StabilizedCallback>(this);

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
           ->setFramesPerCallback(oboe::kUnspecified)
           ->setBufferCapacityInFrames(oboe::kUnspecified)
           ->setChannelConversionAllowed(false)
           ->setFormatConversionAllowed(false)
           ->setCallback(stabilizedCallback_.get());

    oboe::Result result = builder.openStream(stream_);

    // Fallback: If Exclusive failed, try Shared.
    if (result != oboe::Result::OK) {
        LOGW("Exclusive AAudio open failed (%s), falling back to Shared", oboe::convertToText(result));
        builder.setSharingMode(oboe::SharingMode::Shared);
        result = builder.openStream(stream_);
    }

    // Fallback: If AAudio still failed (or Shared failed), try Unspecified (likely OpenSL ES).
    if (result != oboe::Result::OK) {
        LOGE("AAudio open failed (%s), falling back to unspecified API (OpenSL ES)", oboe::convertToText(result));
        builder.setAudioApi(oboe::AudioApi::Unspecified);
        builder.setSharingMode(oboe::SharingMode::Shared); // OpenSL ES usually prefers Shared.
        result = builder.openStream(stream_);
    }

    lastResult_.store(result);

    if (result != oboe::Result::OK) {
        LOGE("Failed to open Oboe stream: %s", oboe::convertToText(result));
        return false;
    }

    // Enable ADPF (Android Dynamic Performance Framework) hint support.
    stream_->setPerformanceHintEnabled(true);

    // Set buffer size to 3x burst size for initial stability as requested by user.
    // LatencyTuner will then attempt to shrink it to 1x burst if stable.
    stream_->setBufferSizeInFrames(stream_->getFramesPerBurst() * 3);

    // Initialise LatencyTuner for dynamic buffer management.
    tuner_ = std::make_unique<oboe::LatencyTuner>(*stream_);

    LOGI("Oboe stream opened: api=%s, sampleRate=%d, framesPerBurst=%d, "
         "bufferSize=%d, bufferCapacity=%d, sharingMode=%s, mmap=%s",
         stream_->getAudioApi() == oboe::AudioApi::AAudio ? "AAudio" : "OpenSLES",
         stream_->getSampleRate(),
         stream_->getFramesPerBurst(),
         stream_->getBufferSizeInFrames(),
         stream_->getBufferCapacityInFrames(),
         stream_->getSharingMode() == oboe::SharingMode::Exclusive ? "Exclusive" : "Shared",
         oboe::OboeExtensions::isMMapUsed(stream_.get()) ? "yes" : "no");

    return true;
}

bool OboeBridge::start() {
    std::lock_guard<std::mutex> lock(streamLock_);

    if (!stream_) {
        LOGE("Cannot start: stream not opened");
        return false;
    }

    oboe::Result result = stream_->requestStart();
    lastResult_.store(result);

    if (result != oboe::Result::OK) {
        LOGE("Failed to start Oboe stream: %s", oboe::convertToText(result));
        return false;
    }

    active_.store(true);
    affinitySet_.store(false);
    LOGI("Oboe stream started");
    return true;
}

void OboeBridge::stop() {
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

bool OboeBridge::isActive() const {
    return active_.load();
}

int32_t OboeBridge::getSampleRate() const {
    std::lock_guard<std::mutex> lock(const_cast<std::mutex&>(streamLock_));
    return stream_ ? stream_->getSampleRate() : 0;
}

int32_t OboeBridge::getFramesPerBurst() const {
    std::lock_guard<std::mutex> lock(const_cast<std::mutex&>(streamLock_));
    return stream_ ? stream_->getFramesPerBurst() : 0;
}

int32_t OboeBridge::getBufferSizeInFrames() const {
    std::lock_guard<std::mutex> lock(const_cast<std::mutex&>(streamLock_));
    return stream_ ? stream_->getBufferSizeInFrames() : 0;
}

bool OboeBridge::isAAudio() const {
    std::lock_guard<std::mutex> lock(const_cast<std::mutex&>(streamLock_));
    return stream_ && stream_->getAudioApi() == oboe::AudioApi::AAudio;
}

bool OboeBridge::isMMap() const {
    std::lock_guard<std::mutex> lock(const_cast<std::mutex&>(streamLock_));
    return stream_ && oboe::OboeExtensions::isMMapUsed(stream_.get());
}

void OboeBridge::setProvider(OboeAudioProvider provider) {
    provider_.store(provider, std::memory_order_release);
}

const char* OboeBridge::getLastErrorMessage() const {
    return oboe::convertToText(lastResult_.load());
}

oboe::DataCallbackResult OboeBridge::onAudioReady(
    oboe::AudioStream* stream, void* audioData, int32_t numFrames) {

    OboeAudioProvider provider = provider_.load(std::memory_order_acquire);

    if (provider) {
        int32_t framesRead = provider(audioData, numFrames);

        if (framesRead < numFrames) {
            size_t bytesDone = static_cast<size_t>(framesRead) * stream->getChannelCount() * sizeof(float);
            size_t totalBytes = static_cast<size_t>(numFrames) * stream->getChannelCount() * sizeof(float);
            memset(static_cast<char*>(audioData) + bytesDone, 0, totalBytes - bytesDone);
        }
    } else {
        size_t byteCount = static_cast<size_t>(numFrames)
                         * static_cast<size_t>(stream->getChannelCount())
                         * sizeof(float);
        memset(audioData, 0, byteCount);
    }

    uint32_t count = callbackCount_.fetch_add(1, std::memory_order_relaxed);

    if ((count & 127) == 0) {
        updateLatency();

        if (tuner_) {
            tuner_->tune();
        }

        if (!affinitySet_.load(std::memory_order_relaxed)) {
            cpu_set_t cpuset;
            CPU_ZERO(&cpuset);

            int num_cores = sysconf(_SC_NPROCESSORS_CONF);
            if (num_cores > 0) {
                if (num_cores >= 8) {
                    for (int i = 3; i < num_cores; ++i) {
                        CPU_SET(i, &cpuset);
                    }
                } else {
                    for (int i = num_cores / 2; i < num_cores; ++i) {
                        CPU_SET(i, &cpuset);
                    }
                }

                if (sched_setaffinity(0, sizeof(cpu_set_t), &cpuset) == 0) {
                    LOGI("Oboe audio thread pinned to high-performance cores");
                } else {
                    LOGE("Failed to set thread affinity: %d", errno);
                }
            }
            affinitySet_.store(true);
        }
    }

    return oboe::DataCallbackResult::Continue;
}

void OboeBridge::onErrorBeforeClose(oboe::AudioStream* stream, oboe::Result error) {
    LOGE("Oboe error before close: %s", oboe::convertToText(error));
    lastResult_.store(error);
    active_.store(false);
}

void OboeBridge::onErrorAfterClose(oboe::AudioStream* stream, oboe::Result error) {
    LOGE("Oboe error after close: %s — attempting automatic recovery",
         oboe::convertToText(error));
    lastResult_.store(error);
    active_.store(false);

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
    if (open(requestedSampleRate_)) {
        std::lock_guard<std::mutex> lock(streamLock_);

        if (stream_) {
            oboe::Result result = stream_->requestStart();
            lastResult_.store(result);

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
    return bridge ? bridge->getLastErrorMessage() : "Null bridge";
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
}

#include <android/performance_hint.h>

extern "C" {
OSU_EXPORT intptr_t nADPFCreateSession(int64_t targetDurationNanos) {
    auto manager = APerformanceHint_getManager();
    if (!manager) return 0;

    int32_t thread_id = gettid();
    return reinterpret_cast<intptr_t>(APerformanceHint_createSession(manager, &thread_id, 1, targetDurationNanos));
}

OSU_EXPORT void nADPFReportActualDuration(intptr_t sessionPtr, int64_t actualDurationNanos) {
    if (sessionPtr) {
        APerformanceHint_reportActualWorkDuration(reinterpret_cast<APerformanceHintSession*>(sessionPtr), actualDurationNanos);
    }
}

OSU_EXPORT void nADPFUpdateTargetDuration(intptr_t sessionPtr, int64_t targetDurationNanos) {
    if (sessionPtr) {
        APerformanceHint_updateTargetWorkDuration(reinterpret_cast<APerformanceHintSession*>(sessionPtr), targetDurationNanos);
    }
}

OSU_EXPORT void nADPFCloseSession(intptr_t sessionPtr) {
    if (sessionPtr) {
        APerformanceHint_closeSession(reinterpret_cast<APerformanceHintSession*>(sessionPtr));
    }
}
}
