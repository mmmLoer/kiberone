# Сборка KIBERone под Windows с Linux CI

Инструкция для агента/сервера: как воспроизвести локальный Windows-релиз (`scripts/build-installers.ps1`) на Linux, с кросс-публикацией `win-x64`.

Репозиторий: `https://github.com/mmmLoer/kiberone` (ветка `main`).

---

## Цель

Собрать **self-contained win-x64** артефакты:

| Артефакт | Путь |
|---|---|
| Portable Student | `dist/Student-win-x64/` |
| Portable Tutor | `dist/Tutor-win-x64/` |
| Student installer zip | `dist/installers/KIBERoneStudent-Setup-{VERSION}-win-x64.zip` |
| Tutor installer zip | `dist/installers/KIBERoneTutor-Setup-{VERSION}-win-x64.zip` |

Локально на Windows это делает:

```powershell
.\scripts\build-installers.ps1
```

Внутри: `publish-student.ps1` → `publish-tutor.ps1` → упаковка zip → `updates/student_manifest.json`.

На Linux те же шаги повторяются через `dotnet` (+ bash или `pwsh`).

---

## Версия

Держать в синхроне:

1. `src/Kiberone.Core/ClassroomNetwork.cs` → `public const string Version`
2. `scripts/build-installers.ps1` → `$version = "..."` (имя zip и manifest)

Текущее значение смотреть в этих файлах (на момент написания: **0.10.7**).

---

## Требования к Linux CI

1. **.NET SDK 8** (`dotnet --list-sdks` ≥ 8.0).
2. Windows targeting для `net8.0-windows` (Student / Vpn):

   ```bash
   export EnableWindowsTargeting=true
   ```

   Student — `net8.0-windows` + `UseWindowsForms=true`. Cross-compile с Linux обычно проходит для `publish`, но **не запускать** Student на Linux.
3. Prebuilt Windows native DLL WireGuard (**обязательно**):

   - `tunnel.dll`
   - `wireguard.dll`

   Класть в `src/Kiberone.VpnAgent/native/` (fallback: `src/Kiberone.Student/native/`).  
   В git их часто нет — хранить в CI secret / artifact cache / LFS.
4. Zip-утилита (`zip` или `Compress-Archive` в pwsh).

Tutor — `net8.0` Avalonia; кросс-сборка проще, чем Student.

---

## Канонические команды publish

Из корня репозитория.

### Student

```bash
export EnableWindowsTargeting=true
export DOTNET_CLI_TELEMETRY_OPTOUT=1

dotnet publish src/Kiberone.Student/Kiberone.Student.csproj \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -o dist/Student-win-x64 \
  -p:PublishSingleFile=false
```

После publish **обязательно** скопировать native и VPN-installer:

```bash
mkdir -p dist/Student-win-x64/native
cp src/Kiberone.VpnAgent/native/tunnel.dll    dist/Student-win-x64/native/
cp src/Kiberone.VpnAgent/native/wireguard.dll dist/Student-win-x64/native/
# дублировать в корень publish (так делает publish-student.ps1)
cp src/Kiberone.VpnAgent/native/tunnel.dll    dist/Student-win-x64/
cp src/Kiberone.VpnAgent/native/wireguard.dll dist/Student-win-x64/

mkdir -p dist/Student-win-x64/service
cp scripts/install-student-vpn-service.ps1 dist/Student-win-x64/service/
```

`PublishSingleFile=false` **обязателен**: иначе WireGuard / SkiaSharp native DLL не подхватываются.

### Tutor

```bash
dotnet publish src/Kiberone.Tutor/Kiberone.Tutor.csproj \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -o dist/Tutor-win-x64 \
  -p:PublishSingleFile=false
```

---

## Упаковка installers

`VERSION` — из `ClassroomNetwork.cs` / `build-installers.ps1`.

### Student zip layout

```
KIBERoneStudent-Setup-{VERSION}-win-x64.zip
├── app/                          ← всё из dist/Student-win-x64/*
├── service/
│   └── install-student-vpn-service.ps1
├── Setup-Student.ps1             ← из install/
├── Install-Student.cmd
├── Create-Student-Shortcut.ps1
├── Repair-Student-Vpn.cmd
└── README-Student.txt
```

### Tutor zip layout

```
KIBERoneTutor-Setup-{VERSION}-win-x64.zip
├── app/                          ← всё из dist/Tutor-win-x64/*
├── Setup-Tutor.ps1
├── Install-Tutor.cmd
├── Create-Tutor-Shortcut.ps1
└── README-Tutor.txt
```

### Пример bash

```bash
VERSION=0.10.7
STAGE=dist/installers/_staging
rm -rf "$STAGE" && mkdir -p "$STAGE/student/app" "$STAGE/student/service" "$STAGE/tutor/app"

cp -a dist/Student-win-x64/. "$STAGE/student/app/"
cp install/Setup-Student.ps1 install/Install-Student.cmd \
   install/Create-Student-Shortcut.ps1 install/Repair-Student-Vpn.cmd \
   install/README-Student.txt "$STAGE/student/"
cp scripts/install-student-vpn-service.ps1 "$STAGE/student/service/"

cp -a dist/Tutor-win-x64/. "$STAGE/tutor/app/"
cp install/Setup-Tutor.ps1 install/Install-Tutor.cmd \
   install/Create-Tutor-Shortcut.ps1 install/README-Tutor.txt "$STAGE/tutor/"

mkdir -p dist/installers
(cd "$STAGE/student" && zip -r "../../KIBERoneStudent-Setup-${VERSION}-win-x64.zip" .)
(cd "$STAGE/tutor"   && zip -r "../../KIBERoneTutor-Setup-${VERSION}-win-x64.zip" .)
rm -rf "$STAGE"
```

### Опционально: update manifest

Локальный update pipeline:

1. Скопировать `dist/Student-win-x64/Kiberone.Student.exe` → `updates/KIBERoneStudent.exe`
2. Записать `updates/student_manifest.json`:

```json
{
  "version": "0.10.7",
  "filename": "KIBERoneStudent.exe",
  "size": 0,
  "sha256": "<hex lowercase sha256 of updates/KIBERoneStudent.exe>",
  "published_at": "2026-09-03T22:00:00Z"
}
```

---

## Что не делать на Linux CI

- Не останавливать `KIBERoneStudentVpn` / процессы Student — это только Windows-хост с залоченными DLL.
- Не собирать с `PublishSingleFile=true`.
- Не менять RID: только `win-x64`.
- Не обязательно собирать Hub — для клиентских installers не нужен.

---

## Чеклист внедрения на сервере

1. Clone `main`, установить SDK 8, выставить `EnableWindowsTargeting=true`.
2. Положить `tunnel.dll` + `wireguard.dll` в `src/Kiberone.VpnAgent/native/`.
3. Добавить `scripts/build-installers.sh` (эквивалент `build-installers.ps1`) **или** вызывать существующие ps1 через `pwsh`.
4. CI job: `git pull` → publish Student → publish Tutor → zip → upload artifacts.
5. Smoke на **Windows** (не на Linux):
   - распаковать zip, запустить `Install-*.cmd`;
   - Student: VPN bridge + `native/tunnel.dll` рядом с exe;
   - Tutor: UI стартует без missing SkiaSharp DLL.
6. Версию брать из одного места и прокидывать в имя zip + manifest.

---

## Эталон: как собирается на Windows сейчас

```powershell
# 1. git pull
# 2. если dist залочен:
#    Stop-Service KIBERoneStudentVpn
#    Stop-Process -Name Kiberone.Student, Kiberone.Tutor -Force
# 3. релиз
.\scripts\build-installers.ps1
```

Эквивалент на Linux — секции **Канонические команды publish** + **Упаковка installers**.

Ключевые скрипты в репо:

| Скрипт | Назначение |
|---|---|
| `scripts/build-installers.ps1` | Полный релиз: publish + zip + manifest |
| `scripts/publish-student.ps1` | `dotnet publish` Student + native + service script |
| `scripts/publish-tutor.ps1` | `dotnet publish` Tutor |
| `install/*` | Содержимое installer zip (setup/shortcut/README) |
