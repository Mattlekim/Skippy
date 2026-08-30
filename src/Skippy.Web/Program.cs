using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Skippy.Web;

// Browser entry point (Blazor WebAssembly + KNI).
//
// Program.cs is intentionally vanilla Blazor — it does NOT start the game.
// KNI drives the game loop from JavaScript: index.html's requestAnimationFrame
// calls TickDotNet() on the Pages/Index component (see Index.razor.cs), which
// creates the shared SkippyGame and ticks it once per frame. This matches the
// official KNI "kni-blazor-gl" template.
internal static class Program
{
    private static async Task Main(string[] args)
    {
        var builder = WebAssemblyHostBuilder.CreateDefault(args);
        builder.RootComponents.Add<App>("#app");
        builder.RootComponents.Add<HeadOutlet>("head::after");
        builder.Services.AddScoped(sp => new HttpClient
        {
            BaseAddress = new Uri(builder.HostEnvironment.BaseAddress),
        });
        await builder.Build().RunAsync();
    }
}
