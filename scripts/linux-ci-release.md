# Сборка KIBERone под Windows с Linux CI

Каноническая инструкция перенесена в репозиторий из операционного runbook.

## Быстрый старт на сервере

1. Clone `main`, SDK 8, `tunnel.dll` + `wireguard.dll` в `src/Kiberone.VpnAgent/native/`.
2. Полный релиз: `./scripts/build-installers.sh`
3. Авто-pull + publish в Hub:

```bash
export KIBERONE_HUB_DATA=/var/lib/kiberone-hub   # каталог Hub (KIBERONE_HUB_DATA)
export KIBERONE_SKIP_BUILD_IF_UNCHANGED=1
./scripts/ci-pull-and-release.sh
```

Cron (каждые 15 мин):

```
*/15 * * * * KIBERONE_HUB_DATA=/var/lib/kiberone-hub KIBERONE_SKIP_BUILD_IF_UNCHANGED=1 /opt/kiberone/scripts/ci-pull-and-release.sh >> /var/log/kiberone-release.log 2>&1
```

Hub раздаёт `GET /api/update/student` + `/api/update/student/file`.  
Tutor с включённым «Разрешить обновления Student» подтягивает релиз при старте и кладёт его в локальный `updates/` для класса.

Подробные publish/zip команды — см. комментарии в `scripts/build-installers.sh` и исходный runbook `linux-windows-build.md`.
