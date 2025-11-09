#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Script para eliminar todo lo relacionado con ILogger y _logger de archivos C#.
Elimina:
- Declaraciones de campo: private readonly ILogger<...> _logger;
- Parámetros en constructores: ILogger<...> logger
- Asignaciones: _logger = logger;
- Todas las llamadas: _logger.LogInformation(...), _logger.LogError(...), etc.
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
EXTENSIONS = ['.cs']

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
    'env',
    'Migrations'
}

# Archivos a excluir
EXCLUDE_FILES = {
    'remove_ilogger.py',
    'remove_console_logs.py',
    'ILogger.cs'  # Archivo de interfaz del sistema
}


def should_process_file(file_path):
    """Verifica si un archivo debe ser procesado."""
    # Verificar extensión
    if file_path.suffix.lower() not in EXTENSIONS:
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


def remove_ilogger_from_content(content):
    """Elimina todo lo relacionado con ILogger y _logger del contenido."""
    removed_count = 0
    
    # Contar y eliminar declaraciones de campo
    matches = re.findall(r'^\s*private\s+readonly\s+ILogger<[^>]+>\s+_logger\s*;', content, flags=re.MULTILINE)
    removed_count += len(matches)
    content = re.sub(r'^\s*private\s+readonly\s+ILogger<[^>]+>\s+_logger\s*;\s*\n', '', content, flags=re.MULTILINE)
    
    # Contar y eliminar asignaciones _logger = logger;
    matches = re.findall(r'^\s*_logger\s*=\s*\w*logger\w*\s*;', content, flags=re.MULTILINE)
    removed_count += len(matches)
    content = re.sub(r'^\s*_logger\s*=\s*\w*logger\w*\s*;\s*\n', '', content, flags=re.MULTILINE)
    
    # Contar y eliminar líneas completas con llamadas a _logger.Log* (una línea)
    matches = re.findall(r'^\s*_logger\.(Log|LogInformation|LogError|LogWarning|LogDebug|LogTrace|LogCritical)\s*\([^)]*\)\s*;', content, flags=re.MULTILINE)
    removed_count += len(matches)
    content = re.sub(r'^\s*_logger\.(Log|LogInformation|LogError|LogWarning|LogDebug|LogTrace|LogCritical)\s*\([^)]*\)\s*;\s*\n', '', content, flags=re.MULTILINE)
    
    # Manejar llamadas multilínea a _logger (más complejo)
    lines = content.split('\n')
    new_lines = []
    skip_until_semicolon = False
    paren_count = 0
    in_multiline_comment = False
    
    i = 0
    while i < len(lines):
        line = lines[i]
        original_line = line
        
        # Detectar comentarios multilínea
        if '/*' in line:
            in_multiline_comment = True
        if '*/' in line:
            in_multiline_comment = False
        
        # Saltar si estamos en un comentario multilínea
        if in_multiline_comment:
            new_lines.append(line)
            i += 1
            continue
        
        # Saltar comentarios de una línea
        stripped = line.strip()
        if stripped.startswith('//') or (stripped.startswith('*') and not stripped.startswith('**')):
            new_lines.append(line)
            i += 1
            continue
        
        # Si estamos saltando una llamada multilínea
        if skip_until_semicolon:
            # Contar paréntesis para saber cuándo termina
            paren_count += line.count('(') - line.count(')')
            if ';' in line and paren_count <= 0:
                skip_until_semicolon = False
                paren_count = 0
                removed_count += 1
            i += 1
            continue
        
        # Detectar inicio de llamada _logger.Log* que puede ser multilínea
        if re.search(r'_logger\.(Log|LogInformation|LogError|LogWarning|LogDebug|LogTrace|LogCritical)\s*\(', line):
            # Verificar si termina en la misma línea
            if ';' in line and line.count('(') == line.count(')'):
                # Línea completa, eliminarla
                removed_count += 1
                i += 1
                continue
            else:
                # Multilínea, saltar hasta el punto y coma
                paren_count = line.count('(') - line.count(')')
                skip_until_semicolon = True
                i += 1
                continue
        
        # Eliminar parámetros ILogger en constructores
        if re.search(r'ILogger<[^>]+>', line):
            # Caso 1: Parámetro al final antes de ): , ILogger<...> logger)
            line = re.sub(r',\s*ILogger<[^>]+>\s+\w*logger\w*\s*\)', ')', line)
            # Caso 2: Parámetro en medio: , ILogger<...> logger,
            line = re.sub(r',\s*ILogger<[^>]+>\s+\w*logger\w*\s*,', ',', line)
            # Caso 3: Parámetro al inicio: ILogger<...> logger,
            line = re.sub(r'^\s*ILogger<[^>]+>\s+\w*logger\w*\s*,', '', line)
            # Caso 4: Único parámetro: ILogger<...> logger)
            line = re.sub(r'ILogger<[^>]+>\s+\w*logger\w*\s*\)', ')', line)
            if line != original_line:
                removed_count += 1
        
        new_lines.append(line)
        i += 1
    
    return '\n'.join(new_lines), removed_count


def clean_empty_lines(content):
    """Limpia líneas vacías múltiples (máximo 2 consecutivas)."""
    lines = content.split('\n')
    new_lines = []
    empty_count = 0
    
    for line in lines:
        if line.strip() == '':
            empty_count += 1
            if empty_count <= 2:
                new_lines.append(line)
        else:
            empty_count = 0
            new_lines.append(line)
    
    return '\n'.join(new_lines)


def clean_trailing_commas(content):
    """Limpia comas sobrantes al final de parámetros en constructores y métodos."""
    lines = content.split('\n')
    new_lines = []
    
    for i, line in enumerate(lines):
        # Si la línea tiene solo espacios y una coma antes de )
        if re.match(r'^\s*,\s*\)', line):
            # Eliminar la coma, mantener la indentación
            indent = len(line) - len(line.lstrip())
            line = ' ' * indent + ')'
        # Si la línea anterior termina con coma y esta línea solo tiene )
        elif i > 0 and len(new_lines) > 0:
            prev_line = new_lines[-1]
            if re.search(r',\s*$', prev_line) and re.match(r'^\s*\)', line):
                # Eliminar la coma de la línea anterior
                new_lines[-1] = re.sub(r',\s*$', '', prev_line)
        
        new_lines.append(line)
    
    # Segunda pasada: eliminar comas al final de líneas que están seguidas de )
    result_lines = []
    for i, line in enumerate(new_lines):
        # Si la línea termina con coma y la siguiente es solo )
        if i < len(new_lines) - 1:
            next_line = new_lines[i + 1]
            if re.search(r',\s*$', line) and re.match(r'^\s*\)', next_line):
                # Eliminar la coma
                line = re.sub(r',\s*$', '', line)
        result_lines.append(line)
    
    return '\n'.join(result_lines)


def process_file(file_path, dry_run=False):
    """Procesa un archivo eliminando ILogger y _logger."""
    try:
        with open(file_path, 'r', encoding='utf-8', errors='ignore') as f:
            original_content = f.read()
        
        new_content, removed_count = remove_ilogger_from_content(original_content)
        
        # Limpiar comas sobrantes
        new_content = clean_trailing_commas(new_content)
        
        # Limpiar líneas vacías múltiples
        new_content = clean_empty_lines(new_content)
        
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
    
    print("[*] Buscando archivos con ILogger y _logger...\n")
    
    files_to_process = []
    total_removed = 0
    files_modified = 0
    
    # Buscar todos los archivos .cs
    for ext in EXTENSIONS:
        for file_path in project_root.rglob(f'*{ext}'):
            if should_process_file(file_path):
                files_to_process.append(file_path)
    
    print(f"[*] Encontrados {len(files_to_process)} archivos para procesar\n")
    
    # Procesar cada archivo
    for file_path in files_to_process:
        try:
            # Leer archivo para contar ILogger/_logger
            with open(file_path, 'r', encoding='utf-8', errors='ignore') as f:
                content = f.read()
            
            # Contar referencias
            ilogger_count = len(re.findall(r'ILogger<[^>]+>', content))
            logger_field_count = len(re.findall(r'private\s+readonly\s+ILogger<[^>]+>\s+_logger', content))
            logger_usage_count = len(re.findall(r'_logger\.(Log|LogInformation|LogError|LogWarning|LogDebug|LogTrace|LogCritical)', content))
            
            total_refs = ilogger_count + logger_usage_count
            
            if total_refs > 0:
                removed, was_modified = process_file(file_path, dry_run)
                if removed > 0:
                    total_removed += removed
                    if was_modified:
                        files_modified += 1
                    status = "[OK]" if was_modified else "[DRY-RUN]"
                    print(f"{status} {file_path.relative_to(project_root)}: {removed} referencias eliminadas (ILogger: {ilogger_count}, _logger: {logger_usage_count})")
        
        except Exception as e:
            print(f"[ERROR] Error en {file_path}: {e}")
    
    # Resumen
    print(f"\n{'='*60}")
    print(f"RESUMEN:")
    print(f"   Archivos procesados: {len(files_to_process)}")
    print(f"   Archivos modificados: {files_modified}")
    print(f"   Total referencias ILogger/_logger eliminadas: {total_removed}")
    
    if dry_run:
        print(f"\n[INFO] Modo dry-run: No se modificaron archivos")
        print(f"   Ejecuta sin --dry-run para aplicar cambios")
    else:
        print(f"\n[OK] Proceso completado!")
    
    print(f"{'='*60}\n")


if __name__ == '__main__':
    main()

