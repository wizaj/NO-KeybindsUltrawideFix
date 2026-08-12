# NO Keybinds Ultrawide Fix

BepInEx plugin for **Nuclear Option** that fixes the keybinds / controls
rebinding menu on ultrawide (21:9, 32:9, …) monitors.

## The problem

The rebinding screen is Rewired's stock Control Mapper prefab, which has its
own Canvas separate from every other menu in the game — that's why only this
one screen misbehaves. Its `CanvasScalerFitter` picks a reference resolution
from a preset list of aspect-ratio "break points" that tops out at 16:9. On
an ultrawide the 16:9 preset gets applied and the width-driven canvas scaler
blows the UI up until only a couple of rows fit on screen.

## The fix

After Rewired's fitter runs, if the real aspect ratio is wider than its
widest break point, the plugin widens the canvas reference resolution to
match the real aspect ratio (same height — so every element keeps exactly
the size it would have on a 16:9 monitor of the same height).

By default the menu window is then also capped at its designed 16:9 width
and centred on screen, so it looks identical to a normal monitor. Set
`CenterContent = false` in the config (or via ConfigurationManager) if you'd
rather let the window stretch across the full monitor width.

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
| `Layout.CenterContent` | `true` | Cap the menu at its designed 16:9 width and centre it. `false` = span the full monitor width. |

## Build

```
dotnet build -c Release
```

Expects a standard Steam install; override with
`-p:GameDir="D:\path\to\Nuclear Option"`.

## Status

v0.1.0 — built, not yet tested in-game.
