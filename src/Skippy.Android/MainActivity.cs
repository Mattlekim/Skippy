using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Microsoft.Xna.Framework;
using Skippy.Core;

namespace Skippy.Android;

/// <summary>
/// Android entry point. This is the thin platform shell: it creates the shared
/// <see cref="SkippyGame"/>, grabs its view and shows it fullscreen. No game
/// logic lives here.
/// </summary>
[Activity(
    Label = "Skippy",
    MainLauncher = true,
    AlwaysRetainTaskState = true,
    LaunchMode = LaunchMode.SingleInstance,
    ScreenOrientation = ScreenOrientation.SensorLandscape,
    ConfigurationChanges =
        ConfigChanges.Orientation |
        ConfigChanges.Keyboard |
        ConfigChanges.KeyboardHidden |
        ConfigChanges.ScreenSize)]
public class MainActivity : AndroidGameActivity
{
    private SkippyGame _game = null!;
    private View _view = null!;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        _game = new SkippyGame();
        _view = _game.Services.GetService(typeof(View)) as View
                ?? throw new System.InvalidOperationException(
                    "MonoGame did not provide an Android View.");

        SetContentView(_view);
        _game.Run();
    }
}
