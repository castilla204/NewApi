@echo off
echo Starting PostgreSQL MCP Server...
echo.

REM Set the password environment variable (debe estar configurado en el sistema)
REM set PGPASSWORD=tu_password_aqui

REM Start the PostgreSQL MCP server with proper URL encoding
REM La contraseña debe venir de la variable de entorno PGPASSWORD
echo Connection: postgresql://admin:***@185.166.39.4:30000/atrapo
echo Starting MCP Server...
if "%PGPASSWORD%"=="" (
    echo ERROR: PGPASSWORD environment variable not set
    echo Please set PGPASSWORD before running this script
    exit /b 1
)
npx -y @modelcontextprotocol/server-postgres "postgresql://admin:%PGPASSWORD%@185.166.39.4:30000/atrapo"

echo.
echo MCP Server stopped.
















