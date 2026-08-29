# Content pipeline

Assets (textures, fonts, audio, shaders) are compiled from source files into
`.xnb` blobs by the **MonoGame Content Builder (MGCB)**. `Content.mgcb` is the
manifest describing what to build.

All three heads share this one `Content.mgcb`. The compiled `.xnb` files are
consumed identically by MonoGame (Desktop/Android) and by KNI (Web).

## One-time setup

Install the MGCB tools (they're pinned in `.config/dotnet-tools.json`):

```bash
dotnet tool restore
```

## Editing content

Open the visual editor:

```bash
dotnet mgcb-editor Content/Content.mgcb
```

Add your assets, then save. The `Desktop` and `Android` heads build content
automatically on `dotnet build` via the `MonoGameContentReference` in their
`.csproj`.

## Web head

The Web (KNI) head does not auto-run MGCB. After building content, copy the
compiled output into the web head's static files:

```bash
# builds Content/bin/<Platform>/... then copy the tree you need:
cp -r Content/bin/DesktopGL/* src/Skippy.Web/wwwroot/Content/
```

Then load assets normally with `Content.Load<T>("...")` — KNI serves the `.xnb`
over HTTP from `wwwroot/Content`.
