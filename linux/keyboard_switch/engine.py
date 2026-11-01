"""The convert-the-selection flow, independent of the GUI."""

import threading
import time

from . import config, converter, injector, kde
from .selection import SelectionTracker
from .xkbmap import build_maps


class Engine:
    def __init__(self, cfg, notify=print):
        self.cfg = cfg
        self.notify = notify
        self.layouts = []   # [(layout, variant, title)]
        self.maps = []
        self.keyboard = None
        self.tracker = None
        self._lock = threading.Lock()
        self.reload_layouts()

    def reload_layouts(self):
        self.layouts = kde.read_layouts()
        self.maps = build_maps([l for l, _, _ in self.layouts], [v for _, v, _ in self.layouts],
                               model=kde.keyboard_model())

    def enabled(self):
        off = set(self.cfg["disabled_layouts"])
        return [i for i, (l, v, _) in enumerate(self.layouts) if config.layout_id(l, v) not in off]

    def ensure_keyboard(self):
        if self.keyboard is None and not injector.check_environment():
            self.keyboard = injector.Keyboard()
            self.tracker = SelectionTracker()
        return self.keyboard

    def plan(self, text, current):
        """(source index, target index, converted text) for `text`."""
        enabled = self.enabled()
        candidates = [self.maps[i] for i in enabled]
        cur = enabled.index(current) if current in enabled else -1
        src = enabled[converter.detect_source(text, candidates, cur)]
        dst = converter.next_layout(src, enabled)
        return src, dst, converter.convert(text, self.maps[src], self.maps[dst])

    # ------------------------------------------------------------ shortcut action

    def trigger(self):
        """Convert the current selection; runs in a background thread."""
        threading.Thread(target=self._run, daemon=True).start()

    def _run(self):
        if not self._lock.acquire(blocking=False):
            return
        try:
            self._convert_selection()
        except Exception as exc:  # never kill the tray app because of one failed conversion
            self.notify(f"Conversion failed: {exc}")
        finally:
            self._lock.release()

    def _convert_selection(self):
        problems = injector.check_environment()
        if problems:
            self.notify("Not set up yet: " + "; ".join(problems))
            return
        if len(self.enabled()) < 2:
            self.notify("Enable at least two keyboard layouts.")
            return
        kb = self.ensure_keyboard()
        kb.wait_for_modifiers_release()

        if not self.tracker.has_fresh_selection():
            if not self.cfg["select_all"]:
                self.notify("Select some text first.")
                return
            started = time.monotonic()
            kb.select_all()
            if not self.tracker.wait_for_change(started):
                self.notify("Nothing is selected and this place doesn't support select all.")
                return
        text = injector.get_selection()
        if not text.strip():
            self.notify("Select some text first.")
            return

        current = kde.current_layout()
        src, dst, result = self.plan(text, current)

        keys = injector.keys_for_text(result, self.maps[dst]) if self.cfg["method"] == "type" else None
        if keys is not None:
            # The virtual keyboard follows the active layout, so switch before typing.
            kde.set_layout(dst)
            time.sleep(0.05)
            kb.type_keys(keys)
        else:
            old = injector.get_clipboard()
            injector.set_clipboard(result)
            time.sleep(0.1)
            kb.paste()
            if self.cfg["switch_layout"]:
                kde.set_layout(dst)
            if old is not None:
                threading.Timer(0.5, injector.set_clipboard, (old,)).start()

        if self.cfg["reselect"]:
            time.sleep(0.05)
            kb.select_left(converter.cursor_length(result))
        if not self.cfg["switch_layout"]:
            kde.set_layout(current)
