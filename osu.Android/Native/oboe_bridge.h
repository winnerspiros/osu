// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#pragma once

#include <oboe/Oboe.h>
#include <atomic>
#include <mutex>

/// Low-latency audio bridge using Google's Oboe library.
/// Optimised for rhythm-game audio-visual synchronization with:
///  - AAudio preferred (lowest latency path on Android 8.1+)
///  - Exclusive sharing mode (bypass system mixer)
///  - Buffer size tuned to 1× burst for minimum latency
///  - Automatic stream recovery on disconnect / route change
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

    /// Returns the negotiated sample rate of the open stream (e.g. 48000).
    int32_t getSampleRate() const;

    /// Returns the optimal burst size in frames (one callback quantum).
    int32_t getFramesPerBurst() const;

    /// Returns the current buffer size in frames (ideally == framesPerBurst for lowest latency).
    int32_t getBufferSizeInFrames() const;

    /// Returns true if the stream is using AAudio (vs OpenSL ES fallback).
    bool isAAudio() const;

    /// Returns true if the stream is using the hardware MMAP path (lowest possible latency).
    /// MMAP provides direct memory-mapped access to audio hardware buffers.
    bool isMMap() const;

    // oboe::AudioStreamCallback
    oboe::DataCallbackResult onAudioReady(
        oboe::AudioStream* stream, void* audioData, int32_t numFrames) override;

    void onErrorBeforeClose(oboe::AudioStream* stream, oboe::Result error) override;
    void onErrorAfterClose(oboe::AudioStream* stream, oboe::Result error) override;

private:
    std::shared_ptr<oboe::AudioStream> stream_;
    std::mutex streamLock_;
    std::atomic<bool> active_{false};
    std::atomic<double> latencyMs_{-1.0};

    void updateLatency();
    void optimiseBufferSize();
    bool reopenAndRestart();
};
