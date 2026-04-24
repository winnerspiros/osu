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

### 🧪 ANGLE on Android (advanced / experimental)

[ANGLE](https://chromium.googlesource.com/angle/angle/) is Google's OpenGL-ES-on-Vulkan translator. On devices with a sketchy native GL ES driver, forcing ANGLE can work around driver bugs or even improve performance. Android 10+ supports enabling ANGLE **per app** — no APK changes required on our side:

**Option 1 — Developer Options (no PC needed):**
1. Enable Developer Options (`Settings → About phone → tap Build number 7 times`).
2. `Settings → System → Developer options → ANGLE preferences` *(name varies: "OpenGL renderer", "GLES driver" on some OEMs)*.
3. Select **osu!** and choose **`angle`** (default is `native`/`default`).
4. Force-stop and relaunch osu!.

**Option 2 — ADB (one-liner):**
```shell
adb shell settings put global angle_gl_driver_selection_pkgs sh.ppy.osulazer
adb shell settings put global angle_gl_driver_selection_values angle
adb shell am force-stop sh.ppy.osulazer
```
To revert, set the value back to `native` (or `default`).

> **Note:** This affects only the OpenGL ES path — Vulkan rendering (the default on this fork) already runs natively. Use ANGLE only if you've explicitly switched the renderer to OpenGL ES in Settings → Graphics → Renderer. A first-class "ANGLE" entry in the renderer dropdown would need framework-level work (new `RendererType` value + bundling ANGLE's native libs into the APK) and is intentionally left out until the upstream [`winnerspiros/osu-framework`](https://github.com/winnerspiros/osu-framework) fork grows it.

---

### ⚡ Rendering tuning (desktop + Android)

Settings → Graphics → Renderer now exposes the full set of fork-added options:

| Option | What it does |
|---|---|
| **Renderer** | Picks the GPU backend. On Windows you get Metal / Vulkan / D3D11 / **D3D12 (new)** / OpenGL plus their `Deferred_*` experimental variants. On Android you get Vulkan (if supported) and OpenGL ES. |
| **Frame limiter** | VSync, **VSync Unbuffered (new)** — ideal for G-Sync / FreeSync / VRR displays, 2×/4×/8× refresh, Unlimited, or **Custom (new)**. |
| **Custom draw rate limit** | Slider 0–1000 Hz, only visible when the frame limiter is set to Custom. `0` = unlimited draw thread. Useful for benchmarking or VRR-specific tuning. |
| **Low latency** | `Off` / `On` / `Boost` — drives the fork's generic `ILowLatencyProvider` (NVIDIA Reflex / LatencyFlex-ready on D3D11 & D3D12; no-op on other backends until a provider plugin is supplied). `Boost` also sleeps at the start of each update frame for lower input-to-photon latency. |

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

### 🛡️ Robustness improvements

A few hardening fixes on top of upstream that are not directly performance-related but keep the app behaving correctly across edge cases:

- **Multi-threaded execution lock-in (v145+)** — the framework's `ExecutionMode = SingleThread` is force-set to `MultiThreaded` on every startup and the threading-mode toggle is removed from Settings → Graphics → Renderer. SingleThread is strictly slower than MultiThreaded with no UX benefit, so the lock-in is unconditional across all platforms.
- **Sentry-safe init** — the app gracefully handles a missing/placeholder Sentry DSN.
- **Architecture-correct native libraries (v144+)** — `osu.Android.props` strips desktop runtime `.so` files (`runtimes/{linux,osx,ios,maccatalyst,win,…}-*/native/`) from the Android publish set and only marks Android-RID assets as `AssetType=native`, so the proper Android arm64 BASS libraries from `ppy.osu.Framework.Android`'s AAR (`jni/arm64-v8a/`) always win over the desktop `.so` files transitively pulled in by `ppy.osu.Framework.NativeLibs`. The release workflow scans every shipped `libbass*.so` for `GLIBC_*` versioned symbols (only present in glibc-linked Linux ELFs) and fails the build if any are found.
- **IPC / WebSocket polish (v144+)** — desktop external-integrations server (env-var gated, `localhost`-only) tightened on top of upstream: `WebSocketChannel` now uses a strict `UTF8Encoding(throwOnInvalidBytes: true)` decoder so malformed payloads are rejected with `InvalidPayloadData` instead of being silently replaced with `U+FFFD`, and the message-size guard accepts payloads of exactly `max_message_size` bytes (was off-by-one); `WebSocketServer.Dispose()` cancels the request loop and waits briefly for it to exit before tearing down the cancellation/reset-event handles; `OsuWebSocketProvider.Dispose()` swaps the server reference under a local, properly disposes the bounded `CancellationTokenSource` via `using`, and always disposes the `WebSocketServer` in a `finally` so listener handles can't leak across screen transitions.
- **Ranked-play song-preview playback (v144+)** — restored the `Enabled`/`CardHovered` → `PreviewTrack.Start()/Stop()` wiring on `RankedPlayCard.SongPreviewContainer` that was lost in upstream's "playback rewrite" merge. The bind now happens in the `LoadComponentAsync` continuation (so previews never race the track's async load), with both bindables driving a single `updatePlaybackState()` callback.

---

## 🔧 Under the hood

<details>
<summary><strong>Build system & toolchain</strong></summary>

| | |
|---|---|
| **.NET 10** | Upgraded from .NET 8 (upstream) to .NET 10 for the latest runtime and language improvements |
| **Framework as NuGet (fork)** | Consumes the [`winnerspiros/osu-framework`](https://github.com/winnerspiros/osu-framework) fork as `ppy.osu.Framework` / `ppy.osu.Framework.Android` / `ppy.osu.Framework.iOS` **v2026.421.1** from the winnerspiros GitHub Packages feed — enables deep platform changes without carrying a submodule |
| **Profiled AOT** | Startup-critical methods are ahead-of-time compiled for faster app launch |
| **IL trimming** | Unused code is stripped from the APK for smaller size |
| **LZ4 compression** | Assembly compression saves ~20 MB in the final APK |
| **Native C++ library** | `libosu_native.so` — Oboe audio + Vulkan probe, built with NDK r29, C++20, `-O3`, LTO |
| **16 KB page alignment** | All native libraries use 16 KB ELF alignment for Android 15+ and 16 (API 36) compliance |
| **arm64 only** | Single ABI keeps the APK small and builds fast |

</details>

<details>
<summary><strong>osu-framework fork changes (v2026.421.1)</strong></summary>

The [winnerspiros/osu-framework](https://github.com/winnerspiros/osu-framework) fork (published as NuGet v2026.421.1) layers the following on top of upstream `ppy/osu-framework`:

**Rendering backends:**
- Full **Direct3D 12** backend powered by the [winnerspiros/veldrid](https://github.com/winnerspiros/veldrid) fork — exposed as `RendererType.Direct3D12` / `Deferred_Direct3D12` in the renderer dropdown (Windows only; auto-hidden on other platforms).
- Android renderer order: Vulkan (primary) → OpenGL ES (fallback). Vulkan 1.3 requirement check with diagnostic logging.
- New public **`BackendInfoD3D11/D3D12/Metal/OpenGL/Vulkan`** APIs consumed by `VeldridExtensions.LogD3D11/LogD3D12/LogMetal/LogOpenGL/LogVulkan` — avoids re-issuing native capability queries and exposes richer diagnostics (driver name/info, fragment shading rate, mesh shaders, raytracing, enhanced barriers, etc.).

**Low-latency infrastructure (GPU + input):**
- Generic `ILowLatencyProvider` interface (with D3D11-specific `IDirect3D11LowLatencyProvider`) and default no-op implementations — ready for NVIDIA Reflex / LatencyFlex implementations on D3D11 or D3D12.
- Latency markers inserted into `GameHost.UpdateFrame()` / `DrawFrame()` (`SimulationStart/End`, `RenderSubmitStart/End`, `PresentStart/End`, `InputSample`, `TriggerFlash`).
- Provider auto-initialises on the draw thread using the native device handle from Veldrid's `BackendInfoD3D11` / `BackendInfoD3D12`.
- **New `LatencyMode` setting** (`Off` / `On` / `Boost`) — surfaced as "Low latency" in Settings → Graphics → Renderer.
- **Raw keyboard input** on Windows (`SDL_HINT_WINDOWS_RAW_KEYBOARD` enabled by default) — bypasses Windows message translation.
- **Async keyboard event handling** — when text input (IME) is inactive, `KEY_DOWN` / `KEY_UP` are handled directly in SDL's event filter, bypassing the SDL event queue for reduced input-to-render latency.

**Frame-rate limiter enhancements:**
- **Unbuffered VSync (`FrameSync.UVSync`)** — limits both draw and update threads to the exact display refresh rate. Useful for VRR / G-Sync / FreeSync displays where regular VSync adds buffering.
- **Custom FPS limiter (`FrameSync.Custom` + `CustomDrawLimit` 0–1000 Hz)** — surfaced as a "Custom draw rate limit" slider in Settings → Graphics → Renderer that appears only when Custom is selected. `0` = unlimited draw thread.

**Audio engine tuning:**
- BASS device buffer: 10 ms → 5 ms
- Playback buffer: 100 ms → 25 ms (Android) / 30 ms (iOS)
- Update period: 5 ms → 2 ms (Android) / 3 ms (iOS)
- AAudio backend enabled for BASS, native 48 kHz sample rate (matches Android/iOS hardware)
- Mixer handle made public for Oboe bridge access

**Performance (all transparent to consumers):**
- Hot-path LINQ allocations eliminated across the framework (for-loops, spans, cached collections).
- `object`-based locks migrated to `System.Threading.Lock` for lower overhead on .NET 10.
- GridContainer cell sizing uses `RequiredParentSizeToFit` instead of `BoundingBox` — avoids redundant matrix-to-parent-space transforms each layout pass.
- `VeldridExtensions.LogOpenGL` hoists cached Version / ShadingLanguageVersion out of the GL-thread execution scope (fewer unsafe `glGetString` + `Marshal.PtrToStringUTF8` calls per init).
- GL state-change, shader warm-up, texture upload, and mobile vertex-batching improvements.

**Platform targeting:**
- Full `osu.Framework.Android` / `osu.Framework.iOS` implementations.
- Android minimum bumped to **API 33** (matches app manifest), target API 36.
- Android release config: profiled AOT (`AndroidEnableProfiledAot`), partial trimming, `AndroidStripILAfterAOT=false`, no LLVM (incompatible with profiled AOT).
- iOS: `SupportedOSPlatformVersion` 13.4, trim-analysis warnings suppressed with `[DynamicallyAccessedMembers]` and `[UnconditionalSuppressMessage]`.

**Android-specific framework polish:**
- Null `ANativeWindow` guard in `VkSurfaceUtil`.
- `VeldridDevice` polls `SurfaceHandle` for up to 5 s when the Android surface is not yet ready.
- `DrawThread.OnInitialize()` wraps the initial `BeginFrame` in try-catch for graceful handling before surface readiness.
- NRE fix in `GraphicsPipeline.cs` (null-conditional `ResourceLayouts?.Length`).

</details>

<details>
<summary><strong>Veldrid fork changes</strong></summary>

The [winnerspiros/veldrid](https://github.com/winnerspiros/veldrid) fork (net10.0, C# 14, `System.Threading.Lock`) powers the framework above. It adds:

**Direct3D 12 backend:**
- Full D3D12 renderer with swapchain creation (`VeldridDevice.CreateD3D12`) and `PersistentStagingBuffer`.
- `BackendInfoD3D12`: `SupportsEnhancedBarriers`, `SupportsMeshShaders`, `SupportsVariableRateShading`, `SupportsRaytracing`, device handle for low-latency providers.
- D3D12 redundant-state caching, staging-pool swap-remove.

**Android Vulkan rendering:**
- Vulkan surface creation from `ANativeWindow` via `VK_KHR_android_surface`.
- Android-specific extension detection and enablement.
- `VK_EXT_host_image_copy`, push descriptors, dynamic rendering, pipeline-cache optimisations.

**OpenGL ES fallback:**
- Complete EGL 1.4 bindings for GLES 2.0/3.0 context creation.
- Proper stencil buffer initialisation (critical for osu!'s UI).
- OpenGL pipeline state caching; `BackendInfoOpenGL` caches `Version` / `ShadingLanguageVersion` off-thread.

**Metal / D3D11 / general:**
- `BackendInfoMetal` with `MaxFeatureSet` / `FeatureSet`, merged layout-offset loops.
- `BackendInfoD3D11` exposing `FeatureLevel`, `DeviceId`, native `Device` handle (no redundant COM RCW).
- D3D11/D3D12 staging-pool swap-remove for faster buffer recycling.

**Performance, all backends:**
- `System.Threading.Lock` migration across every GPU backend.
- UTF-8 string literals for zero-allocation Vulkan lookups.
- Vulkan fence early-out to avoid blocking waits.
- Screen-tearing support for lowest-latency present modes.
- `Vortice.Windows` bumped to 3.8.3.

**Android packaging:**
- `veldrid-spirv` built with 16 KB ELF page alignment (Android 15+ / API 36 compliance).

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
git clone https://github.com/winnerspiros/osu
cd osu
```

The fork's framework + Veldrid are consumed as NuGet packages from the `winnerspiros` GitHub Packages feed (configured in `NuGet.Config`), so there are no git submodules to initialise. You'll need a GitHub Personal Access Token with `read:packages` scope to restore — the CI workflows pass `GITHUB_TOKEN` automatically:

```shell
dotnet nuget update source winnerspiros-github \
  --username <your-gh-username> --password <your-PAT> \
  --store-password-in-clear-text
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
