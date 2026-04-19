// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#pragma once

#include <oboe/Oboe.h>
#include <oboe/LatencyTuner.h>
#include <oboe/StabilizedCallback.h>
#include <atomic>
#include <mutex>
#include <functional>
#include <memory>

/// Callback function type for providing PCM audio data to the Oboe stream.
/// Returns the number of frames actually written to the buffer.
typedef int32_t (*OboeAudioProvider)(void* audioData, int32_t numFrames);

/// Low-latency audio bridge using Google's Oboe library.
class OboeBridge : public oboe::AudioStreamCallback {
public:
    OboeBridge();
    ~OboeBridge();

    bool open(int32_t sampleRate = 0);
    bool start();
    void stop();

    double getOutputLatencyMs() const;
    bool isActive() const;
    int32_t getSampleRate() const;
    int32_t getFramesPerBurst() const;
    int32_t getBufferSizeInFrames() const;
    bool isAAudio() const;
    bool isMMap() const;
    void setProvider(OboeAudioProvider provider);
    /// Returns a copy of the most recent error message under lock.  We return
    /// by value (not a pointer to internal storage) so callers can't observe a
    /// torn or freed `std::string` if another thread mutates `lastError_`
    /// concurrently (Oboe error callbacks fire from an internal thread).
    std::string getLastError() const;

    // oboe::AudioStreamCallback
    oboe::DataCallbackResult onAudioReady(
        oboe::AudioStream* stream, void* audioData, int32_t numFrames) override;

    void onErrorBeforeClose(oboe::AudioStream* stream, oboe::Result error) override;
    void onErrorAfterClose(oboe::AudioStream* stream, oboe::Result error) override;

private:
    std::shared_ptr<oboe::AudioStream> stream_;
    std::shared_ptr<oboe::StabilizedCallback> stabilizedCallback_;
    std::unique_ptr<oboe::LatencyTuner> tuner_;

    mutable std::mutex streamLock_;
    std::atomic<bool> active_{false};
    std::atomic<double> latencyMs_{-1.0};
    std::atomic<uint32_t> callbackCount_{0};
    std::atomic<OboeAudioProvider> provider_{nullptr};
    std::atomic<bool> affinitySet_{false};
    int32_t requestedSampleRate_{0};
    std::string lastError_;
    mutable std::mutex errorLock_;

    void updateLatency();
    bool reopenAndRestart();
};
