KIBERone Student - установка (Windows x64)

1. Распакуйте архив в любую папку.
2. Запустите Install-Student.cmd (потребуется пароль администратора один раз).
3. На рабочем столе появится ярлык "KIBERone Student".

Установщик Inno / Install-Student.cmd:
- копирует программу в Program Files\KIBERone\Student
- сам ставит VPN-службу KIBERoneStudentVpn (встроенный WireGuard: tunnel.dll + wireguard.dll)
- отдельный bat/cmd для VPN не нужен
- после этого VPN включается из Tutor без UAC

Конфиг VPN тьютор раздаёт автоматически.

Если VPN всё же не работает (редко на виртуалках):
- Repair-Student-Vpn.cmd лежит в папке установки — только для ремонта
- или снова запустите установщик от администратора
