# Unity Layout Manager

Dock Unity editor windows without drag-and-drop. Useful under tiling window managers
(niri, i3, sway, Hyprland, ...), where Unity's drag-to-dock doesn't work because the WM
grabs the floating drag window.

It drives Unity's own docking code (the same code a tab drop runs) through reflection, so
the layouts it produces are normal Unity layouts. They save and load with Window → Layouts.

Requires Unity 6. Tested against the internals of 6000.3, 6000.4 and 6000.6. If a future
version changes the internals, the window shows which members are missing.

## Install

Package Manager → **+** → *Install package from git URL…*:

```
https://github.com/graphicmismatch/UnityLayoutManager.git
```

## Visual manager

**Window → Layout Manager** (Alt+Shift+L) shows a live map of the main window and every
floating window.

- Click a tab to pick it up. Then click a zone on any pane: the center adds it as a tab,
  an edge splits it off to that side. The outer border docks it to that edge of the whole
  window. Esc cancels.
- Right-click a tab: Focus, Toggle Maximize, Gather Here, Close.
- **Gather Floating** moves every floating window into the main window as tabs.

## Shortcuts

Click the window to dock next to, click the window to move, then press:

| Action                                  | Default              |
|-----------------------------------------|----------------------|
| Open Layout Manager                     | Alt+Shift+L          |
| Dock as tab                             | Alt+Shift+Space      |
| Dock left / right / above / below       | Alt+Shift+Arrow      |
| Dock to that edge of the whole window   | Ctrl+Alt+Shift+Arrow |
| Gather floating windows                 | unbound              |

Rebind them under **Edit → Shortcuts → Layout Manager**.

## License

GPL-3.0. See [LICENSE](LICENSE).
