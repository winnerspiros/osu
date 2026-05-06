# Mobile Resource Policy

This repository keeps `ppy.osu.Game.Resources` as the canonical upstream source and applies targeted local overrides for mobile performance.

## Goals

- Reduce APK growth from large media assets.
- Reduce decode/upload spikes on startup and first gameplay entry.
- Keep upstream compatibility without forking `ppy/osu-resources`.

## Override strategy

- Add only high-impact overrides in `osu.Game/Resources`.
- Match upstream virtual paths (`Textures/...`, `Samples/...`, `Videos/...`).
- Prefer compressed formats:
  - Textures: `webp` where quality remains acceptable.
  - Audio: `ogg` for non-critical assets.
  - Video: `webm` where supported and visually acceptable.

## Resource budgets (enforced in workflow)

- Source override limits: `.github/resource-budgets/source-overrides.json`
- Android APK media limits: `.github/resource-budgets/android-apk-media.json`

Workflow checks now fail when budgets regress for:

- Total media bytes
- Per-bucket bytes (image/audio/video)
- Largest single media file
- Top-N largest media aggregate
- Total APK size (release workflow)

## Naming and quality rules

- Keep override names stable (do not invent alternate keys).
- Avoid duplicate variants unless there is a demonstrated runtime need.
- Optimise for perceptual quality at smallest size that preserves gameplay UX.

## Conversion guidance

Use your preferred encoder tooling locally (for example `cwebp`, `ffmpeg`) before committing overrides.
