using Microsoft.JSInterop;
using Microsoft.Xna.Framework;
using Skippy.Core;

namespace Skippy.Web.Pages;

/// <summary>
/// Hosts the game canvas and bridges KNI's JS-driven frame loop to the shared
/// <see cref="SkippyGame"/>. On first render we hand a reference to JS
/// (initRenderJS); JS then calls <see cref="TickDotNet"/> once per animation
/// frame. The Game is created lazily on the first tick, once the WASM runtime
/// and canvas are guaranteed live. (JsRuntime comes from @inject in Index.razor.)
/// </summary>
public partial class Index
{
    private Game? _game;

    protected override void OnAfterRender(bool firstRender)
    {
        base.OnAfterRender(firstRender);

        if (firstRender)
        {
            JsRuntime.InvokeAsync<object>("initRenderJS", DotNetObjectReference.Create(this));
        }
    }

    [JSInvokable]
    public void TickDotNet()
    {
        if (_game == null)
        {
            _game = new SkippyGame();
            _game.Run(); // KNI: sets up the game; the loop is pumped by Tick() below.
        }

        _game.Tick();
    }
}
