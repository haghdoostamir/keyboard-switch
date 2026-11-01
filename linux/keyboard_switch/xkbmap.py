"""Build character <-> physical key tables for XKB layouts using libxkbcommon.

Every layout is described by two tables:
  fwd: (keycode, combo) -> character produced by that key in that layout
  rev: character -> (keycode, combo) needed to type it

`combo` is a bit mask of the modifier keys held: SHIFT and/or ALTGR.
Keycodes are XKB keycodes (evdev code + 8).
"""

import ctypes
import ctypes.util
from dataclasses import dataclass, field

SHIFT = 1
ALTGR = 2
COMBOS = (0, SHIFT, ALTGR, SHIFT | ALTGR)

EVDEV_OFFSET = 8
KC_LEFTSHIFT = 42 + EVDEV_OFFSET
KC_RIGHTALT = 100 + EVDEV_OFFSET

_XKB_KEY_DOWN = 1


class _RuleNames(ctypes.Structure):
    _fields_ = [(name, ctypes.c_char_p) for name in ("rules", "model", "layout", "variant", "options")]


def _load():
    lib = ctypes.CDLL(ctypes.util.find_library("xkbcommon") or "libxkbcommon.so.0")
    p, u32 = ctypes.c_void_p, ctypes.c_uint32
    sigs = {
        "xkb_context_new": (p, [ctypes.c_int]),
        "xkb_context_unref": (None, [p]),
        "xkb_keymap_new_from_names": (p, [p, ctypes.POINTER(_RuleNames), ctypes.c_int]),
        "xkb_keymap_unref": (None, [p]),
        "xkb_keymap_num_layouts": (u32, [p]),
        "xkb_keymap_layout_get_name": (ctypes.c_char_p, [p, u32]),
        "xkb_state_new": (p, [p]),
        "xkb_state_unref": (None, [p]),
        "xkb_state_update_mask": (ctypes.c_int, [p, u32, u32, u32, u32, u32, u32]),
        "xkb_state_update_key": (ctypes.c_int, [p, u32, ctypes.c_int]),
        "xkb_state_key_get_utf32": (u32, [p, u32]),
        "xkb_state_key_get_level": (u32, [p, u32, u32]),
    }
    for name, (res, args) in sigs.items():
        fn = getattr(lib, name)
        fn.restype, fn.argtypes = res, args
    return lib


_lib = None


@dataclass
class LayoutMap:
    name: str
    variant: str
    description: str = ""
    fwd: dict = field(default_factory=dict)
    rev: dict = field(default_factory=dict)


def _is_printable(cp):
    return cp >= 0x20 and cp != 0x7F and not (0x80 <= cp < 0xA0)


def build_maps(layouts, variants=None, model="pc105", rules="evdev"):
    """Return a list of LayoutMap, one per entry of `layouts` (same order)."""
    global _lib
    if _lib is None:
        _lib = _load()
    variants = list(variants or [])
    variants += [""] * (len(layouts) - len(variants))

    names = _RuleNames(rules.encode(), model.encode(), ",".join(layouts).encode(),
                       ",".join(variants).encode(), b"")
    ctx = _lib.xkb_context_new(0)
    if not ctx:
        raise RuntimeError("xkb_context_new failed")
    keymap = _lib.xkb_keymap_new_from_names(ctx, ctypes.byref(names), 0)
    if not keymap:
        _lib.xkb_context_unref(ctx)
        raise RuntimeError(f"could not compile XKB keymap for layouts {layouts} {variants}")
    try:
        maps = []
        for idx, (name, variant) in enumerate(zip(layouts, variants)):
            desc = _lib.xkb_keymap_layout_get_name(keymap, idx)
            lm = LayoutMap(name, variant, desc.decode() if desc else "")
            base_levels = {}
            for combo in COMBOS:
                state = _lib.xkb_state_new(keymap)
                _lib.xkb_state_update_mask(state, 0, 0, 0, 0, 0, idx)
                if combo & SHIFT:
                    _lib.xkb_state_update_key(state, KC_LEFTSHIFT, _XKB_KEY_DOWN)
                if combo & ALTGR:
                    _lib.xkb_state_update_key(state, KC_RIGHTALT, _XKB_KEY_DOWN)
                for kc in range(9, 256):
                    level = _lib.xkb_state_key_get_level(state, kc, idx)
                    if combo & ALTGR:
                        # AltGr is not a level switch for this key/layout (e.g. plain Alt in "us").
                        if base_levels.get((kc, combo & SHIFT)) == level:
                            continue
                    else:
                        base_levels[(kc, combo)] = level
                    cp = _lib.xkb_state_key_get_utf32(state, kc)
                    if not _is_printable(cp):
                        continue
                    ch = chr(cp)
                    lm.fwd[(kc, combo)] = ch
                    lm.rev.setdefault(ch, (kc, combo))
                _lib.xkb_state_unref(state)
            maps.append(lm)
        return maps
    finally:
        _lib.xkb_keymap_unref(keymap)
        _lib.xkb_context_unref(ctx)
