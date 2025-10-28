@echo off
echo 🔄 CONFIGURACIÓN MCP ACTUALIZADA - SEGÚN DOCUMENTACIÓN OFICIAL
echo.
echo ✅ Cambios realizados:
echo - Configuración según guía oficial de Apidog
echo - Servidor: @modelcontextprotocol/server-postgres@latest
echo - Configuración: postgresql-mcp con alwaysAllow
echo.
echo 📋 PASOS PARA APLICAR CAMBIOS:
echo.
echo 1️⃣  Cierra Cursor completamente (Ctrl+Shift+Q)
echo 2️⃣  Espera 5 segundos
echo 3️⃣  Abre Cursor de nuevo
echo 4️⃣  Los agentes MCP se reiniciarán automáticamente
echo.
echo 🧪 DESPUÉS DEL REINICIO:
echo - El agente postgresql-mcp debería estar disponible
echo - Podrás ejecutar consultas SQL directamente
echo - Ejecuta: populate_colors_direct.js para poblar colores
echo.
echo ⚠️  IMPORTANTE: 
echo - No cierres esta ventana hasta reiniciar Cursor
echo - La configuración se aplicará en el próximo inicio
echo.
echo 🎯 PRÓXIMOS PASOS:
echo 1. Reiniciar Cursor
echo 2. Probar agente postgresql-mcp
echo 3. Ejecutar populate_colors_direct.js
echo 4. Probar endpoint details-complete
echo.
echo 📝 CONFIGURACIÓN ACTUAL:
echo Servidor: @modelcontextprotocol/server-postgres@latest
echo Nombre: postgresql-mcp
echo Base de datos: postgresql://admin:***@185.166.39.4:30000/atrapo
echo.
pause

