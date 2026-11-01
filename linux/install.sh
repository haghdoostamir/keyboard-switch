#!/bin/bash
# One-time setup for Arch Linux + KDE Plasma (Wayland).
set -euo pipefail

dir="$(dirname "$(readlink -f "$0")")"

echo "==> Installing dependencies"
sudo pacman -S --needed python-pyqt6 python-gobject python-evdev wl-clipboard libxkbcommon

echo "==> Allowing the 'input' group to create a virtual keyboard (/dev/uinput)"
echo 'KERNEL=="uinput", GROUP="input", MODE="0660", OPTIONS+="static_node=uinput"' |
    sudo tee /etc/udev/rules.d/80-keyboard-switch-uinput.rules >/dev/null
echo uinput | sudo tee /etc/modules-load.d/keyboard-switch-uinput.conf >/dev/null
sudo modprobe uinput
sudo udevadm control --reload-rules
sudo udevadm trigger --name-match=uinput

relogin=0
if [[ " $(id -nG "$USER") " != *" input "* ]]; then
    echo "==> Adding $USER to the 'input' group"
    sudo gpasswd -a "$USER" input
    relogin=1
fi

echo "==> Adding the app to the application menu"
chmod +x "$dir/keyboard-switch"
mkdir -p ~/.local/share/applications
cat > ~/.local/share/applications/keyboard-switch.desktop <<EOF
[Desktop Entry]
Type=Application
Name=Keyboard Switch
Comment=Convert text typed with the wrong keyboard layout
Exec=$dir/keyboard-switch
Icon=input-keyboard
Categories=Utility;
EOF

echo
if [ "$relogin" = 1 ]; then
    echo "Done. Log out and back in (group change), then start 'Keyboard Switch' from the menu."
else
    echo "Done. Start 'Keyboard Switch' from the menu or run: $dir/keyboard-switch"
fi
