# Resource optimizer config

- `config.json` controls workflow-time conversion of local overrides in `osu.Game/Resources`.
- Conversion is performed by `scripts/optimize_resource_overrides.py` using `ffmpeg`.

Android-friendly defaults:

- `png/jpg/jpeg -> webp`
- `wav/mp3 -> ogg (Opus by default, 48kHz target)`
- `mp4 -> webm`

Image quality strategy:

- PNGs can produce both lossless and lossy WebP candidates; the optimizer keeps the smallest valid candidate.
- Lossy image outputs can be gated by SSIM (`measure_image_ssim` + `image_lossy_min_ssim`).
- Alpha PNGs can use a separate quality setting (`png_webp_alpha_lossy_quality`).

Audio strategy:

- Default codec is `libopus` in `.ogg`, tuned for good size/quality on Android.
- Output sample rate/channels are configurable (`audio_target_sample_rate_hz`, `audio_target_channels`).
- Optional path filters can force mono for selected samples (`audio_force_mono_globs`).

Safety and compatibility:

- `keep_original_files=true` keeps source files alongside optimized outputs.
- This allows runtime fallback behavior to remain robust while compressed overrides are preferred.
