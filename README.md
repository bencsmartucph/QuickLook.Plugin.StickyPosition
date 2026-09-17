# QuickLook.Plugin.StickyPosition

A [QuickLook](https://github.com/QL-Win/QuickLook) plugin that remembers where you put the preview window, **per file type**, and opens it there every time, with no jump.

- Drag or resize a `.pdf` preview once, and every `.pdf` opens in that spot at that size. Markdown, images and folders each keep their own spot.
- File types you haven't placed yet open wherever QuickLook puts them, as if the plugin weren't installed. You can set a default position for them instead (see below).
- The window never flashes somewhere else first. QuickLook works out the window's position *before* it shows it, and the plugin changes that position at that moment, by rewriting `WM_WINDOWPOSCHANGING` on the viewer window. Plugins that move the window afterwards always show a visible jump.
- Only QuickLook's own placement of a newly opened file is changed. Dragging, snapping, maximising and fullscreen all behave normally.
- Positions are stored as fractions of the monitor's work area, so they survive resolution and scaling changes. With several monitors, the preview opens on the monitor QuickLook chose (the one with the window you pressed <kbd>Space</kbd> in), at the saved spot on that monitor.
- Previews with a fixed size (folders, unknown files, the plugin installer) keep their size; only their position is remembered.

## Install

Download `QuickLook.Plugin.StickyPosition.qlplugin` from [Releases](../../releases), select it in Explorer, press <kbd>Space</kbd> to preview it with QuickLook, click **Install**, then restart QuickLook.

## Use

- **Remember a position:** drag or resize a preview. It's saved for that file type when you let go.
- **Lock a position:** open the **⋯** menu on a preview and choose **Lock position for .pdf**. The window's current position is saved, and later drags and resizes of `.pdf` previews move only that window; the next `.pdf` still opens at the locked spot. **Unlock position for .pdf** goes back to saving every drag.
- **Forget a position:** **⋯** menu, **Forget position for .pdf**. This also unlocks the type. The next `.pdf` opens where QuickLook puts it.
- **Pinned windows:** dragging a pinned ("prevent closing") preview doesn't save anything. A new preview that would open exactly on top of another preview window is shifted down and right a little.

Settings live in `QuickLook.Plugin.StickyPosition.config`, in QuickLook's data folder (`%APPDATA%\pooi.moe\QuickLook\`, or the equivalent under `%LOCALAPPDATA%\Packages\...\LocalCache\Roaming\` for the Microsoft Store version). Each `Pos_<type>` value is `left,top,width,height` as fractions of the work area; `Lock_<type>` is `1` for a locked type. To give unplaced types a default position (here, a tall column on the right), exit QuickLook first, then add:

```xml
<DefaultPosition>0.545,0.03,0.42,0.92</DefaultPosition>
```

## Build

No .NET SDK needed. `build.ps1` compiles with the C# compiler that ships with the .NET Framework, against the `QuickLook.Common.dll` of your installed QuickLook, and writes `dist\QuickLook.Plugin.StickyPosition.qlplugin`.

```powershell
.\build.ps1                          # Microsoft Store QuickLook
.\build.ps1 -QuickLookDir D:\QuickLook   # portable QuickLook
.\build.ps1 -Install                 # also copy it into the plugin folder
```

## Caveats

- The plugin reads QuickLook internals by reflection: `ViewWindowManager._viewerWindow`, `ViewerWindow._path`, `ViewerWindow.Pinned` and `ViewerWindow.ContextObject`. A QuickLook release that renames the first two switches the plugin off: previews go back to QuickLook's usual placement, and one line in QuickLook's log says so. Tested on QuickLook 4.5.0; the names were still present in QuickLook's source on 2026-09-14.
- A position set with <kbd>Win</kbd>+<kbd>Arrow</kbd> isn't saved on its own; drag the window, or use **Lock position**.

## Credits

The idea of reaching QuickLook's viewer window by reflection comes from [QuickLook.Plugin.EnhancedPreview](https://github.com/ChouDan6/QuickLook.Plugin.EnhancedPreview) by ChouDan6 (GPL-3.0). This plugin places the window with a different technique.

## License

GPL-3.0, the same as QuickLook. See [LICENSE](LICENSE).
