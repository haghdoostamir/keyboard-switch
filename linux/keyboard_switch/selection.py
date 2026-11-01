"""Tell whether the primary selection still reflects something selected right now.

Wayland keeps the last selected text in the primary selection even after the user clicks
elsewhere or starts typing, and apps can't be asked whether they have a selection. So we
compare two timestamps:
  - when the primary selection last changed (wl-paste --watch), and
  - when the user last did something that drops a selection: a click, a touchpad tap,
    or a key press other than modifiers/shortcuts.
If the selection changed after that, it is the current selection.
"""

import select
import subprocess
import threading
import time

import evdev
from evdev import ecodes as e

_MODIFIERS = {
    e.KEY_LEFTSHIFT, e.KEY_RIGHTSHIFT, e.KEY_LEFTCTRL, e.KEY_RIGHTCTRL,
    e.KEY_LEFTALT, e.KEY_RIGHTALT, e.KEY_LEFTMETA, e.KEY_RIGHTMETA,
}
# With these held a key is a shortcut, which doesn't drop the selection.
_SHORTCUT_MODIFIERS = _MODIFIERS - {e.KEY_LEFTSHIFT, e.KEY_RIGHTSHIFT}
_BUTTONS = {e.BTN_LEFT, e.BTN_RIGHT, e.BTN_MIDDLE}
_TAP_MAX = 0.3          # seconds; a shorter touch on a touchpad is a tap (= click)
_RESCAN_EVERY = 5.0     # seconds; pick up keyboards and mice plugged in later


class SelectionTracker:
    def __init__(self):
        self.last_change = 0.0      # primary selection changed
        self.last_deselect = 0.0    # user input that drops a selection
        self._devices = {}          # path -> InputDevice
        self._held = set()
        self._touch_start = {}
        threading.Thread(target=self._watch_selection, daemon=True).start()
        threading.Thread(target=self._watch_input, daemon=True).start()

    def has_fresh_selection(self):
        return self.last_change > self.last_deselect

    def wait_for_change(self, since, timeout=0.5):
        """Wait until the primary selection changes after `since`."""
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if self.last_change > since:
                return True
            time.sleep(0.01)
        return False

    # ------------------------------------------------------------ selection

    def _watch_selection(self):
        while True:
            try:
                proc = subprocess.Popen(["wl-paste", "--primary", "--watch", "echo"],
                                        stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
            except OSError:
                return
            proc.stdout.readline()  # the current selection is reported once at start
            for _line in proc.stdout:
                self.last_change = time.monotonic()
            proc.wait()
            time.sleep(1)

    # ------------------------------------------------------------ input

    def _rescan(self):
        for path in evdev.list_devices():
            if path in self._devices:
                continue
            try:
                dev = evdev.InputDevice(path)
            except OSError:
                continue
            keys = set(dev.capabilities().get(e.EV_KEY, []))
            if "keyboard-switch" in dev.name or not keys & ({e.KEY_A, e.BTN_TOUCH} | _BUTTONS):
                dev.close()
                continue
            self._devices[path] = dev

    def _watch_input(self):
        next_scan = 0.0
        while True:
            now = time.monotonic()
            if now >= next_scan:
                self._rescan()
                next_scan = now + _RESCAN_EVERY
            devices = list(self._devices.values())
            try:
                ready, _, _ = select.select(devices, [], [], 1.0)
            except (OSError, ValueError):
                ready = devices
            for dev in ready:
                try:
                    for ev in dev.read():
                        if ev.type == e.EV_KEY:
                            self._on_key(dev.path, ev.code, ev.value)
                except BlockingIOError:
                    pass
                except OSError:  # unplugged
                    self._devices.pop(dev.path, None)
                    dev.close()

    def _on_key(self, path, code, value):
        now = time.monotonic()
        if code == e.BTN_TOUCH:
            if value == 1:
                self._touch_start[path] = now
            elif value == 0 and now - self._touch_start.pop(path, now) < _TAP_MAX:
                self.last_deselect = now
            return
        if code in _MODIFIERS:
            if value == 1:
                self._held.add(code)
            elif value == 0:
                self._held.discard(code)
            return
        is_key = not e.BTN_MISC <= code < e.KEY_OK
        if value == 1 and (is_key or code in _BUTTONS) and not self._held & _SHORTCUT_MODIFIERS:
            self.last_deselect = now
