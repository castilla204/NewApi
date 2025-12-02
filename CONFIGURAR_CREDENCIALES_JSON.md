# 🔧 Configurar Credenciales JSON para Desarrollo

## 📁 Ubicación del Archivo

Coloca el archivo JSON de credenciales en:

```
C:\cloudcredential.json
```

**Importante**: 
- El archivo debe llamarse exactamente `cloudcredential.json`
- Debe estar en la raíz de `C:\`
- No debe estar en una subcarpeta

## ✅ Pasos

### Paso 1: Colocar el Archivo

1. **Mover el archivo JSON** que descargaste a `C:\cloudcredential.json`
   - Si el archivo tiene otro nombre (ej: `mi-proyecto-12345.json`), renómbralo a `cloudcredential.json`
   - Si está en otra ubicación, muévelo a `C:\`

### Paso 2: Verificar que el Archivo Existe

```powershell
# En PowerShell
Test-Path C:\cloudcredential.json
# Debe devolver: True
```

### Paso 3: Verificar Contenido (Opcional)

El archivo debe contener algo como:
```json
{
  "type": "service_account",
  "project_id": "grup-441318",
  "private_key_id": "...",
  "private_key": "...",
  "client_email": "...",
  ...
}
```

### Paso 4: Probar que Funciona

```bash
# Configurar la variable de entorno temporalmente para esta sesión
$env:GOOGLE_APPLICATION_CREDENTIALS="C:\cloudcredential.json"

# Probar acceso a Secret Manager
gcloud secrets versions access latest --secret=jwt-key --project=grup-441318
```

Si este comando funciona, la aplicación también funcionará.

## 🚀 Ejecutar la Aplicación

Una vez colocado el archivo en `C:\cloudcredential.json`:

```bash
# Desde la carpeta del proyecto
dotnet run
```

La aplicación debería:
1. Detectar que estás en desarrollo
2. Buscar credenciales en `C:\cloudcredential.json`
3. Conectarse a Google Cloud Secret Manager
4. Obtener los secretos automáticamente

## 🔍 Verificar en los Logs

Al ejecutar la aplicación, deberías ver en la consola:

```
=== INICIALIZANDO SECRET MANAGER ===
Entorno: Development
GOOGLE_APPLICATION_CREDENTIALS: NO CONFIGURADO
Ruta de credenciales a usar: C:\cloudcredential.json
Archivo de credenciales existe: true
✅ Secret Manager configurado correctamente desde: C:\cloudcredential.json
🔧 DESARROLLO: Intentando secretos: jwt-key-dev -> jwt-key
✅ Secreto jwt-key obtenido exitosamente en XXms
```

## ⚠️ Si No Funciona

### Error: "Archivo de credenciales no existe"

**Causa**: El archivo no está en `C:\cloudcredential.json`

**Solución**:
- Verifica la ruta exacta: `C:\cloudcredential.json` (no `C:\carpeta\cloudcredential.json`)
- Verifica el nombre: debe ser exactamente `cloudcredential.json` (no `cloudcredential.json.txt`)

### Error: "Secret Manager no está disponible"

**Causa**: Las credenciales no tienen permisos o son inválidas

**Solución**:
```bash
# Verificar que el archivo es válido
$env:GOOGLE_APPLICATION_CREDENTIALS="C:\cloudcredential.json"
gcloud auth activate-service-account --key-file=C:\cloudcredential.json
gcloud secrets list --project=grup-441318
```

### Error: "JWT Key not found"

**Causa**: Secret Manager no está disponible o no puede conectarse

**Solución**:
- Verifica que el archivo JSON es válido
- Verifica que la cuenta de servicio tiene permisos en Secret Manager
- Verifica tu conexión a internet

## 🔐 Alternativa: Variable de Entorno

Si prefieres no usar `C:\cloudcredential.json`, puedes configurar la variable de entorno:

```powershell
# Temporal (solo para esta sesión)
$env:GOOGLE_APPLICATION_CREDENTIALS="C:\ruta\a\tu\archivo.json"

# Permanente (para todas las sesiones)
[System.Environment]::SetEnvironmentVariable('GOOGLE_APPLICATION_CREDENTIALS', 'C:\ruta\a\tu\archivo.json', 'User')
```

## ✅ Checklist

- [ ] Archivo JSON descargado de GCP Console
- [ ] Archivo colocado en `C:\cloudcredential.json`
- [ ] Verificado que el archivo existe: `Test-Path C:\cloudcredential.json`
- [ ] Probado acceso: `gcloud secrets versions access latest --secret=jwt-key --project=grup-441318`
- [ ] Ejecutado `dotnet run` y verificado logs

Si todos los pasos funcionan, la aplicación debería obtener los secretos automáticamente.

