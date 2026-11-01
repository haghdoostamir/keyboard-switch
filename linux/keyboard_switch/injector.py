"""Reading the selection and typing on Wayland.

Selection/clipboard go through wl-clipboard (KWin implements the data-control protocol).
Keystrokes go through a virtual keyboard created with /dev/uinput, so KWin interprets
them with the active layout exactly like real key presses.
"""

import os
import shutil
import subprocess
import time

from .xkbmap import ALTGR, EVDEV_OFFSET, SHIFT

try:
    import evdev
    from evdev import ecodes as e
except ImportError:  # reported by check_environment()
    evdev = None

MODIFIER_CODES = (
    "KEY_LEFTSHIFT", "KEY_RIGHTSHIFT", "KEY_LEFTCTRL", "KEY_RIGHTCTRL",
    "KEY_LEFTALT", "KEY_RIGHTALT", "KEY_LEFTMETA", "KEY_RIGHTMETA",
)


def check_environment():
    """List of human readable problems that prevent the app from working."""
    problems = []
    if evdev is None:
        problems.append("python-evdev is not installed")
    if not shutil.which("wl-paste") or not shutil.which("wl-copy"):
        problems.append("wl-clipboard is not installed")
    if not os.access("/dev/uinput", os.W_OK):
        problems.append("no write access to /dev/uinput (run install.sh, then log out and back in)")
    if not any(os.access(f"/dev/input/{d}", os.R_OK)
               for d in os.listdir("/dev/input") if d.startswith("event")):
        problems.append("no read access to /dev/input (user is not in the 'input' group)")
    return problems


# ---------------------------------------------------------------- clipboard

def _run(args, data=None, timeout=1.0):
    try:
        res = subprocess.run(args, input=data, capture_output=True, timeout=timeout)
    except (OSError, subprocess.TimeoutExpired):
        return None
    return res.stdout if res.returncode == 0 else None


def get_selection():
    out = _run(["wl-paste", "--primary", "--no-newline", "--type", "text/plain;charset=utf-8"])
    if out is None:
        out = _run(["wl-paste", "--primary", "--no-newline"])
    return out.decode("utf-8", "replace") if out else ""


def get_clipboard():
    out = _run(["wl-paste", "--no-newline"])
    return out.decode("utf-8", "replace") if out is not None else None


def set_clipboard(text):
    # wl-copy forks to keep serving the data, so don't wait for it to exit.
    try:
        proc = subprocess.Popen(["wl-copy", "--type", "text/plain;charset=utf-8"],
                                stdin=subprocess.PIPE, stdout=subprocess.DEVNULL,
                                stderr=subprocess.DEVNULL)
        proc.communicate(text.encode(), timeout=1.0)
    except (OSError, subprocess.TimeoutExpired):
        pass


# ---------------------------------------------------------------- keyboard

class Keyboard:
    """Virtual keyboard; keep one alive so KWin has already picked the device up."""

    def __init__(self, delay=0.004):
        self.delay = delay
        self._modifiers = [getattr(e, name) for name in MODIFIER_CODES]
        keys = [code for code in e.KEY.keys() if code < e.KEY_MAX and code < 0x2ff]
        self._dev = evdev.UInput({e.EV_KEY: keys}, name="keyboard-switch virtual keyboard")
        self._sources = self._find_keyboards()

    @staticmethod
    def _find_keyboards():
        found = []
        for path in evdev.list_devices():
            try:
                dev = evdev.InputDevice(path)
            except OSError:
                continue
            if "keyboard-switch" in dev.name:
                dev.close()
                continue
            if e.KEY_A in dev.capabilities().get(e.EV_KEY, []):
                found.append(dev)
            else:
                dev.close()
        return found

    def wait_for_modifiers_release(self, timeout=2.0):
        """Wait until the user lets go of the shortcut, so it doesn't mix with typed keys."""
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            held = False
            for dev in self._sources:
                try:
                    if set(dev.active_keys()) & set(self._modifiers):
                        held = True
                        break
                except OSError:
                    pass
            if not held:
                return True
            time.sleep(0.02)
        return False

    def _emit(self, code, value):
        self._dev.write(e.EV_KEY, code, value)
        self._dev.syn()
        time.sleep(self.delay)

    def tap(self, code, modifiers=()):
        for m in modifiers:
            self._emit(m, 1)
        self._emit(code, 1)
        self._emit(code, 0)
        for m in reversed(modifiers):
            self._emit(m, 0)

    def type_keys(self, keys):
        """Type a list of (xkb keycode, combo) pairs; KWin applies the active layout."""
        for kc, combo in keys:
            mods = []
            if combo & SHIFT:
                mods.append(e.KEY_LEFTSHIFT)
            if combo & ALTGR:
                mods.append(e.KEY_RIGHTALT)
            self.tap(kc - EVDEV_OFFSET, mods)

    def paste(self):
        self.tap(e.KEY_V, [e.KEY_LEFTCTRL])

    def select_all(self):
        self.tap(e.KEY_A, [e.KEY_LEFTCTRL])

    def select_left(self, count):
        self._emit(e.KEY_LEFTSHIFT, 1)
        for _ in range(count):
            self._emit(e.KEY_LEFT, 1)
            self._emit(e.KEY_LEFT, 0)
        self._emit(e.KEY_LEFTSHIFT, 0)

    def close(self):
        self._dev.close()
        for dev in self._sources:
            dev.close()


def keys_for_text(text, layout):
    """Physical keys that type `text` in `layout`, or None if some character can't be typed."""
    special = {"\n": e.KEY_ENTER, "\t": e.KEY_TAB}
    keys = []
    for ch in text:
        if ch in special:
            keys.append((special[ch] + EVDEV_OFFSET, 0))
        elif ch == "\r":
            continue
        elif ch in layout.rev:
            keys.append(layout.rev[ch])
        else:
            return None
    return keys
