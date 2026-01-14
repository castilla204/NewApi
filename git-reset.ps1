# Script para hacer rollback sin paginador
$env:GIT_PAGER = ''
$env:PAGER = ''
git config --local core.pager ''

# Hacer el reset
git reset --hard 9d286004a7571eb3eee6c482bdeef145d384732e

# Verificar
Write-Host "Rollback completado al commit 9d28600"

