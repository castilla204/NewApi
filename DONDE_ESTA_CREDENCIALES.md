# 📁 ¿Dónde está el archivo cloudcredential.json?

## 🔍 El archivo probablemente NO existe todavía

El archivo `C:\cloudcredential.json` es el archivo JSON de credenciales que descargaste desde Google Cloud Console. 

## 📥 Dónde está el archivo que descargaste

Cuando descargas credenciales desde GCP Console, normalmente se guarda en tu carpeta de **Descargas**:

### Windows:
```
C:\Users\Diego\Downloads\[nombre-del-archivo].json
```

El archivo puede tener nombres como:
- `grup-441318-xxxxx.json`
- `mi-proyecto-xxxxx.json`
- `service-account-key.json`
- O cualquier nombre que le hayas dado al descargarlo

## 🔧 Pasos para Configurarlo

### Paso 1: Encontrar el archivo descargado

1. **Abre tu carpeta de Descargas**:
   ```
   C:\Users\Diego\Downloads
   ```

2. **Busca el archivo JSON** que descargaste (tiene extensión `.json`)

3. **Verifica que es el correcto**: Debe contener algo como:
   ```json
   {
     "type": "service_account",
     "project_id": "grup-441318",
     ...
   }
   ```

### Paso 2: Copiar/Mover a la ubicación correcta

Tienes dos opciones:

#### Opción A: Copiar el archivo (Recomendado)
1. Copia el archivo JSON desde `Downloads`
2. Pégalo en `C:\`
3. **Renómbralo** a `cloudcredential.json`

#### Opción B: Mover el archivo
1. Mueve el archivo JSON desde `Downloads` a `C:\`
2. **Renómbralo** a `cloudcredential.json`

### Paso 3: Verificar

Abre PowerShell y ejecuta:

```powershell
# Verificar que existe
Test-Path C:\cloudcredential.json
# Debe devolver: True

# Ver contenido (primeras líneas)
Get-Content C:\cloudcredential.json | Select-Object -First 5
```

## 🔍 Si no encuentras el archivo

### Opción 1: Buscar en tu máquina

```powershell
# Buscar todos los archivos JSON recientes
Get-ChildItem -Path C:\Users\Diego\Downloads -Filter *.json -Recurse | Sort-Object LastWriteTime -Descending | Select-Object -First 5
```

### Opción 2: Descargar de nuevo

1. Ve a: https://console.cloud.google.com/apis/credentials
2. Selecciona tu proyecto: `grup-441318`
3. Busca la cuenta de servicio que quieres usar
4. Haz clic en "Crear clave" o "Descargar JSON"
5. Guarda el archivo

## 📝 Resumen

1. **Archivo descargado**: Probablemente en `C:\Users\Diego\Downloads\[nombre].json`
2. **Ubicación objetivo**: `C:\cloudcredential.json`
3. **Acción**: Copiar/mover y renombrar el archivo descargado a `C:\cloudcredential.json`

## ✅ Verificación Final

Una vez colocado el archivo, verifica:

```powershell
# 1. Verificar que existe
Test-Path C:\cloudcredential.json

# 2. Verificar contenido (debe tener project_id: "grup-441318")
Get-Content C:\cloudcredential.json | Select-String "grup-441318"

# 3. Probar acceso
$env:GOOGLE_APPLICATION_CREDENTIALS="C:\cloudcredential.json"
gcloud secrets versions access latest --secret=jwt-key --project=grup-441318
```

Si todos estos pasos funcionan, la aplicación podrá usar las credenciales automáticamente.

