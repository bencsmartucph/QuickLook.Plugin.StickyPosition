# QuickLook.Plugin.StickyPosition

A [QuickLook](https://github.com/QL-Win/QuickLook) plugin that remembers where you put the preview window, **per file type**, and opens it there every time, with no jump.

- Drag or resize a `.pdf` preview once, and every `.pdf` opens in that spot at that size. Markdown, images and folders each keep their own spot.
- File types you haven't placed yet open in a tall column on the right of the screen. You can change that default (see below).
- The window never flashes somewhere else first. QuickLook works out the window's position *before* it shows it, and the plugin changes that position at that moment, by rewriting `WM_WINDOWPOSCHANGING` on the viewer window. Plugins that move the window afterwards always show a visible jump.
- Only QuickLook's own placement of a newly opened file is changed. Dragging, snapping, maximising and fullscreen all behave normally.
- Positions are stored as fractions of the monitor's work area, so they survive resolution and scaling changes.

## Install

Download `QuickLook.Plugin.StickyPosition.qlplugin` from [Releases](../../releases), select it in Explorer, press <kbd>Space</kbd> to preview it with QuickLook, click **Install**, then restart QuickLook.

## Use

- **Remember a position:** drag or resize a preview. It's saved for that file type when you let go.
- **Forget a position:** open the **⋯** menu on a preview and choose **Forget position for this file type**. The window goes back to the default spot.

Settings live in `QuickLook.Plugin.StickyPosition.config`, in QuickLook's data folder (`%APPDATA%\pooi.moe\QuickLook\`, or the equivalent under `%LOCALAPPDATA%\Packages\...\LocalCache\Roaming\` for the Microsoft Store version). Each value is `left,top,width,height` as fractions of the work area. To change the default for unplaced types, exit QuickLook first, then add:

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

- The plugin reads QuickLook internals by reflection: `ViewWindowManager._viewerWindow` and `ViewerWindow._path`. A QuickLook release that renames either will quietly switch the plugin off, and previews go back to QuickLook's usual placement. Tested on QuickLook 4.5.0.
- The very first preview after pinning a window may open at QuickLook's own position.

## Credits

The idea of reaching QuickLook's viewer window by reflection comes from [QuickLook.Plugin.EnhancedPreview](https://github.com/ChouDan6/QuickLook.Plugin.EnhancedPreview) by ChouDan6 (GPL-3.0). This plugin places the window with a different technique.

## License

GPL-3.0, the same as QuickLook. See [LICENSE](LICENSE).
