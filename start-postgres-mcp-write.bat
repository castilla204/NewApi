@echo off
echo Starting PostgreSQL MCP Server with WRITE permissions...
echo.

REM Set the password environment variable
set PGPASSWORD=Pedrohabo1//

REM Start the PostgreSQL MCP server with write permissions
echo Connection: postgresql://admin:***@185.166.39.4:30000/atrapo
echo Starting MCP Server with WRITE access...
npx -y @modelcontextprotocol/server-postgres "postgresql://admin:Pedrohabo1%2F%2F@185.166.39.4:30000/atrapo" --disable-read-only

echo.
echo MCP Server stopped.
