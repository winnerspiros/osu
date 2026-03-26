// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#pragma once

#include <oboe/Oboe.h>
#include <atomic>

/// Low-latency audio bridge using Google's Oboe library.
/// Provides an AAudio/OpenSL ES output stream with accurate latency reporting
/// for rhythm-game audio-visual synchronization.
class OboeBridge : public oboe::AudioStreamCallback {
public:
    OboeBridge();
    ~OboeBridge();

    bool open();
    bool start();
    void stop();

    /// Returns the measured output latency in milliseconds, or -1 if unavailable.
    double getOutputLatencyMs() const;

    /// Returns true if the stream is currently active.
    bool isActive() const;

    // oboe::AudioStreamCallback
    oboe::DataCallbackResult onAudioReady(
        oboe::AudioStream* stream, void* audioData, int32_t numFrames) override;

    void onErrorBeforeClose(oboe::AudioStream* stream, oboe::Result error) override;
    void onErrorAfterClose(oboe::AudioStream* stream, oboe::Result error) override;

private:
    std::shared_ptr<oboe::AudioStream> stream_;
    std::atomic<bool> active_{false};
    std::atomic<double> latencyMs_{-1.0};

    void updateLatency();
};
