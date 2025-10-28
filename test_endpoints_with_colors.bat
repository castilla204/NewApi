@echo off
echo 🧪 Probando endpoints con SystemStatusDto y colores...
echo.

echo 1️⃣ Probando SearchHire/expert...
curl -s -H "X-Development-Mode: true" "http://localhost:7124/api/SearchHire/expert" | jq ".[0] | {id, status, statusTranslated, statusInfo: {displayName, description, color, isFinalizationStatus}}"
echo.

echo 2️⃣ Probando Search con paginación...
curl -s -H "X-Development-Mode: true" "http://localhost:7124/api/Search?page=1&pageSize=5&sortBy=createdAt&sortDirection=desc" | jq ".searches[0] | {id, title, searchHire: {status, statusTranslated, statusInfo: {displayName, description, color, isFinalizationStatus}}}"
echo.

echo 3️⃣ Probando Search/243/details-complete...
curl -s -H "X-Development-Mode: true" "http://localhost:7124/api/Search/243/details-complete" | jq ".search.searchHire | {status, statusTranslated, statusInfo: {displayName, description, color, isFinalizationStatus}}"
echo.

echo ✅ Pruebas completadas!
