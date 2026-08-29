using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Skippy.Core;

// ============================================================================
// Browser entry point (Blazor WebAssembly + KNI).
//
// Blazor boots the WASM runtime, then we start the shared SkippyGame. Under
// KNI, Game.Run() is non-blocking in the browser — it drives the loop off
// requestAnimationFrame — so we start the game and then run the Blazor host.
//
// NOTE: KNI's Blazor bootstrap has evolved across versions. If the exact
// start-up handshake differs in the KNI version you install, follow the current
// "BlazorGL" sample in the KNI repo (https://github.com/kniEngine/kni). The
// pieces that matter and are already wired here: the <canvas> in
// wwwroot/index.html and starting SkippyGame after the host is built.
// ============================================================================

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<Skippy.Web.App>("#app");

var host = builder.Build();

var game = new SkippyGame();
game.Run(); // non-blocking under KNI/Blazor

await host.RunAsync();
