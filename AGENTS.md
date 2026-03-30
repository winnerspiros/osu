# Oboe Audio Bridge Optimizations

## CPU Affinity Pinning
As of Oboe `main` branch (post-1.9.0), internal headers like `common/Process.h` have been removed.
Do not attempt to include internal Oboe headers for CPU affinity.
Instead, use standard Linux `sched_setaffinity` in `oboe_bridge.cpp` to pin the audio callback thread to high-performance cores (typically the higher-indexed half of available cores in Android big.LITTLE architectures).

## ADPF Integration
The bridge uses `stream_->reportActualWorkDuration()` within the audio callback. This is critical for the Android Dynamic Performance Framework (ADPF) to adjust CPU frequencies accurately for low-latency audio without underruns.

## Build Configuration
`OBOE_ENABLE_FLOWGRAPH` is set to `OFF` in `CMakeLists.txt` to minimize binary size, as we perform all mixing in BASS and only use Oboe for final hardware delivery.
