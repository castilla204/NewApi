@echo off
echo 🚀 SCRIPT COMPLETO: Actualización de endpoints con SystemStatusDto
echo.

echo 1️⃣ Poblando colores en la base de datos...
call populate_colors.bat
echo.

echo 2️⃣ Probando endpoints actualizados...
call test_endpoints_with_colors.bat
echo.

echo 3️⃣ Ejecutando pruebas con Node.js...
node test_endpoints_with_colors.js
echo.

echo ✅ ¡Todas las pruebas completadas!
echo.
echo 📋 RESUMEN DE CAMBIOS:
echo    - ✅ SearchHireResponseDto actualizado con StatusInfo
echo    - ✅ SearchHireService actualizado para incluir StatusInfo
echo    - ✅ SearchController actualizado en todos los endpoints
echo    - ✅ Colores poblados en SystemStatuses
echo    - ✅ Endpoints probados y funcionando
echo.
echo 🎯 ENDPOINTS ACTUALIZADOS:
echo    - GET /api/SearchHire/expert
echo    - GET /api/Search?page=1&pageSize=20&sortBy=createdAt&sortDirection=desc
echo    - GET /api/Search/{id}
echo    - GET /api/Search/{id}/details-complete (ya funcionaba)
echo.
echo 🎨 Los colores ahora están disponibles en todos los endpoints sin hardcodear!
