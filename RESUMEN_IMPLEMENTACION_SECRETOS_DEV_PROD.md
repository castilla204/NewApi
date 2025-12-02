# ✅ Implementación: Secretos Separados para Desarrollo y Producción

## 🎯 Objetivo Cumplido

Se ha modificado el código para que use secretos diferentes según el entorno:
- **Desarrollo**: Intenta obtener secretos con sufijo `-dev` (ej: `jwt-key-dev`)
- **Producción**: Usa secretos sin sufijo (ej: `jwt-key`)

## 🔧 Cambios Realizados

### 1. Modificación de `GetSecretValue` en Program.cs

La función ahora:
- En **desarrollo**: Intenta primero `{secretName}-dev`, si no existe usa `{secretName}`
- En **producción**: Usa directamente `{secretName}`
- Maneja correctamente errores `NotFound` para intentar el siguiente secreto

### 2. Agregado `using System.Collections.Generic;`

Necesario para usar `List<string>` en la función modificada.

## 📋 Próximos Pasos

### Paso 1: Crear Secretos de Desarrollo en GCSM

Ejecuta el script para crear los secretos con sufijo `-dev`:

```bash
# Desde tu máquina local (con gcloud configurado)
cd /root/newapi
./crear-secretos-desarrollo.sh
```

O manualmente:

```bash
# Crear secretos de desarrollo
gcloud secrets create jwt-key-dev --project=grup-441318
gcloud secrets create jwt-issuer-dev --project=grup-441318
gcloud secrets create jwt-audience-dev --project=grup-441318
# ... etc para todos los secretos necesarios

# Agregar valores a los secretos
echo "tu_jwt_key_desarrollo" | gcloud secrets versions add jwt-key-dev \
  --data-file=- --project=grup-441318
```

### Paso 2: Configurar Credenciales de Google Cloud en Desarrollo

Para que la aplicación pueda acceder a GCSM en desarrollo local:

1. **Opción A: Archivo de credenciales**
   - Descarga el archivo JSON de credenciales desde GCP Console
   - Colócalo en `C:\cloudcredential.json` (o la ruta que uses)
   - El código ya tiene fallback a esta ubicación en desarrollo

2. **Opción B: Variable de entorno**
   ```powershell
   # En PowerShell
   $env:GOOGLE_APPLICATION_CREDENTIALS="C:\ruta\a\tu\cloudcredential.json"
   ```

3. **Opción C: gcloud auth application-default**
   ```bash
   gcloud auth application-default login
   ```

### Paso 3: Probar en Desarrollo

1. Ejecuta la aplicación en modo desarrollo
2. Verifica en los logs que veas:
   ```
   🔧 DESARROLLO: Intentando secretos: jwt-key-dev -> jwt-key
   ✅ Secreto jwt-key-dev obtenido exitosamente en XXms
   ```
3. Si `jwt-key-dev` no existe, debería intentar `jwt-key` automáticamente

### Paso 4: Verificar Producción

En producción (Kubernetes), debería seguir funcionando igual:
- Usa secretos sin sufijo (los actuales)
- Los logs mostrarán: `🏭 PRODUCCIÓN: Usando secreto: jwt-key`

## 🔍 Verificación

### En Desarrollo Local

```bash
# Ver logs de la aplicación
# Deberías ver:
# 🔧 DESARROLLO: Intentando secretos: jwt-key-dev -> jwt-key
# ✅ Secreto jwt-key-dev obtenido exitosamente...
```

### En Producción (Kubernetes)

```bash
# Ver logs del pod
kubectl logs deployment/new-api -n default | grep "PRODUCCIÓN\|DESARROLLO"

# Deberías ver:
# 🏭 PRODUCCIÓN: Usando secreto: jwt-key
```

## 📚 Secretos a Crear

Lista completa de secretos que deberías crear con sufijo `-dev`:

- `jwt-key-dev`
- `jwt-issuer-dev`
- `jwt-audience-dev`
- `postgres-host-dev`
- `postgres-port-dev`
- `postgres-username-dev`
- `postgres-password-dev`
- `postgres-database-dev`
- `rabbitmq-password-dev`
- `openai-api-key-dev`
- `google-client-ids-dev`
- `email-from-email-dev`
- `email-from-name-dev`
- `email-smtp-host-dev`
- `email-smtp-port-dev`
- `email-smtp-username-dev`
- `email-smtp-password-dev`
- `stripe-secret-key-dev`
- `stripe-webhook-secret-dev`
- `stripe-general-webhook-secret-dev`
- `twilio-account-sid-dev`
- `twilio-auth-token-dev`
- `twilio-verification-service-sid-dev`

## ✅ Ventajas de esta Implementación

1. **Separación clara**: Secretos diferentes para dev/prod
2. **Compatibilidad**: Si no existe `-dev`, usa el secreto normal (fallback)
3. **Migración gradual**: Puedes crear secretos `-dev` poco a poco
4. **Seguridad**: No mezclas secretos de producción en desarrollo
5. **Logs claros**: Fácil ver qué secreto se está usando

## 🚨 Importante

- Los secretos de desarrollo pueden tener valores de prueba/dummy
- Los secretos de producción deben ser los valores reales
- No uses secretos de producción en desarrollo (a menos que sea necesario para pruebas específicas)

## 📝 Documentación Relacionada

- `/root/newapi/MODIFICACION_GETSECRETVALUE.md` - Detalles técnicos de la modificación
- `/root/newapi/crear-secretos-desarrollo.sh` - Script para crear secretos
- `/root/EXPLICACION_SECRETOS_GCP_K8S.md` - Documentación general de secretos

---

**Estado**: ✅ Código modificado y listo para usar
**Próximo paso**: Crear secretos `-dev` en Google Cloud Secret Manager

