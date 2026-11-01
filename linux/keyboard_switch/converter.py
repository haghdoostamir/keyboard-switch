"""Convert text typed with the wrong keyboard layout into another layout."""

import unicodedata

# Characters that never get their own cursor position in a text editor.
_ZERO_WIDTH = {"‌", "‍", "︎", "️"}


def convert(text, src, dst):
    """Re-interpret `text` as keys pressed in layout `src` and return what `dst` would produce.

    Characters with no equivalent are kept unchanged.
    """
    out = []
    for ch in text:
        key = src.rev.get(ch)
        out.append(dst.fwd.get(key, ch) if key else ch)
    return "".join(out)


def coverage(text, layout):
    """How many meaningful (non-space) characters of `text` the layout can type."""
    return sum(1 for ch in text if not ch.isspace() and ch in layout.rev)


def detect_source(text, layouts, current):
    """Index of the layout `text` was most likely typed with.

    The active layout wins unless another layout explains strictly more of the text.
    """
    scores = [coverage(text, lm) for lm in layouts]
    best = max(scores, default=0)
    if 0 <= current < len(layouts) and scores[current] == best:
        return current
    return scores.index(best)


def next_layout(src, enabled):
    """The layout after `src` in the cycle of enabled layout indices."""
    if not enabled:
        return src
    if src not in enabled:
        return enabled[0]
    return enabled[(enabled.index(src) + 1) % len(enabled)]


def cursor_length(text):
    """Approximate number of cursor positions (grapheme clusters) in `text`."""
    return sum(1 for ch in text if ch not in _ZERO_WIDTH and not unicodedata.combining(ch))
