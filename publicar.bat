@echo off
rem Publica KCMundial 2 en C:\KCMundial2 (separada de la app vieja, que no se toca).
setlocal
set DESTINO=C:\KCMundial2

dotnet publish "%~dp0src\KCMundial.App\KCMundial.App.csproj" -c Release -r win-x64 --self-contained false -o "%DESTINO%"
if errorlevel 1 (
  echo.
  echo *** Fallo la publicacion. Revisa los errores de arriba. ***
  pause
  exit /b 1
)

echo.
echo Listo: %DESTINO%\KCMundial2.exe
echo El video del segundo monitor va en %DESTINO%\assets\promo.mp4
pause
