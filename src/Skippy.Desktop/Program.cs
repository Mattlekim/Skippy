using System;
using Skippy.Core;

// PC entry point. Nothing platform-specific belongs here beyond starting the
// game — all logic lives in Skippy.Core.
using var game = new SkippyGame();
game.Run();
