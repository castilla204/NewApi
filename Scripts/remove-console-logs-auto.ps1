# Script AUTOMÁTICO para eliminar todos los Console.* de archivos .cs
# ⚠️ ADVERTENCIA: Este script MODIFICA archivos automáticamente
# Hace backup antes de modificar

Write-Host "🚀 Script automático para eliminar Console.*" -ForegroundColor Cyan
Write-Host "⚠️  ADVERTENCIA: Este script modificará archivos .cs" -ForegroundColor Yellow
Write-Host ""

$confirm = Read-Host "¿Continuar? (S/N)"
if ($confirm -ne "S" -and $confirm -ne "s") {
    Write-Host "❌ Operación cancelada" -ForegroundColor Red
    exit
}

# Crear carpeta de backup
$backupDir = ".\backup-console-logs-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
Write-Host "📦 Creando backup en: $backupDir" -ForegroundColor Cyan

# Buscar todos los archivos .cs excepto en bin/obj
$files = Get-ChildItem -Path . -Filter "*.cs" -Recurse | 
    Where-Object { 
        $_.FullName -notmatch "\\bin\\" -and 
        $_.FullName -notmatch "\\obj\\" -and
        $_.FullName -notmatch "\\.git\\"
    }

$totalReplacements = 0
$filesModified = @()

foreach ($file in $files) {
    $content = Get-Content $file.FullName -Raw
    $originalContent = $content
    
    # Buscar y eliminar Console.WriteLine, Console.Write, Console.Error, etc.
    # Patrón: Console.WriteLine(...); o Console.WriteLine(...) seguido de salto de línea
    $patterns = @(
        # Console.WriteLine con diferentes formatos
        '(?m)^\s*Console\.WriteLine\s*\([^)]*\);\s*$\r?\n',
        '(?m)Console\.WriteLine\s*\([^)]*\);\s*',
        # Console.Write
        '(?m)^\s*Console\.Write\s*\([^)]*\);\s*$\r?\n',
        '(?m)Console\.Write\s*\([^)]*\);\s*',
        # Console.Error
        '(?m)^\s*Console\.Error\s*\([^)]*\);\s*$\r?\n',
        '(?m)Console\.Error\s*\([^)]*\);\s*',
        # Console.Out
        '(?m)^\s*Console\.Out\s*\([^)]*\);\s*$\r?\n',
        '(?m)Console\.Out\s*\([^)]*\);\s*'
    )
    
    $modified = $false
    foreach ($pattern in $patterns) {
        $matches = [regex]::Matches($content, $pattern)
        if ($matches.Count -gt 0) {
            $content = $content -replace $pattern, ''
            $totalReplacements += $matches.Count
            $modified = $true
        }
    }
    
    if ($modified) {
        # Hacer backup
        $relativePath = $file.FullName.Replace((Get-Location).Path + "\", "")
        $backupPath = Join-Path $backupDir $relativePath
        $backupDirPath = Split-Path $backupPath -Parent
        New-Item -ItemType Directory -Path $backupDirPath -Force | Out-Null
        Copy-Item $file.FullName $backupPath -Force
        
        # Guardar archivo modificado
        Set-Content -Path $file.FullName -Value $content -NoNewline
        $filesModified += $file.FullName
        Write-Host "✅ Modificado: $($file.Name)" -ForegroundColor Green
    }
}

Write-Host "`n📊 Resumen:" -ForegroundColor Cyan
Write-Host "   Archivos modificados: $($filesModified.Count)" -ForegroundColor Yellow
Write-Host "   Total Console.* eliminados: $totalReplacements" -ForegroundColor Yellow
Write-Host "   Backup guardado en: $backupDir" -ForegroundColor Green

if ($filesModified.Count -gt 0) {
    Write-Host "`n📝 Archivos modificados:" -ForegroundColor Cyan
    foreach ($file in $filesModified) {
        Write-Host "   - $file" -ForegroundColor Gray
    }
}

Write-Host "`n✅ Proceso completado!" -ForegroundColor Green


