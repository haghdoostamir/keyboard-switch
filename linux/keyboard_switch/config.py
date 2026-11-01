import json
import os

CONFIG_DIR = os.path.join(os.environ.get("XDG_CONFIG_HOME", os.path.expanduser("~/.config")),
                          "keyboard-switch")
CONFIG_FILE = os.path.join(CONFIG_DIR, "config.json")

DEFAULTS = {
    # Layout ids ("layout" or "layout(variant)") left out of the cycle; new layouts are on.
    "disabled_layouts": [],
    # "type": retype the keys with the new layout, "paste": go through the clipboard.
    "method": "type",
    # Select the converted text again so pressing the shortcut twice restores the original.
    "reselect": True,
    # With nothing selected, select the whole text field (Ctrl+A) and convert that.
    "select_all": True,
    # Leave the system on the layout the text was converted to.
    "switch_layout": True,
}


def load():
    cfg = dict(DEFAULTS)
    try:
        with open(CONFIG_FILE, encoding="utf-8") as f:
            cfg.update(json.load(f))
    except (OSError, ValueError):
        pass
    return cfg


def save(cfg):
    os.makedirs(CONFIG_DIR, exist_ok=True)
    tmp = CONFIG_FILE + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(cfg, f, indent=2, ensure_ascii=False)
    os.replace(tmp, CONFIG_FILE)


def layout_id(layout, variant):
    return f"{layout}({variant})" if variant else layout
