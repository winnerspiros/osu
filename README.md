<p align="center">
  <img width="500" alt="osu! logo" src="assets/lazer.png">
</p>

<h1 align="center">osu! lazer — Android Edition</h1>

<p align="center">
  <a href="https://github.com/winnerspiros/osu/actions/workflows/release.yml"><img src="https://github.com/winnerspiros/osu/actions/workflows/release.yml/badge.svg" alt="Build Android APK"></a>
  <a href="https://github.com/winnerspiros/osu/actions/workflows/ci.yml"><img src="https://github.com/winnerspiros/osu/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <a href="https://github.com/winnerspiros/osu/releases/latest"><img src="https://img.shields.io/github/release/winnerspiros/osu.svg" alt="GitHub release"></a>
</p>

<p align="center">
  A community fork of <a href="https://github.com/ppy/osu">ppy/osu</a> (osu! lazer) rebuilt for the best possible Android experience.<br>
  Lower audio latency · Samsung optimisations · Vulkan rendering · 120 Hz+ support · S Pen as a tablet
</p>

---

## 📱 Download

> **[⬇️ Download the latest APK](https://github.com/winnerspiros/osu/releases/latest)**

| | |
|---|---|
| **Requires** | Android 13 or newer (arm64) |
| **Works on** | Phones, tablets, Samsung DeX, foldables |
| **Install** | Open the `.apk` file on your device. Enable "Install from unknown sources" if prompted. |

The app checks for updates automatically and notifies you when a new version is available.

---

## ✨ What makes this different?

The official osu! lazer has basic Android support. This fork goes much further — adding low-latency audio, proper input handling, GPU optimisations, and Samsung-specific features to make it feel like a native Android rhythm game.

Here's what you get:

---

### 🔊 Low-latency audio

**The #1 improvement for a rhythm game.** Stock Android audio adds 50–200 ms of delay — that's the difference between a perfect hit and a miss.

This fork replaces the default audio path with [Google Oboe](https://github.com/google/oboe), the same low-latency audio library used by professional music apps:

- **AAudio with shared memory (MMAP)** — the fastest audio path Android offers, with OpenSL ES as a fallback for older devices
- **Automatic latency measurement** — the game measures your device's actual audio delay and suggests the right offset, so you don't have to guess
- **Dynamic buffer tuning** — audio buffers automatically shrink to the smallest stable size for your device
- **Reduced framework buffers** — BASS audio engine buffers cut from 100 ms → 25 ms, update period from 5 ms → 2 ms
- **Native 48 kHz sample rate** — matches what modern Android hardware actually uses, avoiding unnecessary resampling

> **💡 Enable it:** Settings → Graphics → Android Performance → *Low-latency audio (Oboe)*

---

### 🎮 Input — S Pen, keyboard & mouse

Stock osu! lazer uses basic touch input on Android. This fork adds three dedicated input handlers:

#### ✏️ Samsung S Pen / stylus
Your S Pen becomes a **real tablet** — just like a Wacom:
- Full tablet area mapping (the digitiser maps directly to the game area)
- Pressure-sensitive clicking with adjustable threshold
- S Pen button = right-click, eraser end = middle-click
- Automatic calibration to your screen size and rotation

#### ⌨️ Physical keyboard
Plug in a keyboard (USB or Bluetooth) and it just works:
- Full key mapping — letters, numbers, F-keys, arrow keys, all the usual suspects
- System keys (Home, Back, Volume) are filtered so they don't interfere with gameplay

#### 🖱️ Mouse & trackpad
Perfect for Samsung DeX or a USB mouse:
- Full mouse support — position tracking, scroll wheel, all 5 buttons
- No double cursor — the system pointer hides automatically
- Mouse back button = Escape (for quick menu navigation)

---

### ⚡ Performance mode

Turn it on and the game squeezes every bit of performance from your hardware:

- **Smart CPU pinning** — game threads run on the fastest CPU cores. The game reads your chip's topology (works on Snapdragon, Exynos, Dimensity, Tensor, and others) and pins to the right cores automatically
- **High thread priority** — game threads run at urgent-display priority so the OS doesn't deprioritise them
- **Low-latency garbage collection** — the .NET runtime switches to a mode that avoids pauses during gameplay
- **Sustained performance** — prevents the phone from thermal throttling during long sessions
- **ADPF hints** — tells Android's performance framework to boost the audio thread

> **💡 Enable it:** Settings → Graphics → Android Performance → *Performance mode*

---

### 🖥️ 120 Hz+ display support

The game queries your screen's supported modes and picks the highest refresh rate automatically:

- Supports 60 / 90 / 120 / 144 / 165 Hz panels (and beyond)
- Tells the Android compositor your target frame rate for optimal scheduling
- On Samsung DeX, it finds and uses the external display's best mode

---

### 🖥️ Samsung DeX

Plug your phone into a monitor and play on the big screen:

- **Auto-detected** — the game knows when you're in DeX mode
- Performance mode and immersive fullscreen turn on automatically
- External display refresh rate is detected and applied
- Keyboard + mouse input works seamlessly (no extra setup)
- The game stays alive during display transitions (no restart)

---

### 🎨 Vulkan GPU detection

A built-in GPU probe checks whether your device can handle Vulkan rendering:

- Tests for Vulkan 1.3+ with all the extensions osu! needs (dynamic rendering, synchronisation2, graphics pipeline library, shader objects)
- Detects GPU-specific driver bugs and automatically disables problematic features
- The Vulkan renderer option only shows up in settings if your GPU truly supports it — no guessing

When Vulkan is available, it's used as the **primary renderer** (with OpenGL ES as fallback).

> **💡 Enable it:** Settings → Graphics → Android Performance → *GPU detection (Vulkan)*

---

### 📱 Android quality-of-life

| Feature | What it does |
|---|---|
| **Open beatmaps, skins & replays** | Tap a `.osz`, `.osk`, or `.osr` file anywhere on your phone and it opens directly in osu! |
| **Deep links** | `osu://` and `osump://` URLs open in the app |
| **Smart orientation** | Phones: portrait in menus, landscape during gameplay. Tablets: stays landscape |
| **Full-screen with notch** | Uses the entire display, including around camera cutouts |
| **Samsung Game Booster** | Registered with Samsung's Game Launcher for extra performance optimisations and thermal management |
| **Update notifications** | Checks GitHub for new releases and lets you know (never forces an update) |
| **Tablet detection** | Devices with screens ≥ 600 dp are treated as tablets, with adapted UI behaviour |
| **Battery info** | Battery level and charging status shown natively |

---

### 🛡️ Stability improvements

This fork includes several crash fixes on top of upstream:

- **Sentry crash fix** — the app no longer crashes on startup when the error reporting service can't initialise (e.g. with a placeholder DSN)
- **Graceful native library loading** — if the Oboe or Vulkan native libraries are missing, the app continues without them instead of crashing
- **JNI surface safety** — proper lifecycle management with atomic swaps and timeouts to prevent race conditions between Android surface creation and destruction
- **Trimmer-safe builds** — critical reflection-heavy assemblies are protected from .NET IL trimming to prevent `TypeLoadException` crashes in release builds

---

## 🔧 Under the hood

<details>
<summary><strong>Build system & toolchain</strong></summary>

| | |
|---|---|
| **.NET 10** | Upgraded from .NET 8 (upstream) to .NET 10 for the latest runtime and language improvements |
| **Framework as submodule** | Uses a [custom osu-framework fork](https://github.com/winnerspiros/osu-framework) as a git submodule instead of the NuGet package — enables deep platform changes |
| **Profiled AOT** | Startup-critical methods are ahead-of-time compiled for faster app launch |
| **IL trimming** | Unused code is stripped from the APK for smaller size |
| **LZ4 compression** | Assembly compression saves ~20 MB in the final APK |
| **Native C++ library** | `libosu_native.so` — Oboe audio + Vulkan probe, built with NDK r29, C++20, `-O3`, LTO |
| **16 KB page alignment** | All native libraries use 16 KB ELF alignment for Android 15+ and 16 (API 36) compliance |
| **arm64 only** | Single ABI keeps the APK small and builds fast |

</details>

<details>
<summary><strong>osu-framework fork changes</strong></summary>

The [winnerspiros/osu-framework](https://github.com/winnerspiros/osu-framework) fork includes:

**Audio engine tuning:**
- BASS device buffer: 10 ms → 5 ms
- Playback buffer: 100 ms → 25 ms (Android) / 30 ms (iOS)
- Update period: 5 ms → 2 ms (Android) / 3 ms (iOS)
- AAudio backend enabled for BASS
- Native 48 kHz sample rate (matches Android/iOS hardware)
- Mixer handle made public for Oboe bridge access

**Rendering:**
- Android renderer order changed to Vulkan (primary) → OpenGL (fallback)
- Vulkan 1.3 requirement check with diagnostic logging

**Platform layers:**
- Full `osu.Framework.Android` project (activity lifecycle, storage, file picker)
- Full `osu.Framework.iOS` project (Metal window, AOT, native frameworks)

**Performance:**
- LINQ eliminated from hot paths (Dropdown, FlowContainer, shader pipelines)
- Modern `System.Threading.Lock` type replaces `lock(object)` in renderers
- Reduced redundant OpenGL state changes
- Faster texture uploads on mobile

**Dependencies updated:** SDL3-CS, ImageSharp, Newtonsoft.Json, JetBrains.Annotations, StbiSharp, AndroidX.Window

</details>

<details>
<summary><strong>Veldrid fork changes</strong></summary>

The [winnerspiros/veldrid](https://github.com/winnerspiros/veldrid) fork adds:

**Android Vulkan rendering:**
- Vulkan surface creation from `ANativeWindow` via `VK_KHR_android_surface`
- Android-specific extension detection and enablement
- Native window P/Invoke bindings

**OpenGL ES fallback:**
- Complete EGL 1.4 bindings for GLES 2.0/3.0 context creation
- Proper stencil buffer initialisation (critical for osu!'s UI)

**Performance:**
- `System.Threading.Lock` migration across all GPU backends
- UTF-8 string literals for zero-allocation Vulkan lookups
- Vulkan fence early-out to avoid blocking waits
- Screen tearing support for lowest-latency present modes

</details>

<details>
<summary><strong>CI/CD pipelines</strong></summary>

| Workflow | What it does |
|---|---|
| **`release.yml`** | One-click APK builder. Compiles native C++ with NDK, builds .NET, signs the APK, creates a GitHub Release. Auto-generates a signing keystore if none is configured. |
| **`generate-keystore.yml`** | Generates a persistent signing keystore so APK updates install cleanly over previous versions. |
| **`ci.yml`** | Runs code quality checks (ReSharper InspectCode), desktop tests, and Android/iOS compile verification. |

</details>

---

## 🏗️ Building from source

### Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- JDK 17 (`sudo apt install openjdk-17-jdk` or [Microsoft's JDK](https://learn.microsoft.com/en-us/java/openjdk/download))
- Android workload: `dotnet workload install android`
- Android NDK r29 + CMake (only needed for release builds with native audio/Vulkan)

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

Debug builds skip AOT and trimming — fast to build, good for testing.

### Release build (optimised)

The easiest way is the GitHub Actions workflow: **Actions → Build Android APK → Run workflow**. It handles everything — NDK, native compilation, signing, and release creation.

To build locally:

```shell
# 1. Build the native library (requires NDK r29)
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

Use `osu.Desktop.slnf` in your IDE for desktop development, or `osu.Android.slnf` for Android work.

---

## 🤝 Contributing

Contributions are welcome! Please refer to the [contributing guidelines](CONTRIBUTING.md).

Before committing, run `dotnet format` to ensure consistent code style. The CI pipeline also runs [ReSharper InspectCode](https://www.jetbrains.com/help/resharper/InspectCode.html) for additional checks — you can run it locally with `.\InspectCode.ps1`.

For localisation help, head to [crowdin](https://crowdin.com/project/osu-web).

---

## 📄 Licence

*osu!*'s code and framework are licensed under the [MIT licence](https://opensource.org/licenses/MIT). See the [LICENCE](LICENCE) file for details. In short — you can do whatever you want as long as you include the original copyright notice.

This does **not** cover the "osu!" or "ppy" branding (protected by trademark law), or game resources (see [ppy/osu-resources](https://github.com/ppy/osu-resources)).

---

<p align="center">
  Based on <a href="https://github.com/ppy/osu">ppy/osu</a> by Dean Herbert (peppy) and contributors.<br>
  All upstream code is under the MIT licence.
</p>
