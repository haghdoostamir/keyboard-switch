"""KDE Plasma integration over DBus: keyboard layouts and global shortcuts."""

import configparser
import os

import gi

gi.require_version("Gio", "2.0")
from gi.repository import Gio, GLib  # noqa: E402

KXKBRC = os.path.expanduser("~/.config/kxkbrc")

# Values of KGlobalAccel::SetShortcutFlag as sent over DBus.
_SET_PRESENT = 2
_NO_AUTOLOADING = 4
_IS_DEFAULT = 8


_connection = None


def _bus():
    # GLib only keeps a weak reference to the shared bus; if nobody holds it, it gets closed
    # and the signal subscriptions disappear with it.
    global _connection
    if _connection is None or _connection.is_closed():
        _connection = Gio.bus_get_sync(Gio.BusType.SESSION, None)
    return _connection


def _call(dest, path, iface, method, params=None, reply=None):
    res = _bus().call_sync(dest, path, iface, method, params,
                           GLib.VariantType(reply) if reply else None,
                           Gio.DBusCallFlags.NONE, 3000, None)
    return res.unpack() if res is not None else None


# ---------------------------------------------------------------- layouts

def read_layouts():
    """[(layout, variant, display name)] as configured in KDE, in switching order."""
    names = []
    try:
        (names,) = _call("org.kde.keyboard", "/Layouts", "org.kde.KeyboardLayouts",
                         "getLayoutsList", reply="(a(sss))")
    except GLib.Error:
        pass

    cfg = configparser.ConfigParser(interpolation=None)
    cfg.optionxform = str
    cfg.read(KXKBRC)
    sec = cfg["Layout"] if cfg.has_section("Layout") else {}
    layouts = [x for x in sec.get("LayoutList", "").split(",") if x]
    variants = sec.get("VariantList", "").split(",")
    variants += [""] * (len(layouts) - len(variants))

    if not layouts:
        layouts = [short for short, _, _ in names] or ["us"]
        variants = [""] * len(layouts)
    result = []
    for i, (lay, var) in enumerate(zip(layouts, variants)):
        title = names[i][2] if i < len(names) and names[i][0] == lay else lay
        result.append((lay, var, title))
    return result


def keyboard_model():
    cfg = configparser.ConfigParser(interpolation=None)
    cfg.read(KXKBRC)
    return cfg.get("Layout", "Model", fallback="pc105") or "pc105"


def current_layout():
    try:
        return _call("org.kde.keyboard", "/Layouts", "org.kde.KeyboardLayouts",
                     "getLayout", reply="(u)")[0]
    except GLib.Error:
        return 0


def set_layout(index):
    try:
        return _call("org.kde.keyboard", "/Layouts", "org.kde.KeyboardLayouts",
                     "setLayout", GLib.Variant("(u)", (index,)), reply="(b)")[0]
    except GLib.Error:
        return False


def watch_layouts(callback):
    """Call `callback()` whenever the layout list or the active layout changes."""
    return _bus().signal_subscribe("org.kde.keyboard", "org.kde.KeyboardLayouts", None, "/Layouts",
                                   None, Gio.DBusSignalFlags.NONE,
                                   lambda *args: callback())


# ---------------------------------------------------------------- shortcuts

class GlobalShortcut:
    """One action registered with kglobalaccel; KDE stores and grabs the key for us."""

    def __init__(self, component, action, component_title, action_title, default_key):
        self.action_id = [component, action, component_title, action_title]
        self._callback = None
        self._sub = None
        self._call("doRegister", "(as)", (self.action_id,))
        self._set([default_key] if default_key else [], _SET_PRESENT | _IS_DEFAULT)
        # Autoloading: KDE answers with the key the user configured, if any.
        self._set([default_key] if default_key else [], _SET_PRESENT)

    def _call(self, method, sig, args, reply=None):
        return _call("org.kde.kglobalaccel", "/kglobalaccel", "org.kde.KGlobalAccel",
                     method, GLib.Variant(sig, args), reply)

    def _set(self, keys, flags):
        return self._call("setShortcut", "(asaiu)", (self.action_id, keys, flags), reply="(ai)")[0]

    def key(self):
        """Current shortcut as a Qt key combination int (0 when unset)."""
        keys = self._call("shortcut", "(as)", (self.action_id,), reply="(ai)")[0]
        return keys[0] if keys else 0

    def set_key(self, key):
        got = self._set([key] if key else [], _SET_PRESENT | _NO_AUTOLOADING)
        return got[0] if got else 0

    def conflicts(self, key):
        """Titles of other actions that already use `key`."""
        try:
            (found,) = _call("org.kde.kglobalaccel", "/kglobalaccel", "org.kde.KGlobalAccel",
                             "getGlobalShortcutsByKey", GLib.Variant("(i)", (key,)),
                             reply="(a(ssssssaiai))")
        except GLib.Error:
            return []
        # KGlobalShortcutInfo: action id/title, component id/title, context id/title, keys, defaults
        return [f"{f[3] or f[2]}: {f[1] or f[0]}" for f in found
                if (f[2], f[0]) != (self.action_id[0], self.action_id[1])]

    def connect(self, callback):
        self._callback = callback
        path = "/component/" + self._call("getComponent", "(s)", (self.action_id[0],),
                                          reply="(o)")[0].rsplit("/", 1)[-1]
        self._sub = _bus().signal_subscribe(
            "org.kde.kglobalaccel", "org.kde.kglobalaccel.Component", "globalShortcutPressed",
            path, None, Gio.DBusSignalFlags.NONE, self._on_signal)

    def _on_signal(self, _conn, _sender, _path, _iface, _signal, params):
        component, action, _ts = params.unpack()
        if component == self.action_id[0] and action == self.action_id[1] and self._callback:
            self._callback()

    def deactivate(self):
        try:
            self._call("setInactive", "(as)", (self.action_id,))
        except GLib.Error:
            pass
