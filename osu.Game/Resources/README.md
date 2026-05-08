# Local resource overrides

This directory is loaded **before** `ppy.osu.Game.Resources` at runtime.

Use it to override upstream resources without forking `ppy/osu-resources`.

## Placement rules

- Put files under the same virtual path used by the game:
  - `Textures/...`
  - `Samples/...`
  - `Videos/...`
- Keep filenames identical to the upstream key you want to override.

## Optimised extension fallback

The game now checks these alternatives first for local overrides:

- `*.png`, `*.jpg`, `*.jpeg` → `*.avif` (tried first), then `*.webp`, then original
- `*.wav`, `*.mp3` → `*.ogg` (Opus in Ogg from the content pipeline)
- `*.mp4` → `*.webm`

That means you can keep call sites unchanged and still serve a compressed local file.

**AVIF vs WebP**: AVIF is tried first because it offers better compression than WebP at equal quality (attractive for large backgrounds/splash art). WebP is the safe fallback because it is supported everywhere. The framework's `TextureLoaderStore` applies the same AVIF-first order with ImageSharp capability checking, so AVIF will be silently skipped on any platform where ImageSharp cannot decode it.

**⚠️ Do NOT generate AVIF for PNG files with alpha channels (e.g. font atlases, UI sprites)**: `libsvtav1` encodes only yuv420p and silently strips the alpha channel, producing a tiny (~350 byte) but completely blank/solid output. `libaom-av1` would preserve alpha via yuva420p but is extremely slow and AVIF alpha support is inconsistent on Android. Use WebP for all PNG assets — WebP lossless perfectly preserves alpha. AVIF is only safe for JPEG-sourced images (no alpha channel) and even then savings over WebP are marginal.

Example:

- Requested key: `Textures/Menu/background.png`
- Best override: `osu.Game/Resources/Textures/Menu/background.avif` *(only for JPEG-origin / no-alpha images)*
- Safe override: `osu.Game/Resources/Textures/Menu/background.webp`

## Notes

- This mechanism is for selective high-impact assets only.
- `ppy.osu.Game.Resources` remains the default fallback source for all non-overridden assets.
- Both the local override store and the upstream osu-resources store are wrapped with `OptimisedMediaResourceStore`, ensuring that raw `byte[]` lookups with the original extension transparently fall through to the compressed format.
- CI/release workflows run `scripts/optimize_resource_overrides.py` before budget checks/build, using `.github/resource-optimizer/config.json`.
- Optimizer-generated compressed files can coexist with originals (`keep_original_files=true`) to keep compatibility safety.
