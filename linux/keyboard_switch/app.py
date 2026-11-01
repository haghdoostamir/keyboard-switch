"""Tray application and settings window."""

import os
import subprocess
import sys

from gi.repository import GLib
from PyQt6.QtCore import QAbstractEventDispatcher, QKeyCombination, Qt, QTimer, pyqtSignal
from PyQt6.QtGui import QAction, QIcon, QKeySequence
from PyQt6.QtNetwork import QLocalServer, QLocalSocket
from PyQt6.QtWidgets import (
    QApplication, QCheckBox, QGroupBox, QHBoxLayout, QKeySequenceEdit, QLabel, QLineEdit,
    QListWidget, QListWidgetItem, QMainWindow, QMenu, QPushButton, QRadioButton,
    QSystemTrayIcon, QVBoxLayout, QWidget,
)

from . import config, injector, kde
from .engine import Engine

APP_ID = "keyboard-switch"
SOCKET_NAME = "keyboard-switch-" + str(os.getuid())
DEFAULT_KEY = QKeySequence("Meta+Shift+Space")[0].toCombined()
AUTOSTART_FILE = os.path.expanduser("~/.config/autostart/keyboard-switch.desktop")
LAUNCHER = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                        "keyboard-switch")


def _icon():
    return QIcon.fromTheme("input-keyboard", QIcon.fromTheme("preferences-desktop-keyboard"))


class Window(QMainWindow):
    message = pyqtSignal(str)   # emitted from the worker thread

    def __init__(self, engine, shortcut):
        super().__init__()
        self.engine = engine
        self.shortcut = shortcut
        self.setWindowTitle("Keyboard Switch")
        self.setWindowIcon(_icon())
        self.resize(520, 640)

        root = QWidget()
        lay = QVBoxLayout(root)
        self.setCentralWidget(root)

        self.status = QLabel()
        self.status.setWordWrap(True)
        self.status.setTextInteractionFlags(Qt.TextInteractionFlag.TextSelectableByMouse)
        lay.addWidget(self.status)

        # Layouts
        box = QGroupBox("Keyboard layouts (detected from KDE)")
        bl = QVBoxLayout(box)
        self.list = QListWidget()
        self.list.itemChanged.connect(self._layout_toggled)
        bl.addWidget(self.list)
        hint = QLabel("Checked layouts take part in the cycle. Add or remove layouts in "
                      "KDE System Settings, they show up here automatically.")
        hint.setWordWrap(True)
        bl.addWidget(hint)
        row = QHBoxLayout()
        refresh = QPushButton("Refresh")
        refresh.clicked.connect(self.refresh)
        kcm = QPushButton("Open KDE keyboard settings")
        kcm.clicked.connect(lambda: subprocess.Popen(["systemsettings", "kcm_keyboard"]))
        row.addWidget(refresh)
        row.addWidget(kcm)
        row.addStretch()
        bl.addLayout(row)
        lay.addWidget(box)

        # Shortcut
        box = QGroupBox("Shortcut")
        bl = QVBoxLayout(box)
        row = QHBoxLayout()
        self.key_edit = QKeySequenceEdit()
        self.key_edit.setMaximumSequenceLength(1)
        apply_key = QPushButton("Apply")
        apply_key.clicked.connect(self._apply_key)
        reset_key = QPushButton("Default")
        reset_key.clicked.connect(lambda: self.key_edit.setKeySequence(QKeySequence(
            QKeyCombination.fromCombined(DEFAULT_KEY))))
        row.addWidget(self.key_edit, 1)
        row.addWidget(apply_key)
        row.addWidget(reset_key)
        bl.addLayout(row)
        self.key_info = QLabel()
        self.key_info.setWordWrap(True)
        bl.addWidget(self.key_info)
        lay.addWidget(box)

        # Behaviour
        cfg = engine.cfg
        box = QGroupBox("Behaviour")
        bl = QVBoxLayout(box)
        self.m_type = QRadioButton("Retype the text (works in terminals too)")
        self.m_paste = QRadioButton("Paste through the clipboard (Ctrl+V)")
        (self.m_type if cfg["method"] == "type" else self.m_paste).setChecked(True)
        self.m_type.toggled.connect(lambda on: self._set("method", "type" if on else "paste"))
        self.reselect = QCheckBox("Keep the converted text selected (press again to undo / cycle)")
        self.reselect.setChecked(cfg["reselect"])
        self.reselect.toggled.connect(lambda on: self._set("reselect", on))
        self.select_all = QCheckBox("With nothing selected, convert the whole text field")
        self.select_all.setChecked(cfg["select_all"])
        self.select_all.toggled.connect(lambda on: self._set("select_all", on))
        self.switch = QCheckBox("Switch the system layout to the new language")
        self.switch.setChecked(cfg["switch_layout"])
        self.switch.toggled.connect(lambda on: self._set("switch_layout", on))
        self.autostart = QCheckBox("Start automatically on login")
        self.autostart.setChecked(os.path.exists(AUTOSTART_FILE))
        self.autostart.toggled.connect(set_autostart)
        for w in (self.m_type, self.m_paste, self.reselect, self.select_all, self.switch,
                  self.autostart):
            bl.addWidget(w)
        lay.addWidget(box)

        # Try it
        box = QGroupBox("Try it")
        bl = QVBoxLayout(box)
        self.test_in = QLineEdit()
        self.test_in.setPlaceholderText("Type something with the wrong layout, e.g. sghl")
        self.test_in.textChanged.connect(self._preview)
        self.test_out = QLabel()
        self.test_out.setTextInteractionFlags(Qt.TextInteractionFlag.TextSelectableByMouse)
        bl.addWidget(self.test_in)
        bl.addWidget(self.test_out)
        lay.addWidget(box)
        lay.addStretch()

        self.message.connect(self._show_message)
        self.tray = None
        self.refresh()

    # ------------------------------------------------------------ helpers

    def _set(self, key, value):
        self.engine.cfg[key] = value
        config.save(self.engine.cfg)

    def refresh(self):
        try:
            self.engine.reload_layouts()
        except Exception as exc:
            self.message.emit(f"Could not read keyboard layouts: {exc}")
        current = kde.current_layout()
        off = set(self.engine.cfg["disabled_layouts"])
        self.list.blockSignals(True)
        self.list.clear()
        for i, (lay, var, title) in enumerate(self.engine.layouts):
            lid = config.layout_id(lay, var)
            text = f"{title}   [{lid}]" + ("   ● active" if i == current else "")
            item = QListWidgetItem(text)
            item.setData(Qt.ItemDataRole.UserRole, lid)
            item.setFlags(item.flags() | Qt.ItemFlag.ItemIsUserCheckable)
            item.setCheckState(Qt.CheckState.Unchecked if lid in off else Qt.CheckState.Checked)
            self.list.addItem(item)
        self.list.blockSignals(False)

        key = self.shortcut.key() if self.shortcut else 0
        self.key_edit.setKeySequence(QKeySequence(QKeyCombination.fromCombined(key)) if key
                                     else QKeySequence())
        self._update_status()
        self._preview()

    def _update_status(self):
        problems = injector.check_environment()
        if self.shortcut is None:
            problems.append("could not register the global shortcut with KDE")
        if problems:
            self.status.setText("<b style='color:#c0392b'>Setup needed:</b><br>• "
                                + "<br>• ".join(problems))
        else:
            seq = self.key_edit.keySequence().toString(QKeySequence.SequenceFormat.NativeText)
            self.status.setText(f"<b style='color:#27ae60'>Ready.</b> Select text anywhere and "
                                f"press <b>{seq or '(no shortcut)'}</b>.")

    def _layout_toggled(self, item):
        off = set(self.engine.cfg["disabled_layouts"])
        lid = item.data(Qt.ItemDataRole.UserRole)
        if item.checkState() == Qt.CheckState.Checked:
            off.discard(lid)
        else:
            off.add(lid)
        self._set("disabled_layouts", sorted(off))
        self._preview()

    def _apply_key(self):
        seq = self.key_edit.keySequence()
        key = seq[0].toCombined() if not seq.isEmpty() else 0
        if self.shortcut is None:
            return
        clashes = self.shortcut.conflicts(key) if key else []
        if clashes:
            self.key_info.setText("Already used by " + ", ".join(clashes)
                                  + ". Pick another combination.")
            return
        got = self.shortcut.set_key(key)
        self.key_info.setText("Saved. You can also change it in System Settings → Shortcuts."
                              if got == key else "KDE refused this combination.")
        self.refresh()

    def _preview(self):
        text = self.test_in.text()
        if not text or len(self.engine.enabled()) < 2:
            self.test_out.setText("")
            return
        src, dst, result = self.engine.plan(text, kde.current_layout())
        name = lambda i: self.engine.layouts[i][2]
        self.test_out.setText(f"{name(src)} → {name(dst)}:  <b>{result}</b>")

    def _show_message(self, text):
        if self.tray and self.tray.isVisible():
            self.tray.showMessage("Keyboard Switch", text, _icon(), 3000)
        else:
            self.statusBar().showMessage(text, 5000)

    def closeEvent(self, event):
        if self.tray and self.tray.isVisible():
            event.ignore()
            self.hide()
        else:
            event.accept()


def set_autostart(on):
    if on:
        os.makedirs(os.path.dirname(AUTOSTART_FILE), exist_ok=True)
        with open(AUTOSTART_FILE, "w") as f:
            f.write("[Desktop Entry]\nType=Application\nName=Keyboard Switch\n"
                    f"Exec={LAUNCHER} --hidden\nIcon=input-keyboard\n"
                    "X-KDE-autostart-phase=2\n")
    elif os.path.exists(AUTOSTART_FILE):
        os.remove(AUTOSTART_FILE)


def _pump_glib_if_needed(app):
    """Gio delivers DBus signals through the GLib main loop; make sure it runs."""
    dispatcher = QAbstractEventDispatcher.instance()
    if dispatcher and "Glib" in dispatcher.metaObject().className():
        return None
    ctx = GLib.MainContext.default()
    timer = QTimer(app)
    timer.timeout.connect(lambda: ctx.iteration(False))
    timer.start(20)
    return timer


def _send_to_running(command):
    sock = QLocalSocket()
    sock.connectToServer(SOCKET_NAME)
    if not sock.waitForConnected(300):
        return False
    sock.write(command.encode())
    sock.waitForBytesWritten(300)
    sock.disconnectFromServer()
    return True


def main(argv):
    hidden = "--hidden" in argv
    app = QApplication(argv)
    app.setApplicationName(APP_ID)
    app.setDesktopFileName(APP_ID)
    app.setQuitOnLastWindowClosed(False)

    command = "convert" if "--convert" in argv else "show"
    if _send_to_running(command if not hidden else "noop"):
        return 0

    server = QLocalServer()
    QLocalServer.removeServer(SOCKET_NAME)
    server.listen(SOCKET_NAME)

    _pump_glib_if_needed(app)

    engine = Engine(config.load())
    try:
        shortcut = kde.GlobalShortcut(APP_ID, "convert-selection", "Keyboard Switch",
                                      "Convert selected text to the next layout", DEFAULT_KEY)
        shortcut.connect(engine.trigger)
    except GLib.Error as exc:
        print("kglobalaccel:", exc.message, file=sys.stderr)
        shortcut = None

    win = Window(engine, shortcut)
    engine.notify = win.message.emit
    kde.watch_layouts(lambda: QTimer.singleShot(0, win.refresh))

    if QSystemTrayIcon.isSystemTrayAvailable():
        tray = QSystemTrayIcon(_icon(), app)
        tray.setToolTip("Keyboard Switch")
        menu = QMenu()
        for title, slot in (("Settings", win.showNormal), ("Convert selection", engine.trigger),
                            ("Quit", app.quit)):
            act = QAction(title, menu)
            act.triggered.connect(slot)
            menu.addAction(act)
        tray.setContextMenu(menu)
        tray.activated.connect(lambda reason: win.setVisible(not win.isVisible())
                               if reason == QSystemTrayIcon.ActivationReason.Trigger else None)
        tray.show()
        win.tray = tray

    def on_connection():
        conn = server.nextPendingConnection()
        conn.waitForReadyRead(300)
        cmd = bytes(conn.readAll()).decode()
        if cmd == "convert":
            engine.trigger()
        elif cmd == "show":
            win.showNormal()
            win.raise_()
            win.activateWindow()

    server.newConnection.connect(on_connection)

    if command == "convert":
        engine.trigger()
    if not hidden or win.tray is None:
        win.show()

    try:
        engine.ensure_keyboard()
    except OSError as exc:
        win.message.emit(f"Virtual keyboard unavailable: {exc}")

    code = app.exec()
    if engine.keyboard:
        engine.keyboard.close()
    return code
