# Script para aplicar migraciones a Supabase automáticamente
# Divide el script en partes y las aplica secuencialmente

$scriptPath = "migrations.sql"
$script = Get-Content $scriptPath -Raw

Write-Host "📦 Script completo: $($script.Length) caracteres" -ForegroundColor Cyan

# Dividir el script en partes de ~30,000 caracteres, asegurándose de que cada parte termine en COMMIT;
$chunkSize = 30000
$chunks = @()
$currentPos = 0

while ($currentPos -lt $script.Length) {
    $remaining = $script.Length - $currentPos
    $chunkLength = [Math]::Min($chunkSize, $remaining)
    $chunk = $script.Substring($currentPos, $chunkLength)
    
    # Buscar el último COMMIT; en este chunk
    $lastCommit = $chunk.LastIndexOf('COMMIT;')
    
    if ($lastCommit -gt 0) {
        # Incluir el COMMIT; completo
        $chunks += $chunk.Substring(0, $lastCommit + 7)
        $currentPos += $lastCommit + 7
    } else {
        # Si no hay COMMIT, tomar todo el chunk (última parte)
        if ($chunk.Trim().Length -gt 0) {
            $chunks += $chunk
        }
        $currentPos = $script.Length
    }
}

Write-Host "✅ Script dividido en $($chunks.Count) partes" -ForegroundColor Green
for ($i = 0; $i -lt $chunks.Count; $i++) {
    Write-Host "  Parte $($i+1): $($chunks[$i].Length) caracteres" -ForegroundColor Yellow
}

Write-Host "`n📝 Aplicando migraciones con MCP de Supabase..." -ForegroundColor Cyan
Write-Host "   (Este script solo muestra las partes. Usa el MCP para aplicar cada parte.)" -ForegroundColor Yellow

# Guardar cada parte en un archivo separado para referencia
for ($i = 0; $i -lt $chunks.Count; $i++) {
    $chunks[$i] | Out-File -Encoding utf8 -FilePath "migration_part_$($i+1).sql"
    Write-Host "  ✅ Guardada: migration_part_$($i+1).sql" -ForegroundColor Green
}

Write-Host "`n✅ Archivos de migración preparados. Aplica cada parte usando el MCP de Supabase." -ForegroundColor Green





