@echo off
setlocal EnableExtensions
cd /d "%~dp0"

echo.
echo  KIBERone Setup
echo  ----------------
echo   1  Student  (класс, VPN)
echo   2  Tutor    (преподаватель)
echo   3  Оба
echo   0  Выход
echo.
set /p "CHOICE=Выбор [1/2/3/0]: "

if "%CHOICE%"=="1" goto student
if "%CHOICE%"=="2" goto tutor
if "%CHOICE%"=="3" goto both
if "%CHOICE%"=="0" exit /b 0
echo Неверный выбор.
pause
exit /b 1

:student
call "%~dp0Student\Install-Student.cmd"
exit /b %ERRORLEVEL%

:tutor
call "%~dp0Tutor\Install-Tutor.cmd"
exit /b %ERRORLEVEL%

:both
call "%~dp0Student\Install-Student.cmd"
set "ST=%ERRORLEVEL%"
call "%~dp0Tutor\Install-Tutor.cmd"
set "TU=%ERRORLEVEL%"
if not "%ST%"=="0" exit /b %ST%
exit /b %TU%
