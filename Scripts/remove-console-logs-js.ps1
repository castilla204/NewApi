# Script para eliminar todos los console.log de archivos .js
# Uso: .\Scripts\remove-console-logs-js.ps1 [--dry-run]

param(
    [switch]$DryRun = $false
)

Write-Host "🔍 Buscando todos los console.log en archivos .js..." -ForegroundColor Cyan
Write-Host ""

if ($DryRun) {
    Write-Host "💡 MODO DRY-RUN: No se modificarán archivos" -ForegroundColor Yellow
    Write-Host ""
}

# Buscar todos los archivos .js excepto en bin/obj/node_modules
$files = Get-ChildItem -Path . -Filter "*.js" -Recurse | 
    Where-Object { 
        $_.FullName -notmatch "\\bin\\" -and 
        $_.FullName -notmatch "\\obj\\" -and
        $_.FullName -notmatch "\\node_modules\\" -and
        $_.FullName -notmatch "\\.git\\"
    }

$totalFound = 0
$filesWithConsole = @()

foreach ($file in $files) {
    $content = Get-Content $file.FullName -Raw
    $originalContent = $content
    
    # Patrones para console.log y variantes
    $patterns = @(
        # console.log(...); en su propia línea
        '(?m)^\s*console\.log\s*\([^)]*\);\s*$\r?\n',
        # console.log(...); en cualquier lugar
        '(?m)console\.log\s*\([^)]*\);\s*',
        # console.log(...) sin punto y coma
        '(?m)^\s*console\.log\s*\([^)]*\)\s*$\r?\n',
        '(?m)console\.log\s*\([^)]*\)\s*',
        # console.error, console.warn, console.info, console.debug
        '(?m)^\s*console\.(error|warn|info|debug)\s*\([^)]*\);\s*$\r?\n',
        '(?m)console\.(error|warn|info|debug)\s*\([^)]*\);\s*'
    )
    
    $matches = @()
    $hasConsole = $false
    
    foreach ($pattern in $patterns) {
        $patternMatches = [regex]::Matches($content, $pattern)
        if ($patternMatches.Count -gt 0) {
            $hasConsole = $true
            $matches += $patternMatches
        }
    }
    
    if ($hasConsole) {
        $count = $matches.Count
        $totalFound += $count
        $filesWithConsole += @{
            Path = $file.FullName
            Count = $count
            Content = $content
        }
        
        Write-Host "📄 $($file.FullName)" -ForegroundColor Yellow
        Write-Host "   ⚠️  Encontrados $count console.*" -ForegroundColor Red
        
        # Mostrar algunas líneas de ejemplo
        $lines = Get-Content $file.FullName
        $shown = 0
        for ($i = 0; $i -lt $lines.Count -and $shown -lt 3; $i++) {
            if ($lines[$i] -match 'console\.(log|error|warn|info|debug)') {
                Write-Host "   Línea $($i + 1): $($lines[$i].Trim())" -ForegroundColor Gray
                $shown++
            }
        }
        if ($count -gt 3) {
            Write-Host "   ... y $($count - 3) más" -ForegroundColor Gray
        }
        Write-Host ""
    }
}

Write-Host "`n📊 Resumen:" -ForegroundColor Cyan
Write-Host "   Archivos con console.*: $($filesWithConsole.Count)" -ForegroundColor Yellow
Write-Host "   Total console.* encontrados: $totalFound" -ForegroundColor Yellow

if (-not $DryRun -and $filesWithConsole.Count -gt 0) {
    Write-Host "`n⚠️  ADVERTENCIA: Este script modificará archivos" -ForegroundColor Red
    $confirm = Read-Host "¿Continuar? (S/N)"
    if ($confirm -ne "S" -and $confirm -ne "s") {
        Write-Host "❌ Operación cancelada" -ForegroundColor Red
        exit
    }
    
    # Crear carpeta de backup
    $backupDir = ".\backup-console-logs-js-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
    New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
    Write-Host "📦 Creando backup en: $backupDir" -ForegroundColor Cyan
    Write-Host ""
    
    $totalRemoved = 0
    $filesModified = @()
    
    foreach ($fileInfo in $filesWithConsole) {
        $filePath = $fileInfo.Path
        $content = $fileInfo.Content
        
        # Aplicar todos los patrones de eliminación
        foreach ($pattern in $patterns) {
            $content = $content -replace $pattern, ''
        }
        
        # Limpiar líneas vacías múltiples (máximo 2 consecutivas)
        $content = $content -replace '(\r?\n\s*){3,}', "`r`n`r`n"
        
        # Hacer backup
        $relativePath = $filePath.Replace((Get-Location).Path + "\", "")
        $backupPath = Join-Path $backupDir $relativePath
        $backupDirPath = Split-Path $backupPath -Parent
        if ($backupDirPath) {
            New-Item -ItemType Directory -Path $backupDirPath -Force | Out-Null
        }
        Copy-Item $filePath $backupPath -Force
        
        # Guardar archivo modificado
        Set-Content -Path $filePath -Value $content -NoNewline
        $filesModified += $filePath
        $totalRemoved += $fileInfo.Count
        Write-Host "✅ Modificado: $($fileInfo.Path)" -ForegroundColor Green
    }
    
    Write-Host "`n📊 Resumen final:" -ForegroundColor Cyan
    Write-Host "   Archivos modificados: $($filesModified.Count)" -ForegroundColor Yellow
    Write-Host "   Total console.* eliminados: $totalRemoved" -ForegroundColor Yellow
    Write-Host "   Backup guardado en: $backupDir" -ForegroundColor Green
    Write-Host "`n✅ Proceso completado!" -ForegroundColor Green
} elseif ($DryRun) {
    Write-Host "`n💡 Modo dry-run: No se modificaron archivos" -ForegroundColor Yellow
    Write-Host "   Ejecuta sin --DryRun para aplicar cambios" -ForegroundColor Cyan
}
