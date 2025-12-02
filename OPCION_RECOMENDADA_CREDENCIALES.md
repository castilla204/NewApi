# ✅ Opción Recomendada: Login Automático con gcloud

## 🎯 La Opción Más Fácil

En lugar de descargar un archivo JSON, puedes usar el comando de Google Cloud CLI que configura todo automáticamente.

## 🚀 Pasos (Muy Simples)

### Paso 1: Abrir Terminal/PowerShell

Abre PowerShell o CMD en tu máquina Windows.

### Paso 2: Ejecutar el Comando

```bash
gcloud auth application-default login
```

### Paso 3: Seguir las Instrucciones

1. El comando abrirá tu navegador automáticamente
2. Te pedirá que inicies sesión con tu cuenta de Google
3. Te pedirá permisos para acceder a Google Cloud
4. Acepta los permisos
5. ¡Listo! Las credenciales se guardan automáticamente

## ✅ Ventajas de esta Opción

1. **Más fácil**: Un solo comando
2. **Automático**: No necesitas descargar ni mover archivos
3. **Seguro**: Las credenciales se guardan en ubicación segura del sistema
4. **Actualizable**: Si cambias de cuenta, solo ejecutas el comando de nuevo

## 🔍 Dónde se Guardan las Credenciales

Windows guarda las credenciales automáticamente en:
```
C:\Users\Diego\AppData\Roaming\gcloud\application_default_credentials.json
```

Pero **NO necesitas hacer nada**, el código las encuentra automáticamente.

## ✅ Verificar que Funciona

Después de ejecutar el comando:

```bash
# Verificar que puedes acceder a Secret Manager
gcloud secrets versions access latest --secret=jwt-key --project=grup-441318
```

Si este comando funciona, la aplicación también funcionará automáticamente.

## 🚀 Ejecutar la Aplicación

Una vez configurado (una sola vez):

```bash
dotnet run
```

La aplicación detectará automáticamente las credenciales y se conectará a Google Cloud Secret Manager.

## ⚠️ Requisito Previo

Necesitas tener `gcloud` CLI instalado. Si no lo tienes:

1. **Descargar**: https://cloud.google.com/sdk/docs/install
2. **Instalar**: Ejecuta el instalador
3. **Configurar**: `gcloud init` (la primera vez)

## 🔄 Si Ya Tienes gcloud Instalado

Solo ejecuta:

```bash
gcloud auth application-default login
```

Y listo. No necesitas descargar ningún archivo JSON.

## 📊 Comparación

| Aspecto | Login Automático ✅ | Archivo JSON |
|---------|-------------------|--------------|
| Facilidad | ⭐⭐⭐⭐⭐ Muy fácil | ⭐⭐⭐ Requiere pasos |
| Automático | ✅ Sí | ❌ Manual |
| Seguridad | ✅ Seguro | ✅ Seguro |
| Mantenimiento | ✅ Fácil | ⚠️ Requiere actualizar archivo |

## 🎯 Recomendación Final

**Usa `gcloud auth application-default login`** porque:
- Es más rápido
- Es más fácil
- Se actualiza automáticamente
- No necesitas gestionar archivos

Solo necesitas ejecutarlo **una vez**, y después la aplicación funcionará automáticamente cada vez que la ejecutes.

