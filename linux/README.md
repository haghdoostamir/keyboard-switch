# Keyboard Switch

Typed a whole sentence in the wrong keyboard layout? Select it, press the shortcut
(default **Meta+Shift+Space**), and it is retyped in the next layout, e.g. `sghl o,fd?` → `سلام خوبی؟`.
Press it again and the text cycles back. The system layout is switched too, so you can keep typing.

Target: Arch Linux, KDE Plasma 6 on Wayland.

## Install

```sh
./install.sh        # pacman deps, /dev/uinput access, 'input' group, menu entry
# log out and back in once (group change)
./keyboard-switch   # or "Keyboard Switch" from the app menu
```

## How it works

- **Layouts** come from KDE (`~/.config/kxkbrc` + `org.kde.keyboard` DBus); the window lets you
  choose which ones take part in the cycle.
- **Mapping** between layouts is computed with libxkbcommon from the real XKB layouts, so any
  layout pair works, not only English/Persian.
- **Source layout** = the active layout, unless the selected text clearly belongs to another one.
- **Shortcut** is registered with KDE's kglobalaccel, so it also appears (and can be changed) in
  System Settings → Shortcuts.
- **Selection** is read from the Wayland primary selection (`wl-paste --primary`).
- **Replacing**: the layout is switched and the same physical keys are retyped through a virtual
  uinput keyboard. Text that can't be typed (emoji…) is pasted through the clipboard instead,
  and the previous clipboard content is restored.

Command line: `keyboard-switch --convert` triggers a conversion in the running instance,
`--hidden` starts in the tray.

Tests: `python -m unittest discover -s tests -t .`
