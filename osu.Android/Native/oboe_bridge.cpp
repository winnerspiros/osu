// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#include "oboe_bridge.h"
#include <oboe/OboeExtensions.h>
#include <oboe/AudioClock.h>
#include <oboe/Process.h>
#include <android/log.h>
#include <cstdint>
#include <cstring>
#include <vector>

#define LOG_TAG "osu!native"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
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
    // We create it here so we can pass it to the builder.
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

    if (result != oboe::Result::OK) {
        LOGE("AAudio open failed (%s), falling back to unspecified API",
             oboe::convertToText(result));
        builder.setAudioApi(oboe::AudioApi::Unspecified);
        result = builder.openStream(stream_);
    }

    if (result != oboe::Result::OK) {
        LOGE("Failed to open Oboe stream: %s", oboe::convertToText(result));
        return false;
    }

    // Enable ADPF (Android Dynamic Performance Framework) hint support.
    stream_->setPerformanceHintEnabled(true);

    // Initialise LatencyTuner for dynamic buffer management.
    // This allows us to start at 1x burst and only grow if underruns occur.
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

oboe::DataCallbackResult OboeBridge::onAudioReady(
    oboe::AudioStream* stream, void* audioData, int32_t numFrames) {

    // Record the start time of this callback for ADPF work duration reporting.
    int64_t startTime = oboe::AudioClock::getNanoseconds();

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

    // Reporting actual work duration helps ADPF (Android Dynamic Performance Framework)
    // adjust CPU frequency precisely to handle the audio load without skipping.
    int64_t endTime = oboe::AudioClock::getNanoseconds();
    stream->reportActualWorkDuration(endTime - startTime);

    uint32_t count = callbackCount_.fetch_add(1, std::memory_order_relaxed);

    if ((count & 127) == 0) {
        updateLatency();

        // Dynamically tune the buffer size to the lowest stable value.
        if (tuner_) {
            tuner_->tune();
        }

        // Attempt to set CPU affinity to high-performance cores.
        if (!affinitySet_.load(std::memory_order_relaxed)) {
            std::vector<int> exclusiveCores = oboe::Process::getExclusiveCores();

            if (!exclusiveCores.empty()) {
                oboe::Result result = oboe::Process::setThreadAffinity(
                    oboe::Process::getThreadId(),
                    exclusiveCores);

                if (result == oboe::Result::OK) {
                    LOGI("Oboe audio thread pinned to exclusive cores");
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

OSU_EXPORT unsigned char nOboeStart(intptr_t ptr) {
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

OSU_EXPORT unsigned char nOboeIsActive(intptr_t ptr) {
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

OSU_EXPORT unsigned char nOboeIsAAudio(intptr_t ptr) {
    auto* bridge = reinterpret_cast<OboeBridge*>(ptr);
    return (bridge && bridge->isAAudio()) ? 1 : 0;
}

OSU_EXPORT unsigned char nOboeIsMMap(intptr_t ptr) {
    auto* bridge = reinterpret_cast<OboeBridge*>(ptr);
    return (bridge && bridge->isMMap()) ? 1 : 0;
}

OSU_EXPORT void nOboeSetProvider(intptr_t ptr, OboeAudioProvider provider) {
    auto* bridge = reinterpret_cast<OboeBridge*>(ptr);
    if (bridge) bridge->setProvider(provider);
}

} // extern "C"
