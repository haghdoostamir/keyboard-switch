import unittest
from unittest import mock

from keyboard_switch import config, converter, engine, injector
from keyboard_switch.xkbmap import build_maps

US, IR = build_maps(["us", "ir"], ["", "winkeys"])


class ConvertTest(unittest.TestCase):
    def test_round_trip(self):
        for text in ["sghl o,fd?", "hdk d; jsj hsj.", "Hello World 123"]:
            fa = converter.convert(text, US, IR)
            self.assertNotEqual(fa, text)
            self.assertEqual(converter.convert(fa, IR, US), text)

    def test_known_words(self):
        self.assertEqual(converter.convert("sghl o,fd?", US, IR), "سلام خوبی؟")
        self.assertEqual(converter.convert("سلام", IR, US), "sghl")

    def test_unknown_characters_are_kept(self):
        self.assertEqual(converter.convert("sghl 🙂\n", US, IR), "سلام 🙂\n")

    def test_detect_source(self):
        self.assertEqual(converter.detect_source("سلام", [US, IR], 0), 1)
        self.assertEqual(converter.detect_source("sghl", [US, IR], 1), 0)
        # Ambiguous text (only spaces / digits) keeps the active layout.
        self.assertEqual(converter.detect_source("   ", [US, IR], 1), 1)

    def test_cycle(self):
        self.assertEqual(converter.next_layout(0, [0, 1]), 1)
        self.assertEqual(converter.next_layout(1, [0, 1]), 0)
        self.assertEqual(converter.next_layout(2, [0, 1, 2]), 0)

    def test_cursor_length_ignores_zwnj_and_marks(self):
        self.assertEqual(converter.cursor_length("می‌خواهم"), 7)
        self.assertEqual(converter.cursor_length("بَد"), 2)

    def test_keys_for_text(self):
        keys = injector.keys_for_text("سلام\n", IR)
        self.assertEqual(len(keys), 5)
        self.assertIsNone(injector.keys_for_text("🙂", IR))


class FakeKeyboard:
    def __init__(self):
        self.log = []

    def wait_for_modifiers_release(self):
        self.log.append("wait")

    def type_keys(self, keys):
        self.log.append(("type", len(keys)))

    def paste(self):
        self.log.append("paste")

    def select_all(self):
        self.log.append("select_all")
        if self.tracker.app_selects_all:
            self.tracker.last_change = self.tracker.last_deselect + 1

    def select_left(self, n):
        self.log.append(("select", n))


class FakeTracker:
    def __init__(self, fresh, app_selects_all=True):
        self.last_deselect = 10.0
        self.last_change = 11.0 if fresh else 9.0
        self.app_selects_all = app_selects_all

    def has_fresh_selection(self):
        return self.last_change > self.last_deselect

    def wait_for_change(self, since, timeout=0.5):
        return self.last_change > self.last_deselect


class EngineTest(unittest.TestCase):
    def make(self, selection, active, fresh=True, app_selects_all=True, **cfg):
        layouts = [("us", "", "English (US)"), ("ir", "winkeys", "Persian (Windows)")]
        conf = dict(config.DEFAULTS)
        conf.update(cfg)
        self.state = {"layout": active, "clipboard": "old"}
        patches = [
            mock.patch.object(engine.kde, "read_layouts", return_value=layouts),
            mock.patch.object(engine.kde, "keyboard_model", return_value="pc105"),
            mock.patch.object(engine.kde, "current_layout", side_effect=lambda: self.state["layout"]),
            mock.patch.object(engine.kde, "set_layout", side_effect=lambda i: self.state.update(layout=i)),
            mock.patch.object(engine.injector, "check_environment", return_value=[]),
            mock.patch.object(engine.injector, "get_selection", return_value=selection),
            mock.patch.object(engine.injector, "get_clipboard", side_effect=lambda: self.state["clipboard"]),
            mock.patch.object(engine.injector, "set_clipboard", side_effect=lambda t: self.state.update(clipboard=t)),
            mock.patch.object(engine.time, "sleep"),
        ]
        for p in patches:
            p.start()
            self.addCleanup(p.stop)
        eng = engine.Engine(conf, notify=lambda m: self.state.setdefault("msgs", []).append(m))
        eng.keyboard = FakeKeyboard()
        eng.tracker = eng.keyboard.tracker = FakeTracker(fresh, app_selects_all)
        return eng

    def test_type_and_switch(self):
        eng = self.make("sghl o,fd?", active=0)
        eng._convert_selection()
        self.assertEqual(self.state["layout"], 1)
        self.assertEqual(eng.keyboard.log, ["wait", ("type", 10), ("select", 10)])

    def test_back_from_persian(self):
        eng = self.make("سلام", active=1)
        eng._convert_selection()
        self.assertEqual(self.state["layout"], 0)

    def test_untypeable_falls_back_to_paste(self):
        eng = self.make("sghl 🙂", active=0)
        with mock.patch.object(engine.threading, "Timer"):
            eng._convert_selection()
        self.assertEqual(self.state["clipboard"], "سلام 🙂")
        self.assertIn("paste", eng.keyboard.log)

    def test_keep_layout(self):
        eng = self.make("sghl", active=0, switch_layout=False)
        eng._convert_selection()
        self.assertEqual(self.state["layout"], 0)

    def test_empty_selection(self):
        eng = self.make("", active=0)
        eng._convert_selection()
        self.assertEqual(eng.keyboard.log, ["wait"])
        self.assertTrue(self.state["msgs"])

    def test_no_selection_selects_whole_field(self):
        eng = self.make("sghl", active=0, fresh=False)
        eng._convert_selection()
        self.assertEqual(eng.keyboard.log, ["wait", "select_all", ("type", 4), ("select", 4)])

    def test_no_selection_and_select_all_unsupported(self):
        eng = self.make("stale text", active=0, fresh=False, app_selects_all=False)
        eng._convert_selection()
        self.assertEqual(eng.keyboard.log, ["wait", "select_all"])
        self.assertEqual(self.state["layout"], 0)

    def test_stale_selection_ignored_when_select_all_off(self):
        eng = self.make("stale text", active=0, fresh=False, select_all=False)
        eng._convert_selection()
        self.assertEqual(eng.keyboard.log, ["wait"])
        self.assertTrue(self.state["msgs"])


if __name__ == "__main__":
    unittest.main()
