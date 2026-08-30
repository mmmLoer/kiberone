# KIBERone Classroom

Локальная LAN-система для компьютерного класса: приложение тьютора, приложение ученика, SQLite и HTTP API.

## Текущий рабочий срез

- C# / .NET 8.0.424.
- Текущая версия: 0.2.0.
- Avalonia 11.2.3 + CommunityToolkit.Mvvm 8.3.2.
- EF Core SQLite 8.0.13, WAL и foreign keys.
- Конструктор многоэтапных уроков печати в Tutor.
- Student-экран печати с запретом Backspace и паузой по Escape.
- CPM, точность, прогресс, ошибки, активное/приостановленное время и проблемные символы.
- Персональная телеметрия учеников и сохранение временных срезов.
- Три разные номинации победителей без повторения ученика.
- HTTP API уроков и сессий на порту 8765.
- UDP discovery на портах 8766/8767 и автоматическое подключение Student.
- Heartbeat каждые 3 секунды и online-таймаут 15 секунд.
- Надёжная очередь команд: повторная выдача до ACK, TTL и журнал receipts.
- Безопасный разрешённый набор команд; удалённый shell не включён.
- Случайный 256-битный sync-токен вместо общего пароля в исходном коде.

## Запуск

```powershell
.\scripts\run-tutor.ps1
.\scripts\run-student.ps1
```

Локальная база и sync-токен Tutor хранятся в `%LOCALAPPDATA%\KIBERone Classroom`.

Автономные релизные EXE также находятся в корне проекта и в каталогах `dist/Tutor-win-x64`, `dist/Student-win-x64`. Копия Student для обновления и её SHA-256-манифест находятся в `updates`.

## Проверка

```powershell
.\scripts\verify.ps1
```

Скрипт выполняет Release-сборку решения и все тесты.

## Основные маршруты первого среза

- `GET /health`
- `GET /typing/lessons`
- `POST /heartbeat`
- `GET /clients`
- `GET /commands?client_id=...`
- `POST /command`
- `POST /commands/{id}/ack?client_id=...`
- `GET /command-receipts`
- `GET /typing/lessons/{id}`
- `POST /typing/lessons`
- `PUT /typing/lessons/{id}`
- `POST /typing/sessions`
- `GET /typing/sessions/{id}`
- `POST /typing/sessions/{id}/telemetry`
- `POST /typing/sessions/{id}/finish`

Все маршруты, кроме `/health`, требуют заголовок `X-Sync-Token`.

## Структура

- `src/Kiberone.Core` — доменные модели, DTO, метрики и правила.
- `src/Kiberone.Infrastructure` — SQLite, сервис уроков и HTTP-сервер.
- `src/Kiberone.Tutor` — приложение преподавателя.
- `src/Kiberone.Student` — приложение ученика.
- `src/Kiberone.VpnAgent` — Windows Service: WireGuard embeddable-dll-service + HTTP API `:9777` (роутер → connect/disconnect).
- `tests/Kiberone.Tests` — unit и SQLite integration tests.

VPN-агент (отдельно от Classroom LAN): см. `src/Kiberone.VpnAgent/README.md`, установка `scripts/install-vpn-agent.ps1`.

## Следующие этапы

Система реализуется по `SYSTEM_DOCUMENTATION.md`. Текущий срез не объявляется полной готовностью всех 28 экранов: далее необходимы discovery, roster, команды, синхронизация файлов и версии, approvals, магазин, достижения, screen preview, focus/watchdog, deploy/update и полная LAN-интеграция Student.
