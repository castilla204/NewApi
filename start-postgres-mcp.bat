@echo off
echo Starting PostgreSQL MCP Server...
echo.

REM Set the password environment variable
set PGPASSWORD=Pedrohabo1//

REM Start the PostgreSQL MCP server with proper URL encoding
echo Connection: postgresql://admin:***@185.166.39.4:30000/atrapo
echo Starting MCP Server...
npx -y @modelcontextprotocol/server-postgres "postgresql://admin:Pedrohabo1%2F%2F@185.166.39.4:30000/atrapo"

echo.
echo MCP Server stopped.
















