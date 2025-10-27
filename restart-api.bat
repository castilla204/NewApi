@echo off
echo 🔄 Reiniciando API .NET...
echo.

echo ⏹️ Deteniendo procesos existentes...
taskkill /F /IM dotnet.exe 2>nul

echo ⏳ Esperando 3 segundos...
timeout /t 3 /nobreak >nul

echo 🚀 Iniciando API...
cd /d "C:\Users\Diego\OneDrive - Educacyl\Escritorio\App\newApi"
dotnet run

echo.
echo ✅ API reiniciada con modo de desarrollo habilitado
pause

