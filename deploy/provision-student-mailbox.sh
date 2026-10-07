#!/usr/bin/env bash
set -euo pipefail
# Intended for the existing virtual Maildir setup on nshub.pro.
username="${1:-}"
[[ "$username" =~ ^s-[0-9a-f]{32}$ ]] || { echo 'Invalid mailbox username' >&2; exit 2; }
[[ "$EUID" == 0 ]] || { echo 'Run through the privileged Hub service' >&2; exit 2; }
uid_map="$(postconf -h virtual_uid_maps)"
gid_map="$(postconf -h virtual_gid_maps)"
[[ "$uid_map" =~ ^static:[0-9]+$ && "$gid_map" =~ ^static:[0-9]+$ ]] || { echo 'Expected static virtual UID/GID maps' >&2; exit 2; }
base="$(postconf -h virtual_mailbox_base)"
[[ "$base" == /var/mail/nshub ]] || { echo 'Unexpected virtual mailbox root' >&2; exit 2; }
exec 9>/run/kiberone-mail-api/mailbox.lock
flock 9
alias="${2:-$username}"
[[ "$alias" =~ ^[a-z0-9]{1,64}$ || "$alias" == "$username" ]] || exit 2
address="$alias@students.nshub.pro"
relative="students.nshub.pro/$username/Maildir/"
install -d -m 0700 -o "${uid_map#static:}" -g "${gid_map#static:}" "$base/students.nshub.pro/$username"
for folder in Maildir Maildir/new Maildir/cur Maildir/tmp; do
  install -d -m 0700 -o "${uid_map#static:}" -g "${gid_map#static:}" "$base/students.nshub.pro/$username/$folder"
done
map=/etc/postfix/vmailbox
if existing="$(postmap -q "$address" "hash:$map")"; then :; else
  result=$?; [[ "$result" == 1 ]] || exit "$result"
fi
[[ -z "$existing" || "$existing" == "$relative" ]] || { echo 'Address already assigned' >&2; exit 3; }
if ! awk -v address="$address" '$1 == address {found=1} END {exit !found}' "$map"; then
  printf '%s %s\n' "$address" "$relative" >> "$map"
fi
legacy="$username@students.nshub.pro"
if ! awk -v address="$legacy" '$1 == address {found=1} END {exit !found}' "$map"; then
  printf '%s %s\n' "$legacy" "$relative" >> "$map"
fi
postmap "$map"
chmod 0644 "$map" "$map.db"
