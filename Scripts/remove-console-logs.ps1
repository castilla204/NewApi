# Script para eliminar todos los Console.WriteLine, Console.Write, Console.Error de archivos .cs
# Mantiene los scripts de migración si es necesario

Write-Host "🔍 Buscando todos los Console.* en archivos .cs..." -ForegroundColor Cyan

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
    
    # Buscar Console.WriteLine, Console.Write, Console.Error, etc.
    $consolePatterns = @(
        'Console\.WriteLine\s*\([^)]*\);?\s*',
        'Console\.Write\s*\([^)]*\);?\s*',
        'Console\.Error\s*\([^)]*\);?\s*',
        'Console\.Out\s*\([^)]*\);?\s*'
    )
    
    $hasConsole = $false
    foreach ($pattern in $consolePatterns) {
        if ($content -match $pattern) {
            $hasConsole = $true
            break
        }
    }
    
    if ($hasConsole) {
        Write-Host "📄 Encontrado en: $($file.FullName)" -ForegroundColor Yellow
        
        # Contar cuántos hay
        $matches = [regex]::Matches($content, 'Console\.(WriteLine|Write|Error|Out)')
        $count = $matches.Count
        Write-Host "   ⚠️  Encontrados $count Console.*" -ForegroundColor Red
        
        # Mostrar las líneas encontradas
        $lines = Get-Content $file.FullName
        $lineNumber = 1
        foreach ($line in $lines) {
            if ($line -match 'Console\.(WriteLine|Write|Error|Out)') {
                Write-Host "   Línea $lineNumber : $line" -ForegroundColor Gray
            }
            $lineNumber++
        }
        
        Write-Host ""
    }
}

Write-Host "`n✅ Búsqueda completada. Revisa los archivos encontrados arriba." -ForegroundColor Green
Write-Host "`n💡 Para eliminar automáticamente, ejecuta: .\Scripts\remove-console-logs-auto.ps1" -ForegroundColor Cyan

