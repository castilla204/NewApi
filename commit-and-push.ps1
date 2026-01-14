# Configurar git para evitar paginador
$env:GIT_PAGER = ''
$env:PAGER = ''

# Agregar archivos
git add appsettings.json appsettings.Development.json

# Hacer commit
git commit -m "FIX: Restaurar configuración puerto 5432 después de rollback - Mantener Session Pooler (5432) en todos los entornos - Actualizar appsettings.json y appsettings.Development.json con puerto 5432"

# Push
git push origin HEAD

Write-Host "Commit y push completados"



