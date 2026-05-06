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

- `*.png`, `*.jpg`, `*.jpeg` → `*.webp`
- `*.wav`, `*.mp3` → `*.ogg`
- `*.mp4` → `*.webm`

That means you can keep call sites unchanged and still serve a compressed local file.

Example:

- Requested key: `Textures/Menu/background.png`
- Local override: `osu.Game/Resources/Textures/Menu/background.webp`

## Notes

- This mechanism is for selective high-impact assets only.
- `ppy.osu.Game.Resources` remains the default fallback source for all non-overridden assets.
