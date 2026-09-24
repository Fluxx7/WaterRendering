# Godot Water Rendering

GPU ocean simulation and rendering in Godot 4.8, written against
[Fluxxi Shader Language](https://github.com/Fluxx7/FluxxiShaderLanguage-Godot) (FSL).
The wave field is a Tessendorf-style inverse-FFT spectrum (JONSWAP and friends,
with selectable directional spreading) evaluated entirely on the GPU, rendered
onto a GPU-generated mesh.

Main scene: [Scenes/ocean_rendering.tscn](Scenes/ocean_rendering.tscn).

> **This project does not run on a stock Godot build.** It depends on four
> engine PRs that are not yet merged upstream. See
> [Requirements](#requirements) before cloning — on an unmodified Godot 4.8 the
> compute shaders fail in ways that look like "the water is flat" rather than
> like an error.

## Requirements

### A Godot build with four PRs applied

Build Godot from source (`4.8`, `.NET`/Mono enabled) with these merged:

| PR | Title | Why it's needed |
| --- | --- | --- |
| [117836](https://github.com/godotengine/godot/pull/117836) | Implement MeshRD for RenderingDevice buffers | The ocean mesh is built in compute and handed to the renderer as `RenderingDevice` buffers; without it there is no way to draw the generated mesh. |
| [122071](https://github.com/godotengine/godot/pull/122071) | Metal: use specialization constants that set the compute workgroup size | The FFT and spectrum kernels set their workgroup size via specialization constants. On stock Metal these dispatch **zero invocations, silently** — no error, just an empty result. |

Both are open as of this writing, so there is no release to download; you
need your own build.

### .NET

.NET 8 SDK. The project builds with `Godot.NET.Sdk/4.8.0-dev`.

## Getting started

```sh
git clone https://github.com/Fluxx7/WaterRendering.git
cd WaterRendering
dotnet build
```

The FSL addon is vendored into the repository (see below), so a plain `clone`
is enough — there are no submodules to initialize.

Open the project with your patched Godot build and run
`Scenes/ocean_rendering.tscn`. Wave spectrum, spreading, cascade and mesh
parameters are exposed as in-simulation controls.

## The FSL addon

[addons/fluxxishaderlang](addons/fluxxishaderlang) is vendored with `git
subtree` from the `addon-dist` branch of the FSL repository. That branch is a
subtree split of FSL's `project/addons/fluxxishaderlang`, so it contains only
what a consuming project needs — the `.gdextension`, the C# bindings and the
prebuilt native libraries — and none of the C++ source or the `godot-cpp`
checkout.

To pull in a newer FSL build:

```sh
git subtree pull --prefix=addons/fluxxishaderlang \
  https://github.com/Fluxx7/FluxxiShaderLanguage-Godot.git addon-dist --squash
```

`git log addons/fluxxishaderlang` shows which FSL revision is currently
vendored. The branch is republished automatically by FSL's
`publish-addon.yml` workflow whenever its addon directory changes on `main`.

### Platform support

The vendored binaries currently cover **macOS only** (`template_debug`). The
`.gdextension` declares paths for Windows, Linux, Android, iOS and web, but
those artifacts are not yet published, so the extension will fail to load on
those platforms. Building them is a matter of running FSL's
`make_build.yml` workflow and committing the results into its addon `bin/`
directory.

## Shaders

FSL sources live under [assets/Shaders/Compute/FSL/](assets/Shaders/Compute/FSL/):

- `ocean/` — spectrum generation, directional spreading, the optimized FFT, and
  mipmap/derivative passes.
- `mesh/` — GPU mesh generation, including concurrent-binary-tree scaffolding.
- `ocean/old/` — earlier, unoptimized implementations kept for reference.

Godot-side surface and vertex shaders are in [assets/Shaders/](assets/Shaders/).

## AI Disclosure
This README was written by Claude Code, all of the actual code in the project was written by a human with no AI involvement beyond debugging assistance

## License

The `sky_3d` and `csharp_gdextension_bindgen` addons carry their own licenses;
see their directories.
