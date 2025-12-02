# ✅ Requisitos para Ejecutar en Desarrollo con Google Cloud Secret Manager

## 🎯 Respuesta Corta

**SÍ**, debería funcionar automáticamente, PERO necesitas tener configuradas las credenciales de Google Cloud.

## 📋 Requisitos

### 1. Credenciales de Google Cloud

El código busca credenciales en este orden:

1. **Variable de entorno**: `GOOGLE_APPLICATION_CREDENTIALS`
2. **Archivo local** (solo en desarrollo): `C:\cloudcredential.json`

### 2. Opciones para Configurar Credenciales

#### Opción A: Login Automático (Más Fácil) ✅

```bash
# Desde tu máquina local (Windows)
gcloud auth application-default login
```

Esto configura automáticamente las credenciales para que las aplicaciones las usen.

#### Opción B: Archivo JSON de Credenciales

1. **Descargar desde GCP Console**:
   - Ve a: https://console.cloud.google.com/apis/credentials
   - Crea una "Cuenta de servicio" o usa una existente
   - Descarga el archivo JSON de credenciales

2. **Colocar el archivo**:
   - Colócalo en: `C:\cloudcredential.json`
   - O en cualquier ubicación y configura la variable de entorno:
     ```powershell
     $env:GOOGLE_APPLICATION_CREDENTIALS="C:\ruta\a\tu\credenciales.json"
     ```

#### Opción C: Usar Credenciales Existentes del Servidor

Si ya tienes credenciales en el servidor (`/root/cloudcredential.json`), puedes copiarlas a tu máquina local.

## 🔍 Verificar que Funciona

### Paso 1: Verificar Credenciales

```bash
# Verificar que gcloud está configurado
gcloud auth list

# Verificar que puedes acceder a Secret Manager
gcloud secrets list --project=grup-441318

# Probar obtener un secreto
gcloud secrets versions access latest --secret=jwt-key --project=grup-441318
```

Si estos comandos funcionan, la aplicación también debería funcionar.

### Paso 2: Ejecutar la Aplicación

```bash
# Desde la carpeta del proyecto
dotnet run
```

### Paso 3: Verificar Logs

Deberías ver en la consola:

```
=== INICIALIZANDO SECRET MANAGER ===
Entorno: Development
GOOGLE_APPLICATION_CREDENTIALS: [ruta o NO CONFIGURADO]
Archivo de credenciales existe: true/false
✅ Secret Manager configurado correctamente desde: [ruta]
🔧 DESARROLLO: Intentando secretos: jwt-key-dev -> jwt-key
✅ Secreto jwt-key obtenido exitosamente en XXms
```

## ❌ Si No Funciona

### Error: "Secret Manager no está disponible"

**Causas posibles:**
1. No tienes credenciales configuradas
2. El archivo de credenciales no existe o está en ruta incorrecta
3. Las credenciales no tienen permisos para acceder a Secret Manager

**Solución:**
```bash
# Configurar credenciales
gcloud auth application-default login

# Verificar permisos
gcloud projects get-iam-policy grup-441318
```

### Error: "JWT Key not found"

**Causas posibles:**
1. Secret Manager no está disponible (ver arriba)
2. No hay variables de entorno como fallback
3. El secreto no existe en GCSM

**Solución:**
- Configurar credenciales (ver arriba)
- O crear archivo `.env` con `JWT_KEY` como fallback

## 🚀 Solución Rápida (Recomendada)

```bash
# 1. Configurar credenciales (una sola vez)
gcloud auth application-default login

# 2. Verificar que funciona
gcloud secrets versions access latest --secret=jwt-key --project=grup-441318

# 3. Ejecutar aplicación
dotnet run
```

Si el paso 2 funciona, el paso 3 también debería funcionar automáticamente.

## 📝 Notas Importantes

1. **Una vez configurado**: Las credenciales se guardan y no necesitas volver a configurarlas
2. **Seguridad**: Las credenciales se almacenan localmente en tu máquina
3. **Permisos**: Asegúrate de que las credenciales tengan acceso a Secret Manager en el proyecto `grup-441318`

## ✅ Checklist

- [ ] `gcloud` instalado y configurado
- [ ] Credenciales configuradas (`gcloud auth application-default login`)
- [ ] Puedes listar secretos: `gcloud secrets list --project=grup-441318`
- [ ] Puedes obtener un secreto: `gcloud secrets versions access latest --secret=jwt-key --project=grup-441318`
- [ ] Ejecutar `dotnet run` y verificar logs

Si todos los pasos funcionan, la aplicación debería obtener los secretos automáticamente de Google Cloud Secret Manager.

