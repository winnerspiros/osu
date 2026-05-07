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

Example:

- Requested key: `Textures/Menu/background.png`
- Best override: `osu.Game/Resources/Textures/Menu/background.avif`
- Fallback override: `osu.Game/Resources/Textures/Menu/background.webp`

## Notes

- This mechanism is for selective high-impact assets only.
- `ppy.osu.Game.Resources` remains the default fallback source for all non-overridden assets.
- The same fallback wrapper is also applied to current osu-side texture/audio file lookups for beatmaps, storyboards, skins, and ruleset resources where requests still use the original extension.
- CI/release workflows run `scripts/optimize_resource_overrides.py` before budget checks/build, using `.github/resource-optimizer/config.json`.
- Optimizer-generated compressed files can coexist with originals (`keep_original_files=true`) to keep compatibility safety.
