# Resource optimizer config

- `config.json` controls workflow-time conversion of local overrides in `osu.Game/Resources`.
- Conversion is performed by `scripts/optimize_resource_overrides.py` using `ffmpeg`.

Supported conversions:

- `png/jpg/jpeg -> webp`
- `wav/mp3 -> ogg`
- `mp4 -> webm`

Safety defaults:

- `keep_original_files=true` keeps source files alongside optimized outputs.
- This allows runtime fallback behavior to remain robust while compressed overrides are preferred.
