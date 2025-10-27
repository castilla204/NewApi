@echo off
echo Resetting Cursor MCP configuration...
echo.

REM Kill any running Cursor processes
echo Stopping Cursor processes...
taskkill /f /im "Cursor.exe" 2>nul
taskkill /f /im "cursor.exe" 2>nul

REM Wait a moment
timeout /t 2 /nobreak >nul

REM Clear any MCP cache (if it exists)
echo Clearing MCP cache...
if exist "%APPDATA%\Cursor\User\globalStorage\cursor.mcp" (
    rmdir /s /q "%APPDATA%\Cursor\User\globalStorage\cursor.mcp" 2>nul
)

REM Clear workspace cache
if exist "%APPDATA%\Cursor\User\workspaceStorage" (
    echo Clearing workspace cache...
    for /d %%i in ("%APPDATA%\Cursor\User\workspaceStorage\*") do (
        if exist "%%i\mcp" rmdir /s /q "%%i\mcp" 2>nul
    )
)

echo.
echo MCP configuration reset complete!
echo Please restart Cursor manually.
echo.
pause
