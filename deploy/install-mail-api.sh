#!/usr/bin/env bash
set -euo pipefail
stage=/root/kiberone-mail-api-stage
target=/opt/kiberone-mail-api
backup="/root/kiberone-mail-api-backup-$(date -u +%Y%m%dT%H%M%SZ)"
install -d -m 0700 "$backup"
cp -a /etc/nginx/sites-available/nshub.pro "$backup/nginx-nshub.pro"
cp -a /etc/systemd/system/kiberone-hub.service "$backup/kiberone-hub.service"
cp -a /var/lib/kiberone-hub/location-secrets.json "$backup/location-secrets.json"
cp -a /etc/postfix/vmailbox "$backup/vmailbox"
sha256sum /opt/kiberone-hub/Kiberone.Hub /var/lib/kiberone-hub/updates/student_manifest.json > "$backup/hub-before.sha256"
if [[ -d "$target" ]]; then cp -a "$target" "$backup/previous-mail-api"; fi
install -d -m 0755 "$target"
install -m 0755 "$stage/Kiberone.MailApi" "$target/Kiberone.MailApi"
install -m 0700 "$stage/provision-student-mailbox.sh" "$target/provision-student-mailbox.sh"
install -d -m 0700 /var/lib/kiberone-hub/mail-accounts
install -m 0644 "$stage/kiberone-mail-api.service" /etc/systemd/system/kiberone-mail-api.service
python3 - <<'PY'
from pathlib import Path
p = Path('/etc/nginx/sites-available/nshub.pro')
s = p.read_text()
if 'zone=kiberone_mail:' not in s:
    s = 'limit_req_zone $binary_remote_addr zone=kiberone_mail:10m rate=5r/s;\n\n' + s
if 'location ^~ /api/mail/' not in s:
    marker = '    location /api/ {'
    if marker not in s:
        raise SystemExit('Expected portal API location not found')
    block = '''    # KIBERone incoming mail; keep the tournament portal API separate.
    location ^~ /api/mail/ {
        limit_req zone=kiberone_mail burst=20 nodelay;
        limit_req_status 429;
        client_max_body_size 16k;
        proxy_pass http://127.0.0.1:8790;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-Proto https;
        proxy_read_timeout 30s;
        add_header Cache-Control "no-store" always;
    }

'''
    s = s.replace(marker, block + marker, 1)
p.write_text(s)
PY
if ! nginx -t; then
    cp -a "$backup/nginx-nshub.pro" /etc/nginx/sites-available/nshub.pro
    exit 1
fi
systemctl daemon-reload
systemctl enable kiberone-mail-api
systemctl restart kiberone-mail-api
for attempt in {1..15}; do
    if curl -fsS http://127.0.0.1:8790/api/mail/health > /dev/null; then break; fi
    sleep 1
done
curl -fsS http://127.0.0.1:8790/api/mail/health
systemctl reload nginx
sha256sum -c "$backup/hub-before.sha256"
printf '\nBackup: %s\n' "$backup"
