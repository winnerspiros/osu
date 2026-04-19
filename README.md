<p align="center">
  <img width="500" alt="osu! logo" src="assets/lazer.png">
</p>

# osu! (Android-optimised fork)

[![Build Android APK](https://github.com/winnerspiros/osu/actions/workflows/release.yml/badge.svg)](https://github.com/winnerspiros/osu/actions/workflows/release.yml)
[![CI](https://github.com/winnerspiros/osu/actions/workflows/ci.yml/badge.svg)](https://github.com/winnerspiros/osu/actions/workflows/ci.yml)
[![GitHub release](https://img.shields.io/github/release/winnerspiros/osu.svg)](https://github.com/winnerspiros/osu/releases/latest)

A fork of [ppy/osu](https://github.com/ppy/osu) (osu! lazer) with deep Android platform integration — low-latency audio, Samsung optimisations, Vulkan GPU probing, and production-ready APK builds.

> **📱 Download the latest APK:** Go to [Releases](https://github.com/winnerspiros/osu/releases/latest) and download `osu-lazer-*.apk`.
> Requires **Android 13+** (arm64).

---

## What's different from upstream ppy/osu?

This fork adds **~5,000 lines of custom code** (managed C# + native C++) to turn osu! lazer into a performance-tuned Android rhythm game. The upstream ppy/osu has minimal Android support — this fork fills in everything needed for a production-quality mobile experience.

### 🔊 Low-latency audio (Google Oboe)

The single most important change for a rhythm game. Upstream uses the default Android audio path, which adds 50–200 ms of latency — unacceptable for gameplay.

| Feature | Upstream | This fork |
|---------|----------|-----------|
| Audio backend | Default Android (high latency) | [Google Oboe](https://github.com/google/oboe) via native C++ bridge |
| Audio API | OpenSL ES | AAudio with MMAP (shared memory) when available, OpenSL ES fallback |
| Measured latency | Not measured | Real-time measurement via `stream->calculateLatencyMillis()` |
| Audio offset | Manual user guess | Auto-suggested from measured hardware latency |
| Buffer tuning | Fixed | Dynamic via Oboe `LatencyTuner` (shrinks to 1× burst when stable) |
| Callback stability | N/A | `StabilizedCallback` wrapper smooths execution jitter |
| ADPF hints | No | `setPerformanceHintEnabled(true)` tells Android to prioritise the audio thread |

The audio bridge (`osu.Android/Native/oboe_bridge.cpp`) runs as an unmanaged C++ callback at real-time priority. BASS audio mixers are redirected into this callback via `OboeAudioRedirector`, which discovers mixer handles through reflection since `BassAudioMixer` is internal to the framework.

**Toggle:** Settings → Graphics → Android Performance → *Low-latency audio (Oboe)*

### 🎮 Custom input handlers

Upstream relies on the framework's default touch handling. This fork adds three dedicated input handlers with direct event dispatch and unbuffered input:

#### Samsung S Pen / stylus (`AndroidStylusHandler`)
- Full **tablet area mapping** — the S Pen digitiser maps to the game area like a Wacom tablet
- Pressure-sensitive clicking with configurable threshold
- S Pen button → right-click, eraser → middle-click
- Dynamic area expansion if the digitiser reports out-of-bounds coordinates
- Rotation support for different device orientations

#### Physical keyboard (`AndroidKeyboardHandler`)
- Complete Android keycode → osuTK key mapping (A–Z, 0–9, F1–F12, special keys)
- Uses `FrozenDictionary` for O(1) lookup in the hot path
- Filters system keys (Back, Home, Volume) to avoid interfering with Android

#### Mouse / trackpad (`AndroidMouseHandler`)
- Full mouse support (position, scroll wheel, 5 buttons) for Samsung DeX and USB mice
- Processes historical motion events for accurate input timing
- System pointer icon hidden to prevent double cursors in DeX mode

### ⚡ Performance tuning

| Optimisation | What it does |
|---|---|
| **CPU affinity pinning** | Pins update, render, input, and audio threads to high-performance (big) CPU cores. Uses sysfs topology detection (`/sys/devices/system/cpu/cpuN/cpufreq/cpuinfo_max_freq`) to correctly identify Prime + Gold cores across Snapdragon, Exynos, Dimensity, and Tensor SoCs. |
| **Thread priority** | Sets game threads to `UrgentDisplay` priority (-8) for minimum scheduling latency. |
| **GC tuning** | Switches to `SustainedLowLatency` GC mode during gameplay to avoid collection pauses. |
| **Sustained performance mode** | Always-on `Window.SetSustainedPerformanceMode(true)` prevents thermal throttling from causing sudden FPS drops. |
| **ADPF integration** | Native ADPF session creation and work duration reporting for Android's Dynamic Performance Framework. |
| **Display refresh rate** | Queries all supported display modes, auto-selects the highest refresh rate, and sets `Surface.SetFrameRate()` hints for the compositor. Supports 120 Hz+ panels. |

**Toggle:** Settings → Graphics → Android Performance → *Performance mode*

### 🖥️ Samsung DeX support

When connected to an external monitor via DeX:

- Auto-detects DeX mode (`UiMode.TypeDesk`)
- Auto-enables performance mode and immersive fullscreen
- Queries external display modes and selects the highest refresh rate
- Starts a permanent high-performance GC session
- Mouse/keyboard input works seamlessly (including mouse back button → Escape)

### 🎨 Vulkan GPU probing

A native C++ Vulkan probe (`vulkan_bridge.cpp`) checks the GPU's capabilities at startup:

- Vulkan API version and driver info
- Device-local VRAM
- Modern extensions: dynamic rendering, synchronisation2, graphics pipeline library, shader objects, present ID/wait
- GPU-specific workaround detection (disables problematic features on known-bad drivers)
- Result exposed as `IsVulkanRecommended` — the Vulkan renderer option only appears in settings if the GPU actually supports it

**Toggle:** Settings → Graphics → Android Performance → *GPU detection (Vulkan)*

### 📦 Build system

| Change | Detail |
|---|---|
| **.NET 10** | Upgraded from .NET 8 (upstream) to .NET 10 for latest runtime improvements |
| **Framework submodule** | Uses [winnerspiros/osu-framework](https://github.com/winnerspiros/osu-framework) as a git submodule instead of the NuGet package, enabling mobile platform modifications |
| **Profiled AOT** | `AndroidEnableProfiledAot=true` for faster startup (startup-critical methods are ahead-of-time compiled) |
| **IL trimming** | Partial trimming enabled for smaller APK size |
| **LZ4 compression** | Assembly compression reduces APK size by ~20 MB |
| **Native C++ library** | `libosu_native.so` built with NDK r29, C++20, `-O3`, LTO, and 16 KB page alignment (`-Wl,-z,max-page-size=16384`) for Android 15+ compatibility |
| **ELF page alignment** | Custom MSBuild task (`PatchElfPageSize.targets`) rewrites 4 KB-aligned .so files to 16 KB for Android 16 (API 36+) compliance |
| **arm64 only** | Single ABI target reduces APK size and build complexity |

### 📱 Android integration

| Feature | Detail |
|---|---|
| **File associations** | Opens `.osz` (beatmaps), `.osk` (skins), `.osr` (replays) and `osu://` / `osump://` URLs |
| **Samsung Game Launcher** | Registered via `com.samsung.android.game.biz` metadata for Samsung Game Booster optimisations |
| **Samsung MultiDisplay** | `keep_process_alive` flag prevents process termination on display transitions |
| **Orientation management** | Locks to landscape during gameplay, allows portrait in menus (phone only — tablets stay landscape) |
| **Tablet detection** | Devices with smallest screen width ≥ 600 dp are treated as tablets |
| **Update notifications** | Checks GitHub Releases for newer versions and notifies the user |
| **Notch/cutout support** | `LayoutInDisplayCutoutMode.ShortEdges` uses the full display area |
| **Min SDK 33** | Targets Android 13+ (API 33) for modern API access; target SDK 36 |

### 🔧 CI/CD

| Workflow | What it does |
|---|---|
| `release.yml` | **One-click APK builder.** Compiles native C++, builds the .NET project, signs the APK, creates a GitHub Release. Auto-generates a signing keystore if no secrets are configured. |
| `generate-keystore.yml` | Helper to generate a persistent signing keystore for consistent APK signatures across builds. |
| `ci.yml` | Full CI with desktop tests + Android/iOS compile-only verification. |

---

## Download

Grab the latest signed APK from the [Releases page](https://github.com/winnerspiros/osu/releases/latest).

**Requirements:**
- Android 13 or later (API 33+)
- arm64 device (virtually all modern Android phones and tablets)

Install by opening the APK on your device. You may need to enable "Install from unknown sources" in your device settings.

---

## Building from source

### Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- JDK 17 (`sudo apt install openjdk-17-jdk` or [Microsoft's JDK](https://learn.microsoft.com/en-us/java/openjdk/download))
- Android workload: `dotnet workload install android`
- Android NDK r29 + CMake (for native library — only needed for release builds)

### Clone

```shell
git clone --recurse-submodules https://github.com/winnerspiros/osu
cd osu
```

### Debug build (quick iteration)

```shell
dotnet build -c Debug osu.Android/osu.Android.csproj
adb install osu.Android/bin/Debug/net10.0-android/sh.ppy.osulazer.apk
```

Debug builds skip AOT/trimming and use the Android debug keystore — fast to build, fine for testing.

### Release build (optimised)

The easiest way is the GitHub Actions workflow — just click **Actions → Build Android APK → Run workflow**. It handles NDK setup, native compilation, signing, and creates a Release automatically.

To build locally:

```shell
# 1. Build native library (requires NDK r29)
NDK_HOME="$ANDROID_HOME/ndk/29.0.14206865"
CMAKE_BIN="$ANDROID_HOME/cmake/3.22.1/bin/cmake"

"$CMAKE_BIN" -B build-native/arm64-v8a -S osu.Android/Native \
  -DCMAKE_TOOLCHAIN_FILE="$NDK_HOME/build/cmake/android.toolchain.cmake" \
  -DANDROID_ABI=arm64-v8a -DANDROID_PLATFORM=android-33 -DCMAKE_BUILD_TYPE=Release

"$CMAKE_BIN" --build build-native/arm64-v8a --config Release -j $(nproc)
mkdir -p osu.Android/libs/arm64-v8a
cp build-native/arm64-v8a/libosu_native.so osu.Android/libs/arm64-v8a/

# 2. Build and publish the APK
dotnet publish -c Release osu.Android/osu.Android.csproj -f net10.0-android
```

### Desktop build

```shell
dotnet run --project osu.Desktop
```

Load `osu.Desktop.slnf` in your IDE for desktop development, or `osu.Android.slnf` for Android.

---

## Project structure (fork-specific files)

```
osu.Android/
├── Native/
│   ├── oboe_bridge.cpp/h      # C++ Oboe audio bridge (AAudio/OpenSL ES)
│   ├── vulkan_bridge.cpp/h     # C++ Vulkan GPU capability probe
│   ├── OboeAudioBridge.cs      # P/Invoke wrapper for Oboe
│   ├── VulkanProbe.cs          # P/Invoke wrapper for Vulkan
│   └── CMakeLists.txt          # NDK build config (C++20, Oboe, Vulkan)
├── Input/
│   ├── AndroidStylusHandler.cs # S Pen / stylus tablet-area input
│   ├── AndroidKeyboardHandler.cs
│   └── AndroidMouseHandler.cs
├── Performance/
│   └── AndroidHighPerformanceSessionManager.cs
├── OboeAudioRedirector.cs      # BASS → Oboe audio routing
├── AndroidNativeBridgeManager.cs
├── OsuGameAndroid.cs           # Main game class (Android lifecycle, perf, DeX)
├── OsuGameActivity.cs          # Activity (intents, surface, input dispatch)
└── AndroidManifest.xml         # Samsung tags, file associations, API levels

build/
├── PatchElfPageSize.targets    # ELF 4KB→16KB alignment for Android 16+
└── SuppressSubmoduleWarnings.targets

.github/workflows/
├── release.yml                 # One-click APK builder + GitHub Release
├── generate-keystore.yml       # Signing keystore generator
└── ci.yml                      # CI with Android/iOS compile jobs

osu.Game/
├── Configuration/OsuConfigManager.cs   # +3 Android settings
├── OsuGameBase.cs                      # +virtual props (Vulkan, Oboe, refresh rate)
├── Overlays/Settings/.../AndroidPerformanceSettings.cs  # Android settings UI
├── Overlays/Settings/.../RendererSettings.cs            # +Vulkan dropdown on Android
├── Utils/MobileUtils.cs                # Orientation management
└── Updater/MobileUpdateNotifier.cs     # GitHub Release update checker
```

---

## Developing osu!

### Code analysis

Before committing your code, please run a code formatter. This can be achieved by running `dotnet format` in the command line, or using the `Format code` command in your IDE.

We have adopted some cross-platform, compiler integrated analyzers. They can provide warnings when you are editing, building inside IDE or from command line, as-if they are provided by the compiler itself.

JetBrains ReSharper InspectCode is also used for wider rule sets. You can run it from PowerShell with `.\InspectCode.ps1`. Alternatively, you can install ReSharper or use Rider to get inline support in your IDE of choice.

## Contributing

Contributions are welcome! Please refer to the [contributing guidelines](CONTRIBUTING.md) to understand how to help in the most effective way possible.

If you wish to help with localisation efforts, head over to [crowdin](https://crowdin.com/project/osu-web).

## Licence

*osu!*'s code and framework are licensed under the [MIT licence](https://opensource.org/licenses/MIT). Please see [the licence file](LICENCE) for more information. [tl;dr](https://tldrlegal.com/license/mit-license) you can do whatever you want as long as you include the original copyright and license notice in any copy of the software/source.

Please note that this *does not cover* the usage of the "osu!" or "ppy" branding in any software, resources, advertising or promotion, as this is protected by trademark law.

Please also note that game resources are covered by a separate licence. Please see the [ppy/osu-resources](https://github.com/ppy/osu-resources) repository for clarifications.

## Credits

This fork is based on [ppy/osu](https://github.com/ppy/osu) by Dean Herbert (peppy) and contributors. All upstream code is under the MIT licence.
