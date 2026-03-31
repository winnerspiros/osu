# Oboe Audio Bridge Optimizations

## CPU Affinity Pinning
As of Oboe `main` branch (post-1.9.0), internal headers like `common/Process.h` have been removed.
Do not attempt to include internal Oboe headers for CPU affinity.
Instead, use standard Linux `sched_setaffinity` in `oboe_bridge.cpp` to pin the audio callback thread to high-performance cores (typically the higher-indexed half of available cores in Android big.LITTLE architectures).

## ADPF Integration
Oboe handles ADPF (Android Dynamic Performance Framework) automatically when `setPerformanceHintEnabled(true)` is called during stream initialization.
Manual work duration reporting (`reportActualWorkDuration`) has been removed from the public Oboe API and should not be implemented in the bridge to avoid build errors and redundant reporting.

## Build Configuration
`OBOE_ENABLE_FLOWGRAPH` is set to `OFF` in `CMakeLists.txt` to minimize binary size, as we perform all mixing in BASS and only use Oboe for final hardware delivery.

## API 33 Upgrade
The project was upgraded to Android API 33 to support APerformanceHint (ADPF) APIs.
Native builds now target android-33 to avoid compilation errors for these high-performance features.
The 'byte' type is defined as 'uint8_t' in native bridges to ensure C# P/Invoke compatibility.
