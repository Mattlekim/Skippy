# Architecture

## The core idea: one game, three thin heads

```
                       ┌─────────────────────────┐
                       │      Skippy.Core         │
                       │  (all game logic; uses   │
                       │  Microsoft.Xna.Framework)│
                       └────────────┬─────────────┘
                 linked & recompiled into each head
        ┌───────────────────────────┼───────────────────────────┐
        ▼                           ▼                           ▼
┌───────────────┐          ┌───────────────┐          ┌────────────────┐
│ Skippy.Desktop│          │ Skippy.Android│          │  Skippy.Web     │
│  MonoGame     │          │  MonoGame     │          │  KNI (Blazor    │
│  DesktopGL    │          │  Android      │          │  WASM + WebGL)  │
│  Windows/     │          │  APK on your  │          │  runs in any    │
│  Linux/macOS  │          │  phone        │          │  browser        │
└───────────────┘          └───────────────┘          └────────────────┘
```

`Skippy.Core` contains the entire game. Each head is a small bootstrap that
starts the platform and creates a `SkippyGame`. The heads carry **no game
logic** — if you find yourself writing gameplay in a head, it belongs in Core.

## How the shared code is shared

The shared sources are **linked** into each head via a glob in every head's
`.csproj`:

```xml
<Compile Include="..\Skippy.Core\**\*.cs" LinkBase="Skippy.Core" />
```

Each head recompiles the same source with **its own** framework package. This is
deliberate:

- It works with plain `dotnet build` on any OS and in headless CI (no Visual
  Studio "Shared Project" / CodeSharing targets required).
- It lets each platform bind `Microsoft.Xna.Framework` to the right
  implementation — MonoGame on Desktop/Android, KNI in the browser — while the
  game code stays identical.

Because MonoGame and KNI both expose the `Microsoft.Xna.Framework` API, the same
`SkippyGame`, `SpriteBatch`, `Vector2`, input, etc. compile against all three.

> Keep Core to the common, stable XNA surface (SpriteBatch, Texture2D,
> GraphicsDeviceManager, Keyboard/Mouse/TouchPanel, math types). Avoid
> platform-only extensions in shared code; if you need one, put it behind a
> small interface and implement it per head.

## Input: one scheme, every device

`Skippy.Core/Input/InputState.cs` folds keyboard, mouse and touch into a single
per-frame snapshot (`MoveDirection`, `PointerPosition`, `PointerDown`, …). Game
logic reads that snapshot and never touches raw device APIs, so the same code
feels right with a keyboard on a PC and with a finger on a phone.

## Package versions in one place

`Directory.Packages.props` uses **Central Package Management** — every NuGet
version for every head is declared once. Heads reference packages *without*
versions. Upgrading MonoGame or KNI is a one-line change.

## Content pipeline

One `Content/Content.mgcb` feeds all heads. MGCB compiles assets to `.xnb`,
which MonoGame and KNI both load. Desktop/Android build content automatically;
the Web head copies compiled `.xnb` into `wwwroot/Content` (see
`Content/README.md`). The starter game uses a runtime-generated texture so it
runs before any content exists.

## The browser build, in detail

`Skippy.Web` is a Blazor WebAssembly app. The KNI browser loop is JS-driven, so
the wiring matters — this mirrors the official `kni-blazor-gl` template and is
verified to render (see `docs/web-preview.png`):

1. `Program.cs` is vanilla Blazor — it does **not** start the game. It just
   mounts the `App` router.
2. `Pages/Index.razor` supplies `#canvasHolder` + `#theCanvas`. Its code-behind
   (`Index.razor.cs`), on first render, hands a `DotNetObjectReference` to JS via
   `initRenderJS`.
3. `wwwroot/index.html` loads KNI's JS interop globals (`nkJSObject`, `nkCanvas`,
   … from `_content/nkast.Wasm.*`) **before** the Blazor runtime, sizes the
   canvas, then pumps `requestAnimationFrame → TickDotNet()` every frame.
4. `TickDotNet()` lazily creates the shared `SkippyGame` (`_game.Run()`) and
   ticks it once per frame (`_game.Tick()`).
5. `dotnet publish` emits a fully static site (`wwwroot`) — host it anywhere
   (GitHub Pages, Netlify, S3, …). CI wires up GitHub Pages.

Two version-coupled details to know when you upgrade KNI: the `_content/nkast.Wasm.*`
JS filenames carry a version suffix (this pin uses `8.0.2`; newer KNI also moves
`JSObject` to `_content/nkast.Wasm.JSInterop/`), and the `#theCanvas` / `initRenderJS`
handshake comes from the current
[`kni-blazor-gl` template](https://github.com/kniEngine/kni/tree/main/Templates/dotnetTemplates/content/BlazorGL.NetCore.CSharp).
If the browser console reports a missing JS global after an upgrade, refresh the
`<script>` paths in `index.html` to match the new `_content/` filenames. The
architecture around it (shared Core, static publish, Pages deploy) is stable.

## Why these choices

- **MonoGame for PC/Android** — the mature, expected engine with the best tooling
  and the content pipeline.
- **KNI for the browser** — the only battle-tested way to run
  `Microsoft.Xna.Framework` code in a browser today, and it's a drop-in fork, so
  the browser isn't a separate rewrite.
- **Linked source over Shared Projects** — portable, CI-friendly, no IDE lock-in.
- **The Web head doubles as the phone preview** — it's how you "see things
  without being at your computer," and CI keeps a live URL updated on every push.

## Extending it

- **New gameplay** → add classes under `src/Skippy.Core`. They compile into all
  three heads automatically.
- **A new platform** (e.g. iOS) → add a head project referencing the matching
  framework package (`MonoGame.Framework.iOS`) plus the same `<Compile Include>`
  glob, and add it to the solution.
- **Platform-specific behavior** → define an interface in Core, implement it in
  each head, and pass it into `SkippyGame`.
