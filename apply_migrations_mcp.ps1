# Script para aplicar migraciones a Supabase usando el MCP
# Divide el script en partes y las aplica secuencialmente

$scriptPath = "migrations.sql"
$script = Get-Content $scriptPath -Raw

Write-Host "📦 Script completo: $($script.Length) caracteres" -ForegroundColor Cyan

# Dividir el script en partes de ~50,000 caracteres, asegurándose de que cada parte termine en COMMIT;
$chunkSize = 50000
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
        $chunks += $chunk
        $currentPos = $script.Length
    }
}

Write-Host "✅ Script dividido en $($chunks.Count) partes" -ForegroundColor Green
for ($i = 0; $i -lt $chunks.Count; $i++) {
    Write-Host "  Parte $($i+1): $($chunks[$i].Length) caracteres" -ForegroundColor Yellow
}

Write-Host "`n📝 Para aplicar las migraciones, usa el MCP de Supabase con cada parte." -ForegroundColor Cyan
Write-Host "   O copia y pega el contenido de migrations.sql en el SQL Editor de Supabase." -ForegroundColor Cyan


