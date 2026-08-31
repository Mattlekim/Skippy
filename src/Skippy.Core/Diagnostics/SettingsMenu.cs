using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Skippy.Core.Input;
using Skippy.Core.Worlds;
using Skippy.Core.Worlds.Rendering;

namespace Skippy.Core.Diagnostics;

/// <summary>
/// The in-game settings panel: the world's knobs — the time of day, the map view, the tile
/// size — gathered somewhere they can be found, instead of each living on a key you have to
/// know about. TAB opens and closes it.
///
/// Every setting is one <see cref="Row"/>: a label, something that reads its current value
/// back as text, and a step that changes it. The panel itself knows nothing about what any
/// of them mean, so adding a setting is adding a row.
///
/// It is drawn in the same runtime 3×5 font as the read-out (the project ships no content
/// assets), and it takes over the arrow keys while it is open — the camera stops listening
/// so the same keys can walk the list.
/// </summary>
public sealed class SettingsMenu : IDisposable
{
    /// <summary>One setting: how to show it, and how to change it.</summary>
    private sealed class Row
    {
        public required string Label { get; init; }
        public required Func<string> Value { get; init; }

        /// <summary>Moves the setting one step; -1 for left, +1 for right.</summary>
        public required Action<int> Step { get; init; }

        /// <summary>What ENTER does. Steps forward by default, which suits a cycle or a toggle.</summary>
        public Action? Select { get; init; }

        /// <summary>Whether holding the key repeats the step — for values with a long range.</summary>
        public bool Repeats { get; init; }

        public Rectangle Bounds;
        public Rectangle Less, More;
    }

    private const int Scale = 3;              // screen pixels per glyph pixel
    private const int Pad = 10;               // inside the panel's border
    private const int RowGap = 6;
    private const int ColumnGap = 24;         // between a label and its value
    private const float RepeatDelay = 0.32f, RepeatEvery = 0.05f;

    private static readonly Color Ink = Color.White;
    private static readonly Color DimInk = new(168, 176, 188);
    private static readonly Color Panel = new(18, 22, 30, 225);
    private static readonly Color Border = new(96, 108, 128, 235);
    private static readonly Color Highlight = new(52, 64, 86, 235);

    private readonly PixelFont _font;
    private readonly List<Row> _rows = new();

    private int _selected;
    private float _repeatTimer;
    private int _heldDirection;

    public bool IsOpen { get; private set; }

    public SettingsMenu(GraphicsDevice gd, World world)
    {
        _font = new PixelFont(gd);
        Build(world);
    }

    public void Dispose() => _font.Dispose();

    public void Toggle() => IsOpen = !IsOpen;

    public void Close() => IsOpen = false;

    private void Build(World world)
    {
        SkyLight sky = world.Sky;

        _rows.Add(new Row
        {
            Label = "TIME OF DAY",
            Value = () => sky.Hour.ToString("00", CultureInfo.InvariantCulture) + ":"
                        + sky.Minute.ToString("00", CultureInfo.InvariantCulture),
            // Setting the time by hand holds the clock, or the value would drift off the
            // one just chosen. The row below starts it again.
            Step = dir => { sky.Paused = true; sky.Scrub(dir * (10f / (24f * 60f))); },
            Repeats = true,
        });

        _rows.Add(new Row
        {
            Label = "CLOCK",
            Value = () => sky.Paused ? "HELD" : "RUNNING",
            Step = _ => sky.Paused = !sky.Paused,
        });

        _rows.Add(new Row
        {
            Label = "DAY LENGTH",
            Value = () => (int)sky.DayLength + "S",
            Step = dir => sky.DayLength = Math.Clamp(sky.DayLength + dir * 20f, 20f, 600f),
            Repeats = true,
        });

        // The world dials: how big it is, how warm it runs, how wet it runs, and how worn
        // down it is. All of them rebuild the map — they are baked into a tile at
        // generation time, so the loaded chunks have to be thrown away for the change to
        // mean anything (see World.Regenerate).
        _rows.Add(new Row
        {
            Label = "WORLD SIZE",
            Value = () => world.Settings.SizeName,
            Step = dir =>
            {
                world.Settings.StepSize(dir);
                world.Regenerate();

                // A planet of a different size is a different planet. Where the camera
                // stood on the old one says nothing about the new one, and on a smaller
                // world it may be past a pole altogether.
                world.CenterOnEquator();
            },
        });

        _rows.Add(new Row
        {
            Label = "TEMPERATURE",
            Value = () => world.Settings.TemperatureName,
            Step = dir =>
            {
                world.Settings.StepTemperature(dir);
                world.Regenerate();
            },
        });

        _rows.Add(new Row
        {
            Label = "RAINFALL",
            Value = () => world.Settings.RainfallName,
            Step = dir =>
            {
                world.Settings.StepRainfall(dir);
                world.Regenerate();
            },
        });

        // How long the world has been weathering — which is to say how hilly it is: a young
        // world is raw mountain country, an ancient one has been ground down to plains.
        _rows.Add(new Row
        {
            Label = "PLANET AGE",
            Value = () => world.Settings.AgeName,
            Step = dir =>
            {
                world.Settings.StepAge(dir);
                world.Regenerate();
            },
        });

        _rows.Add(new Row
        {
            Label = "MAP VIEW",
            Value = () => world.MapView.Name(),
            Step = _ => world.MapView = world.MapView.Next(),
        });

        _rows.Add(new Row
        {
            Label = "TILE SIZE",
            Value = () => Iso.TileWidth.ToString(CultureInfo.InvariantCulture),
            Step = dir => Iso.TileWidth += dir * 8,
            Repeats = true,
        });

        _rows.Add(new Row
        {
            Label = "ZOOM",
            Value = () => world.Camera.Zoom.ToString("0.0", CultureInfo.InvariantCulture) + "X",
            Step = dir => world.Camera.ZoomBy(dir > 0 ? 1.1f : 1f / 1.1f),
            Repeats = true,
        });

        _rows.Add(new Row
        {
            Label = "TILE GRID",
            Value = () => world.ShowTileGrid ? "ON" : "OFF",
            Step = _ => world.ShowTileGrid = !world.ShowTileGrid,
        });
    }

    /// <summary>
    /// Walks the list and changes settings. Only called while the panel is open, and the
    /// world is not given the same input on those frames, so the arrows are unambiguous.
    /// </summary>
    public void Update(InputState input, float dt)
    {
        if (input.MenuVertical != 0)
        {
            _selected = (_selected + input.MenuVertical + _rows.Count) % _rows.Count;
            _repeatTimer = 0f;
            _heldDirection = 0;
        }

        Row row = _rows[_selected];

        if (input.MenuSelectRequested)
            (row.Select ?? (() => row.Step(1)))();

        int dir = input.MenuHorizontal;
        if (dir == 0)
        {
            _heldDirection = 0;
            _repeatTimer = 0f;
        }
        else if (dir != _heldDirection)
        {
            // A fresh press acts at once; holding it then repeats, slowly at first.
            row.Step(dir);
            _heldDirection = dir;
            _repeatTimer = RepeatDelay;
        }
        else if (row.Repeats)
        {
            _repeatTimer -= dt;
            if (_repeatTimer <= 0f)
            {
                row.Step(dir);
                _repeatTimer = RepeatEvery;
            }
        }

        HandleClicks(input);
    }

    /// <summary>Clicking a row selects it; clicking one of its arrows steps the setting.</summary>
    private void HandleClicks(InputState input)
    {
        if (!input.PointerPressed || input.PointerPosition is not Vector2 at)
            return;

        var point = new Point((int)at.X, (int)at.Y);
        for (int i = 0; i < _rows.Count; i++)
        {
            if (!_rows[i].Bounds.Contains(point))
                continue;

            _selected = i;
            if (_rows[i].Less.Contains(point)) _rows[i].Step(-1);
            else if (_rows[i].More.Contains(point)) _rows[i].Step(1);
            return;
        }
    }

    public void Draw(SpriteBatch sb, Viewport viewport)
    {
        if (!IsOpen)
            return;

        int line = PixelFont.LineHeight(Scale);
        int rowHeight = line + RowGap;

        int labelWidth = 0, valueWidth = 0;
        foreach (Row row in _rows)
        {
            labelWidth = Math.Max(labelWidth, PixelFont.Measure(row.Label, Scale));
            valueWidth = Math.Max(valueWidth, PixelFont.Measure(row.Value(), Scale));
        }

        // Room for the < and > either side of every value, so the column does not jump
        // as the selection moves down the list.
        int arrow = PixelFont.Measure("<", Scale) + Scale * 3;
        int inner = labelWidth + ColumnGap + arrow + valueWidth + arrow;
        string title = "SETTINGS";
        int width = Math.Max(inner, PixelFont.Measure(title, Scale)) + Pad * 2;
        int height = Pad * 2 + line + RowGap * 2 + _rows.Count * rowHeight;

        int x = (viewport.Width - width) / 2;
        int y = (viewport.Height - height) / 2;

        sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);

        _font.Fill(sb, new Rectangle(x - 2, y - 2, width + 4, height + 4), Border);
        _font.Fill(sb, new Rectangle(x, y, width, height), Panel);

        // No drop shadows inside the panel: it has its own background to read against, and
        // every shadow is a second pass of one quad per lit pixel of every glyph.
        _font.Draw(sb, title, new Vector2(x + Pad, y + Pad), Scale, Ink);

        int rowY = y + Pad + line + RowGap * 2;
        for (int i = 0; i < _rows.Count; i++)
        {
            Row row = _rows[i];
            bool selected = i == _selected;

            row.Bounds = new Rectangle(x + Pad / 2, rowY - RowGap / 2, width - Pad, rowHeight);
            if (selected)
                _font.Fill(sb, row.Bounds, Highlight);

            _font.Draw(sb, row.Label, new Vector2(x + Pad, rowY), Scale, selected ? Ink : DimInk);

            string value = row.Value();
            int valueRight = x + width - Pad - arrow;
            int valueX = valueRight - PixelFont.Measure(value, Scale);
            _font.Draw(sb, value, new Vector2(valueX, rowY), Scale, selected ? Ink : DimInk);

            // Only the selected row wears its arrows. On every row they would be a column of
            // chevrons down both sides of the values, which reads as clutter rather than as
            // "this is the one the keys are pointed at".
            row.Less = new Rectangle(valueX - arrow, rowY - RowGap / 2, arrow, rowHeight);
            row.More = new Rectangle(valueRight, rowY - RowGap / 2, arrow, rowHeight);
            if (selected)
            {
                _font.Draw(sb, "<", new Vector2(row.Less.X + Scale, rowY), Scale, Ink);
                _font.Draw(sb, ">", new Vector2(row.More.X + Scale * 2, rowY), Scale, Ink);
            }

            rowY += rowHeight;
        }

        sb.End();
    }
}
