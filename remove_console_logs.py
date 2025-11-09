#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Script para eliminar todos los console.log (JS/TS) y Console.WriteLine/Write (C#)
de archivos en el proyecto newApi.
"""

import os
import re
import sys
from pathlib import Path

# Configurar encoding para Windows
if sys.platform == 'win32':
    import codecs
    sys.stdout = codecs.getwriter('utf-8')(sys.stdout.buffer, 'strict')
    sys.stderr = codecs.getwriter('utf-8')(sys.stderr.buffer, 'strict')

# Extensiones de archivos a procesar
EXTENSIONS_JS = ['.js', '.jsx', '.ts', '.tsx']
EXTENSIONS_CS = ['.cs']
EXTENSIONS = EXTENSIONS_JS + EXTENSIONS_CS

# Patrones para detectar console.log (JavaScript/TypeScript)
JS_CONSOLE_PATTERNS = [
    r'^\s*console\.log\s*\([^)]*\)\s*;?\s*$',  # Línea completa con console.log
    r'console\.log\s*\([^)]*\)',  # console.log en cualquier lugar
]

# Patrones para detectar Console.WriteLine/Write (C#)
CS_CONSOLE_PATTERNS = [
    r'^\s*Console\.WriteLine\s*\([^)]*\)\s*;?\s*$',  # Console.WriteLine(...);
    r'^\s*Console\.Write\s*\([^)]*\)\s*;?\s*$',  # Console.Write(...);
    r'^\s*Console\.Error\.WriteLine\s*\([^)]*\)\s*;?\s*$',  # Console.Error.WriteLine(...);
    r'^\s*Console\.Error\.Write\s*\([^)]*\)\s*;?\s*$',  # Console.Error.Write(...);
    r'Console\.WriteLine\s*\([^)]*\)',  # Console.WriteLine en cualquier lugar
    r'Console\.Write\s*\([^)]*\)',  # Console.Write en cualquier lugar
]

# Directorios a excluir
EXCLUDE_DIRS = {
    'node_modules',
    '.git',
    'bin',
    'obj',
    'logs',
    'cursor-backend-logs',
    '.vs',
    '.vscode',
    '__pycache__',
    'venv',
    'env'
}

# Archivos a excluir (scripts de limpieza)
EXCLUDE_FILES = {
    'remove_console_logs.py',
    'remove-console-logs.js',
    'remove-console-logs.ps1',
    'remove-console-logs-js.ps1'
}


def should_process_file(file_path):
    """Verifica si un archivo debe ser procesado."""
    # Verificar extensión
    if not any(file_path.suffix.lower() in EXTENSIONS for ext in EXTENSIONS):
        return False
    
    # Verificar si está en directorios excluidos
    parts = file_path.parts
    for exclude_dir in EXCLUDE_DIRS:
        if exclude_dir in parts:
            return False
    
    # Verificar si el archivo está en la lista de exclusión
    if file_path.name in EXCLUDE_FILES:
        return False
    
    return True


def remove_console_logs_from_content(content, file_extension):
    """Elimina todas las líneas que contienen console.log (JS) o Console.WriteLine/Write (C#) del contenido."""
    lines = content.split('\n')
    new_lines = []
    removed_count = 0
    
    # Determinar qué patrones usar según la extensión del archivo
    if file_extension.lower() in EXTENSIONS_CS:
        patterns = CS_CONSOLE_PATTERNS
    else:
        patterns = JS_CONSOLE_PATTERNS
    
    for line in lines:
        # Verificar si la línea contiene console.log o Console.WriteLine/Write
        has_console = any(re.search(pattern, line) for pattern in patterns)
        
        if has_console:
            removed_count += 1
            continue  # Saltar esta línea
        
        new_lines.append(line)
    
    return '\n'.join(new_lines), removed_count


def process_file(file_path, dry_run=False):
    """Procesa un archivo eliminando console.log (JS) o Console.WriteLine/Write (C#)."""
    try:
        with open(file_path, 'r', encoding='utf-8', errors='ignore') as f:
            original_content = f.read()
        
        file_extension = file_path.suffix
        new_content, removed_count = remove_console_logs_from_content(original_content, file_extension)
        
        if removed_count > 0:
            if not dry_run:
                with open(file_path, 'w', encoding='utf-8', errors='ignore') as f:
                    f.write(new_content)
                return removed_count, True
            else:
                return removed_count, False
        else:
            return 0, False
    
    except Exception as e:
        print(f"[ERROR] Error procesando {file_path}: {e}")
        return 0, False


def main():
    """Función principal."""
    # Determinar si es dry-run
    dry_run = '--dry-run' in sys.argv or '-n' in sys.argv
    
    # Directorio raíz del proyecto
    project_root = Path(__file__).parent
    
    print("[*] Buscando archivos con console.log (JS/TS) y Console.WriteLine/Write (C#)...\n")
    
    files_to_process = []
    total_console_logs = 0
    files_modified = 0
    
    # Buscar todos los archivos
    for ext in EXTENSIONS:
        for file_path in project_root.rglob(f'*{ext}'):
            if should_process_file(file_path):
                files_to_process.append(file_path)
    
    print(f"[*] Encontrados {len(files_to_process)} archivos para procesar\n")
    
    # Procesar cada archivo
    for file_path in files_to_process:
        try:
            # Leer archivo para contar console.log o Console.WriteLine/Write
            with open(file_path, 'r', encoding='utf-8', errors='ignore') as f:
                content = f.read()
            
            # Contar según el tipo de archivo
            if file_path.suffix.lower() in EXTENSIONS_CS:
                count = len(re.findall(r'Console\.(WriteLine|Write)', content))
                log_type = "Console.WriteLine/Write"
            else:
                count = len(re.findall(r'console\.log', content))
                log_type = "console.log"
            
            if count > 0:
                removed, was_modified = process_file(file_path, dry_run)
                if removed > 0:
                    total_console_logs += removed
                    if was_modified:
                        files_modified += 1
                    status = "[OK]" if was_modified else "[DRY-RUN]"
                    print(f"{status} {file_path.relative_to(project_root)}: {removed} {log_type} eliminados")
        
        except Exception as e:
            print(f"[ERROR] Error en {file_path}: {e}")
    
    # Resumen
    print(f"\n{'='*60}")
    print(f"RESUMEN:")
    print(f"   Archivos procesados: {len(files_to_process)}")
    print(f"   Archivos modificados: {files_modified}")
    print(f"   Total console.log/Console.WriteLine eliminados: {total_console_logs}")
    
    if dry_run:
        print(f"\n[INFO] Modo dry-run: No se modificaron archivos")
        print(f"   Ejecuta sin --dry-run para aplicar cambios")
    else:
        print(f"\n[OK] Proceso completado!")
    
    print(f"{'='*60}\n")


if __name__ == '__main__':
    main()

