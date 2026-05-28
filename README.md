<p align="center">
  <img width="500" alt="osu! logo" src="assets/lazer.png">
</p>

<h1 align="center">osu! lazer</h1>

<p align="center">
  <a href="https://github.com/winnerspiros/osu/actions/workflows/release.yml"><img src="https://github.com/winnerspiros/osu/actions/workflows/release.yml/badge.svg" alt="Release Build"></a>
  <a href="https://github.com/winnerspiros/osu/actions/workflows/ci.yml"><img src="https://github.com/winnerspiros/osu/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <a href="https://github.com/winnerspiros/osu/releases/latest"><img src="https://img.shields.io/github/release/winnerspiros/osu.svg" alt="GitHub release"></a>
</p>

<p align="center">
  A community fork of <a href="https://github.com/ppy/osu">ppy/osu</a> (osu! lazer) optimised for all platforms.<br>
  Low-latency audio · Vulkan/D3D12/Metal rendering · Performance tuning · Per-platform optimisations
</p>

---

## Download

> **[⬇️ Latest release](https://github.com/winnerspiros/osu/releases/latest)**

| Platform | Requirements | Format |
|---|---|---|
| **Windows** | Windows 10+ (x64) | Self-contained `.zip` |
| **Linux** | x64, glibc 2.17+ | Self-contained `.tar.gz` |
| **macOS** | macOS 12+ (Apple Silicon & Intel) | Self-contained `.tar.gz` |
| **Android** | Android 13+ (arm64) | `.apk` |
| **iOS** | iOS 13.4+ | Unsigned `.app` (sideload) |

Desktop builds are self-contained — no .NET runtime install required. Android APK can be installed directly; enable "Install from unknown sources" if prompted. iOS requires sideloading (AltStore, Sideloadly, or Xcode).

---

## What's in this fork

This fork diverges from upstream `ppy/osu` with deep platform-specific optimisations across all supported operating systems.

---

### Low-latency audio

Audio latency is critical for a rhythm game. Each platform uses a different strategy to minimise the audio pipeline delay:

| Platform | Backend | Strategy |
|---|---|---|
| **Android** | [Google Oboe](https://github.com/google/oboe) → AAudio (MMAP) | Shared-memory path, bypasses Android AudioFlinger. Falls back to OpenSL ES. |
| **Windows** | WASAPI (via BASS) | Exclusive-mode capable. BASS device buffer reduced to 5 ms, playback buffer 30 ms. |
| **macOS** | Core Audio (via BASS) | Low-latency HAL output. BASS device buffer 5 ms, playback buffer 30 ms. |
| **iOS** | Core Audio (via BASS) | `AVAudioSession` category `.playback` with `.mixWithOthers`, buffer duration request of ~5 ms. BASS playback buffer 25 ms, update period 3 ms. |
| **Linux** | PipeWire / PulseAudio / ALSA (via BASS) | BASS device buffer 5 ms, playback buffer 30 ms. PipeWire provides the best latency when available. |

Additional audio tuning (all platforms):
- BASS update period reduced to 2–3 ms
- Native 48 kHz sample rate (avoids resampling on modern hardware)
- Automatic latency measurement suggests the right universal offset on Android
- Dynamic buffer tuning shrinks to the smallest stable size on Android

> **Android:** Settings → Graphics → Android Performance → *Low-latency audio (Oboe)*

---

### Rendering

| Platform | Primary renderer | Fallback |
|---|---|---|
| **Windows** | Vulkan / Direct3D 12 / Direct3D 11 | OpenGL |
| **Linux** | Vulkan | OpenGL |
| **macOS** | Metal | OpenGL |
| **Android** | Vulkan 1.3 (with full capability probe) | OpenGL ES |
| **iOS** | Metal | OpenGL ES |

Fork-added renderer settings (Settings → Graphics → Renderer):

| Option | What it does |
|---|---|
| **Renderer** | Picks the GPU backend. Windows: Metal / Vulkan / D3D11 / D3D12 / OpenGL + deferred variants. |
| **Frame limiter** | VSync, VSync Unbuffered (for VRR/G-Sync/FreeSync), 2×/4×/8× refresh, Unlimited, Custom. |
| **Custom draw rate** | 0–1000 Hz slider. Visible when "Custom" is selected. 0 = unlimited. |
| **Low latency** | Off / On / Boost. Drives `ILowLatencyProvider` (NVIDIA Reflex / LatencyFlex ready on D3D11/D3D12). Boost adds per-frame sleep for lower input-to-photon. |

---

### Performance tuning

**Desktop (Windows / Linux / macOS):**
- Server GC with concurrent collection — reduces STW pause time (largest source of frame spikes)
- Low-latency GC mode during gameplay (switches back afterward)
- Raw keyboard input on Windows (`SDL_HINT_WINDOWS_RAW_KEYBOARD`)
- Async keyboard event handling — bypasses SDL event queue when IME is inactive
- Multi-threaded execution enforced across all platforms

**Android:**
- Smart CPU pinning to fastest cores (Snapdragon, Exynos, Dimensity, Tensor)
- High thread priority (urgent-display) for game and audio threads
- Low-latency GC during gameplay
- ADPF performance hints for the audio thread
- Sustained performance mode to prevent thermal throttling
- 120 Hz+ display support with automatic refresh rate selection

**iOS:**
- AOT compilation with interpreter fallback for dynamic code
- Metal rendering (primary)
- Server GC with concurrent collection

---

### Platform-specific features

<details>
<summary><strong>Android</strong></summary>

- **S Pen / stylus:** Full tablet-area mapping, pressure-sensitive clicking, button mapping
- **Physical keyboard:** Full key mapping (USB / Bluetooth), system key filtering
- **Mouse & trackpad:** 5-button support, auto-hide system cursor, back button = Escape
- **Samsung DeX:** Auto-detected, performance mode auto-enabled, highest refresh rate requested
- **120 Hz+:** Queries supported display modes, sets `Surface.SetFrameRate` with seamless flag
- **Vulkan GPU detection:** Probes for Vulkan 1.3 + required extensions, auto-disables problematic features per GPU
- **ANGLE support:** Can force OpenGL-ES-on-Vulkan via Developer Options for devices with buggy GL drivers
- **File associations:** `.osz`, `.osk`, `.osr` files open directly; `osu://` and `osmp://` deep links
- **Smart orientation:** Portrait in menus (phones), landscape during gameplay
- **Full-screen with notch:** Uses entire display area
- **Battery info:** Native battery level and charging status
- **Update notifications:** Checks GitHub releases automatically

</details>

<details>
<summary><strong>Windows</strong></summary>

- **Direct3D 12 backend** via [winnerspiros/veldrid](https://github.com/winnerspiros/veldrid) fork
- **NVIDIA Reflex / LatencyFlex** infrastructure (D3D11 & D3D12)
- **Raw keyboard input** via SDL hint (bypasses Windows message translation)
- **VSync Unbuffered** mode for G-Sync / FreeSync displays
- **Game Booster integration** via WinKey blocking during gameplay
- **Self-contained single-file** deployment with trimming and compression

</details>

<details>
<summary><strong>macOS</strong></summary>

- **Metal rendering** as primary backend
- **Universal binary support** (arm64 + x64 builds available)
- **Core Audio low-latency** HAL output via BASS
- **Self-contained** deployment — no .NET runtime needed

</details>

<details>
<summary><strong>Linux</strong></summary>

- **Vulkan rendering** as primary backend
- **PipeWire-aware** audio (lowest latency when PipeWire is the active server)
- **Self-contained single-file** deployment
- **No external dependencies** beyond glibc

</details>

<details>
<summary><strong>iOS</strong></summary>

- **Metal rendering** as primary backend
- **AOT compilation** for fast startup and smooth gameplay
- **Core Audio** with minimal buffer configuration
- **Supports iOS 13.4+** on iPhone and iPad

</details>

---

## Build optimisations by platform

All release builds include:

| Optimisation | Windows | Linux | macOS | Android | iOS |
|---|---|---|---|---|---|
| Self-contained | ✓ | ✓ | ✓ | N/A | N/A |
| Single-file publish | ✓ | ✓ | ✓ | N/A | N/A |
| IL trimming (partial) | ✓ | ✓ | ✓ | SDK-only | SDK-only |
| Compression | ✓ | ✓ | ✓ | LZ4 | N/A |
| Server GC | ✓ | ✓ | ✓ | ✓ | ✓ |
| Concurrent GC | ✓ | ✓ | ✓ | ✓ | ✓ |
| Debug symbols stripped | ✓ | ✓ | ✓ | ✓ | ✓ |
| Profiled AOT | — | — | — | ✓ | ✓ |
| Native lib (Oboe + Vulkan probe) | — | — | — | ✓ | — |

---

## Under the hood

<details>
<summary><strong>Build system & toolchain</strong></summary>

| | |
|---|---|
| **.NET 10** | Upgraded from .NET 8 (upstream) for latest runtime/language improvements |
| **Framework as NuGet (fork)** | Consumes [`winnerspiros/osu-framework`](https://github.com/winnerspiros/osu-framework) from GitHub Packages — deep platform changes without submodules |
| **arm64 only (Android)** | Single ABI keeps APK small |
| **Universal (macOS)** | Separate arm64 and x64 builds |
| **16 KB page alignment** | All Android native libraries aligned for Android 15+ / API 36 |
| **Native C++ library (Android)** | `libosu_native.so` — Oboe audio + Vulkan probe, NDK r29, C++20, `-O3`, LTO |

</details>

<details>
<summary><strong>osu-framework fork changes</strong></summary>

The [winnerspiros/osu-framework](https://github.com/winnerspiros/osu-framework) fork adds:

**Rendering backends:**
- Full Direct3D 12 backend (Windows) via [winnerspiros/veldrid](https://github.com/winnerspiros/veldrid)
- Android: Vulkan (primary) → OpenGL ES (fallback)
- Public `BackendInfoD3D11/D3D12/Metal/OpenGL/Vulkan` APIs for diagnostics

**Low-latency infrastructure:**
- Generic `ILowLatencyProvider` interface (NVIDIA Reflex / LatencyFlex ready)
- Latency markers in `GameHost.UpdateFrame()` / `DrawFrame()`
- `LatencyMode` setting: Off / On / Boost
- Raw keyboard input on Windows
- Async keyboard event handling (bypasses SDL event queue)

**Frame-rate limiter:**
- Unbuffered VSync for VRR displays
- Custom FPS limiter (0–1000 Hz)

**Audio engine tuning:**
- BASS device buffer: 5 ms
- Playback buffer: 25 ms (Android/iOS) / 30 ms (desktop)
- Update period: 2 ms (Android) / 3 ms (iOS/desktop)
- AAudio backend + native 48 kHz sample rate

**Performance:**
- `System.Threading.Lock` migration across hot-path call sites
- `SpinWait.SpinOnce()` replacing `Thread.Sleep(1)` in async paths
- Input `ButtonEventManager` allocation elimination
- `TimedExpiryCache` using `Environment.TickCount64`

**Platform targeting:**
- Android minimum API 33, target API 36
- Android: profiled AOT, Server GC, SDK-only linking
- iOS: `SupportedOSPlatformVersion` 13.4

</details>

<details>
<summary><strong>Veldrid fork changes</strong></summary>

The [winnerspiros/veldrid](https://github.com/winnerspiros/veldrid) fork (net10.0, C# 14) adds:

- Full D3D12 renderer with swapchain and `PersistentStagingBuffer`
- Vulkan surface creation from `ANativeWindow` (`VK_KHR_android_surface`)
- `vkQueueSubmit2` (Vulkan 1.3 / `VK_KHR_synchronization2`)
- `VK_GOOGLE_display_timing` for presentation timestamp queries
- `VK_EXT_pipeline_creation_cache_control`
- IMMEDIATE present mode on Android (uncapped frame rates)
- Vertex/index buffer binding cache
- Complete EGL 1.4 bindings for GLES fallback
- `glInvalidateFramebuffer` on offscreen FBOs
- Metal `BackendInfoMetal` with feature set queries
- D3D11/D3D12/Vulkan staging-pool O(1) swap-remove
- `System.Threading.Lock` across all backends
- 16 KB ELF page alignment for Android

</details>

<details>
<summary><strong>CI/CD pipelines</strong></summary>

| Workflow | What it does |
|---|---|
| **`release.yml`** | Multi-platform builder. Builds Windows, Linux, macOS, Android, iOS — individually or all at once. Creates a GitHub Release with all artifacts. |
| **`generate-keystore.yml`** | Generates a persistent Android signing keystore. |
| **`ci.yml`** | Code quality (InspectCode), desktop tests, Android/iOS compile verification. |

</details>

---

## Building from source

### Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- For Android: JDK 17 + `dotnet workload install android` + NDK r29 (release builds only)
- For iOS: macOS + Xcode + `dotnet workload install ios`

### Clone

```shell
git clone https://github.com/winnerspiros/osu
cd osu
```

The fork's framework and Veldrid are consumed as NuGet packages from the `winnerspiros` GitHub Packages feed (configured in `NuGet.Config`). You need a GitHub Personal Access Token with `read:packages` scope:

```shell
dotnet nuget update source winnerspiros-github \
  --username <your-gh-username> --password <your-PAT> \
  --store-password-in-clear-text
```

### Desktop (quick start)

```shell
dotnet run --project osu.Desktop
```

### Desktop release build

```shell
# Windows
dotnet publish -c Release osu.Desktop/osu.Desktop.csproj -r win-x64 --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=true -p:TrimMode=partial

# Linux
dotnet publish -c Release osu.Desktop/osu.Desktop.csproj -r linux-x64 --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=true -p:TrimMode=partial

# macOS (Apple Silicon)
dotnet publish -c Release osu.Desktop/osu.Desktop.csproj -r osx-arm64 --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=true -p:TrimMode=partial
```

### Android debug build

```shell
dotnet build -c Debug osu.Android/osu.Android.csproj
adb install osu.Android/bin/Debug/net10.0-android/sh.ppy.osulazer.apk
```

### Android release build

The easiest way is the GitHub Actions workflow: **Actions → Release Build → Run workflow → select "android"**. For local builds:

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

# 2. Build and publish
dotnet publish -c Release osu.Android/osu.Android.csproj -f net10.0-android
```

### IDE solution filters

| Filter | Use for |
|---|---|
| `osu.Desktop.slnf` | Desktop development |
| `osu.Android.slnf` | Android development |
| `osu.iOS.slnf` | iOS development |

---

## Low-latency audio: framework-level requirements

The audio latency improvements for **Windows** (WASAPI) and **Android** (Oboe) are already implemented in the [winnerspiros/osu-framework](https://github.com/winnerspiros/osu-framework) fork.

For **macOS**, **iOS**, and **Linux**, the BASS audio library (used by osu-framework) already communicates with the native audio subsystems. The key latency-reducing parameters (device buffer, playback buffer, update period) are tuned in the framework fork. However, to achieve the absolute lowest latency comparable to Oboe on Android:

| Platform | Native API | What can be done in osu-framework |
|---|---|---|
| **macOS** | Core Audio (Audio Unit HAL) | Set `kAudioDevicePropertyBufferFrameSize` to minimum supported value (~64–128 frames at 48 kHz = ~1.3–2.7 ms). Currently relies on BASS defaults. |
| **iOS** | AVAudioSession + Audio Unit | Request `setPreferredIOBufferDuration` to ~0.005 s. Set `AVAudioSession.category` to `.playback` with `.mixWithOthers`. |
| **Linux** | PipeWire (preferred) / PulseAudio / ALSA | For PipeWire: set `PIPEWIRE_LATENCY` env var to request minimum quantum (e.g. `64/48000`). For ALSA direct: set period size to 64–128 frames. BASS on Linux uses whatever backend is active. |

These are framework-level (osu-framework) changes, not osu-game changes. The BASS configuration in the framework fork already reduces buffers significantly; the next step would be direct native API calls for buffer size hinting.

---

## Contributing

Contributions are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md).

Before committing, run `dotnet format` for consistent code style. CI runs [ReSharper InspectCode](https://www.jetbrains.com/help/resharper/InspectCode.html) — run locally with `./InspectCode.ps1`.

For localisation, see [crowdin](https://crowdin.com/project/osu-web).

---

## Licence

osu!'s code and framework are licensed under the [MIT licence](https://opensource.org/licenses/MIT). See [LICENCE](LICENCE). The "osu!" and "ppy" branding is protected by trademark law. Game resources are covered separately (see [ppy/osu-resources](https://github.com/ppy/osu-resources)).

---

<p align="center">
  Based on <a href="https://github.com/ppy/osu">ppy/osu</a> by Dean Herbert (peppy) and contributors.<br>
  All upstream code is under the MIT licence.
</p>
