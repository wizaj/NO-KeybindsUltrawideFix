# NO Keybinds Ultrawide Fix

BepInEx plugin for **Nuclear Option** that fixes the keybinds / controls
rebinding menu on ultrawide (21:9, 32:9, …) monitors.

## The problem

The rebinding screen is Rewired's Control Mapper prefab, which has its own
Canvas separate from every other menu in the game — that's why only this
one screen misbehaves. Its `CanvasScalerFitter` picks a reference resolution
from a preset list of aspect-ratio "break points" that tops out at 2.0
(18:9). The presets are width-matched, so the canvas ends up
`refWidth / aspect` units tall — ~1055 units at the widest preset, which is
the height the menu content is laid out for. On anything wider than 18:9
the closest (2.0) preset still gets applied, the canvas comes out shorter
than the content needs, and the bottom of the menu falls off screen (at
32:9 only a couple of rows survive).

## The fix

After Rewired's fitter runs, if the real aspect ratio is wider than its
widest break point, the plugin sets the canvas reference resolution to
`(designHeight × aspect, designHeight)` — preserving the ~1055-unit design
height so every element keeps its intended size and the whole menu fits.

By default the menu window is then also capped at the widest preset's
designed width and centred on screen, so it looks like it does on a normal
monitor. Set `CenterContent = false` in the config (or via
ConfigurationManager) if you'd rather let the window stretch across the
full monitor width.

On monitors at 16:9 or narrower the plugin changes nothing.

## Install

1. Install [BepInEx 5](https://github.com/BepInEx/BepInEx/releases) into the
   game folder if you haven't already.
2. Drop `KeybindsUltrawideFix.dll` into
   `Nuclear Option\BepInEx\plugins\`.

## Config

`BepInEx\config\local.keybindsultrawidefix.cfg`

| Setting | Default | Effect |
|---|---|---|
| `Layout.CenterContent` | `true` | Cap the menu at its designed width and centre it. `false` = span the full monitor width. |

## Build

```
dotnet build -c Release
```

Expects a standard Steam install; override with
`-p:GameDir="D:\path\to\Nuclear Option"`.

## Status

v0.2.0 — tested and working in-game at 5120x1440 (32:9), game v0.34.1.
