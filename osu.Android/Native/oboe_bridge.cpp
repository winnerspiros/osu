// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#include "oboe_bridge.h"
#include <oboe/OboeExtensions.h>
#include <android/log.h>
#include <cstdint>
#include <cstring>

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

bool OboeBridge::open() {
    std::lock_guard<std::mutex> lock(streamLock_);

    // Request MMAP mode globally before opening the stream.
    // MMAP provides a hardware-level DMA path that bypasses the kernel audio
    // copy, shaving ~1-2 ms off the round-trip latency on supported devices.
    oboe::OboeExtensions::setMMapEnabled(true);

    oboe::AudioStreamBuilder builder;
    builder.setDirection(oboe::Direction::Output)
           ->setPerformanceMode(oboe::PerformanceMode::LowLatency)
           ->setSharingMode(oboe::SharingMode::Exclusive)
           ->setFormat(oboe::AudioFormat::Float)
           // Mono — this stream outputs silence for latency measurement only.
           // Mono halves the per-callback buffer vs stereo, reducing the
           // minimum achievable latency.
           ->setChannelCount(oboe::ChannelCount::Mono)
           // Let Oboe pick the device's native sample rate.
           // Hardcoding (e.g. 48000) would force Android's SRC resampler when the
           // device native rate differs, adding measurable latency.
           ->setSampleRate(oboe::kUnspecified)
           // Explicitly forbid all internal conversions so that no resampler,
           // channel mixer, or format converter sits in the audio path.
           ->setChannelConversionAllowed(false)
           ->setFormatConversionAllowed(false)
           ->setSampleRateConversionQuality(oboe::SampleRateConversionQuality::None)
           // Semantic hints help Android route through the optimal audio path.
           ->setContentType(oboe::ContentType::Music)
           ->setUsage(oboe::Usage::Game)
           ->setCallback(this)
           // Prefer AAudio for lowest latency (available on Android 8.1+).
           // Falls back to OpenSL ES automatically on older devices.
           ->setAudioApi(oboe::AudioApi::AAudio)
           // Request minimum buffer for lowest latency.
           // Oboe will clamp to the smallest safe value.
           ->setFramesPerCallback(oboe::kUnspecified)
           ->setBufferCapacityInFrames(oboe::kUnspecified);

    oboe::Result result = builder.openStream(stream_);

    if (result != oboe::Result::OK) {
        // AAudio might not be available; retry without API preference.
        LOGI("AAudio open failed (%s), falling back to unspecified API",
             oboe::convertToText(result));
        builder.setAudioApi(oboe::AudioApi::Unspecified);
        result = builder.openStream(stream_);
    }

    if (result != oboe::Result::OK) {
        LOGE("Failed to open Oboe stream: %s", oboe::convertToText(result));
        return false;
    }

    optimiseBufferSize();

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

void OboeBridge::optimiseBufferSize() {
    if (!stream_) return;

    // Set buffer size to exactly 1× burst for minimum latency.
    // This gives the tightest possible callback schedule.
    int32_t burst = stream_->getFramesPerBurst();

    if (burst > 0) {
        auto setResult = stream_->setBufferSizeInFrames(burst);

        if (setResult) {
            LOGI("Buffer size tuned to %d frames (1x burst)", setResult.value());
        }
    }
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

    OboeAudioProvider provider = provider_.load(std::memory_order_acquire);

    if (provider) {
        int32_t framesRead = provider(audioData, numFrames);

        if (framesRead < numFrames) {
            // Fill remaining buffer with silence if provider didn't return enough data.
            size_t bytesDone = static_cast<size_t>(framesRead) * stream->getChannelCount() * sizeof(float);
            size_t totalBytes = static_cast<size_t>(numFrames) * stream->getChannelCount() * sizeof(float);
            memset(static_cast<char*>(audioData) + bytesDone, 0, totalBytes - bytesDone);
        }
    } else {
        // Fallback to silence if no provider is registered.
        size_t byteCount = static_cast<size_t>(numFrames)
                         * static_cast<size_t>(stream->getChannelCount())
                         * sizeof(float);
        memset(audioData, 0, byteCount);
    }

    // Sample latency every 128 callbacks (~250 ms at typical burst/sample rates)
    // instead of every single callback. calculateLatencyMillis() issues a
    // system call; keeping it out of the majority of callbacks reduces jitter
    // in this real-time audio thread.
    if ((callbackCount_.fetch_add(1, std::memory_order_relaxed) & 127) == 0) {
        updateLatency();
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

    // Automatic stream recovery: re-open and restart on disconnect / route change.
    // This is critical for maintaining low-latency audio when headphones are
    // plugged/unplugged or Bluetooth devices connect/disconnect.
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
    if (open()) {
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

// Use intptr_t for pointer handles so the size matches C# IntPtr on both
// 32-bit (4 bytes) and 64-bit (8 bytes) platforms.  The previous use of
// C++ `long` was 4 bytes on 32-bit ARM/x86 but C# `long` is always
// 8 bytes, causing a calling-convention mismatch and crash.

#define OSU_EXPORT __attribute__((visibility("default")))

extern "C" {

OSU_EXPORT intptr_t nOboeCreate() {
    auto* bridge = new (std::nothrow) OboeBridge();

    if (!bridge) return 0;

    if (!bridge->open()) {
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
