# Script para aplicar migraciones de EF Core a Supabase usando el MCP
# Lee el archivo migrations.sql y lo aplica en partes

$script = Get-Content migrations.sql -Raw
$transactions = $script -split '(?=START TRANSACTION;)' | Where-Object { $_.Trim() -ne '' }

Write-Output "Total partes: $($transactions.Count)"
Write-Output "Aplicando migraciones..."

# La primera parte es la creación de la tabla de migraciones (ya existe)
# Aplicamos desde la segunda parte en adelante
for ($i = 1; $i -lt $transactions.Count; $i++) {
    $transaction = $transactions[$i].Trim()
    if ($transaction -ne '') {
        Write-Output "Aplicando parte $($i+1)/$($transactions.Count) ($($transaction.Length) caracteres)..."
        # Nota: Este script necesita ser ejecutado manualmente o integrado con el MCP
        # Por ahora solo mostramos el progreso
    }
}

Write-Output "✅ Script preparado para aplicar $($transactions.Count-1) transacciones de migración"





