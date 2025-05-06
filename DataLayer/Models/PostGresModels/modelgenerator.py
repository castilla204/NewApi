# -*- coding: utf-8 -*-
import os

# Usar la carpeta donde está el script
folder_path = os.path.dirname(os.path.abspath(__file__))
output_file = "combined.cs"  # Cambiado a .cs

# Lista para almacenar el contenido
combined_content = []
files_found = []

# Imprimir la carpeta que está usando
print(f"Buscando archivos .cs en: {folder_path}")

# Recorre todos los archivos en la carpeta
for filename in sorted(os.listdir(folder_path)):
    # Solo procesa archivos .cs (excepto el archivo de salida)
    if filename.lower().endswith(".cs") and filename != output_file:
        files_found.append(filename)
        file_path = os.path.join(folder_path, filename)
        print(f"Procesando archivo: {filename}")
        try:
            # Intentar leer con UTF-8
            with open(file_path, "r", encoding="utf-8") as file:
                content = file.read()
                combined_content.append(f"// --- Contenido de {filename} ---\n{content}\n\n")
        except UnicodeDecodeError:
            # Si falla UTF-8, intentar con Windows-1252
            print(f"  Error de codificación UTF-8 en {filename}, intentando Windows-1252")
            try:
                with open(file_path, "r", encoding="windows-1252") as file:
                    content = file.read()
                    combined_content.append(f"// --- Contenido de {filename} ---\n{content}\n\n")
            except Exception as e:
                print(f"  Error al leer {filename}: {e}")
        except Exception as e:
            print(f"  Error al leer {filename}: {e}")

# Imprimir resultados
if files_found:
    print(f"Archivos .cs encontrados: {', '.join(files_found)}")
else:
    print("No se encontraron archivos .cs en la carpeta")

# Escribe el archivo combinado
if combined_content:
    try:
        with open(os.path.join(folder_path, output_file), "w", encoding="utf-8") as output:
            output.write("".join(combined_content))
        print(f"Listo! Todos los archivos .cs se combinaron en {output_file}")
    except Exception as e:
        print(f"Error al crear {output_file}: {e}")
else:
    print(f"No se escribió {output_file} porque no se encontró contenido para combinar")