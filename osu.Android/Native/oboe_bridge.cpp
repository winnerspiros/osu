<<<<<<< SEARCH
    if (burst > 0) {
        auto setResult = stream_->setBufferSizeInFrames(burst);

        if (setResult) {
            LOGI("Buffer size tuned to %d frames (1x burst)", setResult.value());
        }
    }
=======
    if (burst > 0) {
        // Set buffer size to 2× burst for improved stability on Samsung and other devices.
        // 1x burst is often too aggressive for managed code callbacks, causing underruns.
        // 2x provides a safe jitter margin while still maintaining extremely low latency.
        auto setResult = stream_->setBufferSizeInFrames(burst * 2);

        if (setResult) {
            LOGI("Buffer size tuned to %d frames (2x burst)", setResult.value());
        }
    }
>>>>>>> REPLACE
