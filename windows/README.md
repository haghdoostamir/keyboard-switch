# Keyboard Switch for Windows

Typed a whole sentence in the wrong keyboard layout? Select it, press the shortcut
(default **Ctrl+Alt+Space**), and it is retyped as if you had used the next layout, e.g.
`sghl o,fd?` → `سلام خوبی؟`. Press it again and the text cycles back. The window's layout is
switched too, so you can keep typing.

Windows 10/11. A single ~80 KB `KeyboardSwitch.exe` (C# / WinForms on .NET Framework 4.8, which is
part of Windows); nothing to install. Port of the [Linux/KDE version](../linux).

## Build / run

No SDK needed: the build uses the C# compiler that ships with Windows.

```powershell
.\build.ps1          # dist\KeyboardSwitch.exe
.\build.ps1 -Test    # + unit tests (converter, engine flow with fakes)
.\build.ps1 -E2E     # + dist\E2E.exe: real input/clipboard/layout test against its own window
dist\KeyboardSwitch.exe --dump [--standard]   # detected layouts + English/Persian test vectors
```

If scripts are blocked: `powershell -ExecutionPolicy Bypass -File build.ps1`.

To "install", copy `KeyboardSwitch.exe` anywhere (e.g. `%LOCALAPPDATA%\Programs\Keyboard Switch`),
start it, and tick *Start automatically on login*.

The **Help** link in the window (also in the tray menu) explains usage, setting it up on another
computer (SmartScreen, Unblock, antivirus) and the limitations. Its *Copy diagnostic info* button
copies the same report as `--dump`, plus the app's settings, for bug reports.

Command line: `--hidden` starts in the tray; `--convert` converts the selection in the running
instance (useful for launching from other hotkey tools).

## How it works

- **Layouts**: `GetKeyboardLayoutList`; names from the registry (`Keyboard Layouts\<KLID>\Layout Text`)
  and the language. Polled twice a second, so layouts added or removed in Windows settings show up
  by themselves. Uncheck a layout to leave it out of the cycle.
- **Tables**: every virtual key with no modifier / Shift / AltGr / Shift+AltGr goes through
  `ToUnicodeEx`, so the mapping comes from Windows itself and any layout pair works (checked: US ↔
  Persian 00000429 and Persian (Standard) 00050429).
- **Source layout**: the layout of the focused window, unless another enabled layout can type
  strictly more of the selected text. Target = next enabled layout.
- **Shortcut**: `RegisterHotKey`; a combination taken by another program is reported.
- **Selection**: waits until the shortcut keys are released, saves the clipboard (all formats that
  can be copied back: text, HTML, RTF, images, files, custom data), sends Ctrl+C and watches
  `GetClipboardSequenceNumber`. No change → nothing selected → Ctrl+A and copy again (option).
  The clipboard is restored afterwards.
- **Replacing**: the result is typed with `SendInput` Unicode events (independent of the layout),
  the window gets `WM_INPUTLANGCHANGEREQUEST`, and Shift+Left (once per grapheme) selects the
  result again.

Settings: `%APPDATA%\Keyboard Switch\settings.json`.

## Known limitations

- **Terminals** (Windows Terminal, cmd, PowerShell, mintty…): Ctrl+Shift+C is used instead of Ctrl+C
  and there's no select-all fallback, so select the text with the mouse first. The result is typed
  at the prompt cursor; the terminal doesn't delete the selected text by itself.
- **VS Code** copies the current line on Ctrl+C with no selection. It marks that copy, which is
  detected and treated as "nothing selected" (then Ctrl+A selects the whole file; selections longer
  than 5000 characters are refused).
- Fields without Ctrl+A support (e.g. some WinForms text boxes) need a selection.
- **Administrator windows**: Windows blocks input from normal apps into elevated ones (UIPI). You
  get a notification; run Keyboard Switch as administrator if you need it there.
- **Console windows** may report the layout of the wrong thread; the text-based detection
  usually corrects that.
- The temporary copies go through the clipboard, so they can appear in clipboard history (Win+V).
- Line breaks are typed as Shift+Enter, so chat apps don't send the message halfway. In Word that
  gives a line break inside the paragraph instead of a new paragraph.
