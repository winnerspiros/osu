# `local-packages/` — vendored NuGet packages

This folder hosts pre-built `.nupkg` files for fork dependencies that are not
published to a public NuGet feed and that GitHub Packages cannot host (e.g.
release-asset-only artifacts).

It is wired up as a NuGet source in the repo-root `NuGet.Config`:

```xml
<add key="local-packages" value="./local-packages" />
```

## Current contents

| Package                         | Version                | Why vendored |
|---------------------------------|------------------------|--------------|
| `ppy.Veldrid.SPIRV`             | `1.0.15-gb268bf39ea`   | This fork build (from <https://github.com/winnerspiros/veldrid-spirv/releases/tag/1.0>) ships `runtimes/android-arm64/native/libveldrid-spirv.so` aligned to **16 KB pages**, which is required for Android 16+. The version published on nuget.org (`1.0.15-gb66ebf81d2`) is 4 KB-aligned and triggers a build warning when packaging the APK. The version is referenced by `ppy.osu.Framework 2026.422.1` and re-pinned explicitly in `osu.Game/osu.Game.csproj` so resolution is deterministic. |
| `ManagedBass`                   | `2026.506.1`           | **Stub package** (no assemblies). `ppy.osu.Framework.Android 2026.506.1` bundles `ManagedBass.dll` directly in its lib/ but its nuspec incorrectly declares `ManagedBass >= 2026.506.1` as a NuGet dependency because `osu.Framework.Android.csproj` uses a bare `ProjectReference` (without `PrivateAssets="all"`) to the ManagedBass submodule. The stub satisfies the version constraint; the actual DLL is resolved from the framework package. **Long-term fix**: add `PrivateAssets="all"` + `IncludeSubmoduleAssemblies` target to `osu.Framework.Android.csproj` in [winnerspiros/osu-framework](https://github.com/winnerspiros/osu-framework) and re-publish. |
| `ManagedBass.Mix`               | `2026.506.1`           | Same as `ManagedBass` above, but for `ppy.osu.Framework.iOS`. The iOS framework csproj has a bare ProjectReference to `BassMix.csproj`. |
| `ManagedBass.Fx`                | `2026.506.1`           | Same as `ManagedBass` above, but for `ppy.osu.Framework.iOS`. The iOS framework csproj has a bare ProjectReference to `BassFx.csproj`. |

## Updating

When the fork rebuilds the package, drop the new `.nupkg` here, bump the
`Version` in `osu.Game/osu.Game.csproj` (and any direct framework reference if
the dep target version changes), and delete the old `.nupkg` so the folder
stays small.
