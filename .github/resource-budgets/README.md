# Resource budget files

- `source-overrides.json`: limits for local override media under `osu.Game/Resources`.
- `android-apk-media.json`: limits for media packaged into the final Android APK.

`android-apk-media.json` currently uses `max_apk_bytes = 320000000` as a direct-distribution guardrail for this fork while still failing on major regressions.
