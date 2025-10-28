@echo off
echo 🔄 CONFIGURACIÓN MCP ACTUALIZADA - SERVIDOR DE ACCESO COMPLETO
echo.
echo ✅ Cambios realizados:
echo - Cambiado a @syahiidkamil/mcp-postgres-full-access
echo - Este servidor permite operaciones de escritura por defecto
echo.
echo 📋 PASOS PARA APLICAR CAMBIOS:
echo.
echo 1️⃣  Cierra Cursor completamente (Ctrl+Shift+Q)
echo 2️⃣  Espera 5 segundos
echo 3️⃣  Abre Cursor de nuevo
echo 4️⃣  Los agentes MCP se reiniciarán automáticamente
echo.
echo 🧪 DESPUÉS DEL REINICIO:
echo - El agente postgres-db debería permitir escritura
echo - Podrás ejecutar UPDATE, INSERT, DELETE
echo - Ejecuta: populate_colors.sql para poblar colores
echo.
echo ⚠️  IMPORTANTE: 
echo - No cierres esta ventana hasta reiniciar Cursor
echo - La configuración se aplicará en el próximo inicio
echo.
echo 🎯 PRÓXIMO PASO:
echo Ejecutar populate_colors.sql después del reinicio
echo.
echo 📝 CONFIGURACIÓN ACTUAL:
echo Servidor: @syahiidkamil/mcp-postgres-full-access@latest
echo Base de datos: postgresql://admin:***@185.166.39.4:30000/atrapo
echo.
pause


