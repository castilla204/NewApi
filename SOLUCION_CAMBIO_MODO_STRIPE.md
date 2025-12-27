# 🔧 Solución: Cambio de Modo Stripe (Desarrollo/Producción)

## 📋 Problema Identificado

Cuando se cambiaba el modo Stripe desde el panel de administración:
1. ✅ El modo se guardaba correctamente en la base de datos
2. ❌ Las claves Stripe NO se recargaban dinámicamente
3. ❌ Las URLs de webhooks NO se actualizaban (deben configurarse manualmente en Stripe Dashboard)

## ✅ Solución Implementada

### 1. Endpoints Agregados en AdminController

Se agregaron tres endpoints para gestionar el modo Stripe:

- **GET `/api/Admin/stripe/mode`**: Obtener el modo actual
- **POST `/api/Admin/stripe/mode`**: Establecer modo específico
- **POST `/api/Admin/stripe/toggle-mode`**: Alternar automáticamente entre development/production

### 2. Recarga Dinámica de Claves

Cuando se cambia el modo:
1. Se actualiza el modo en la base de datos
2. Se recargan las claves Stripe desde Secret Manager según el modo:
   - **Development**: Busca `stripe-secret-key-dev`, `stripe-webhook-secret-dev`, etc.
   - **Production**: Busca `stripe-secret-key`, `stripe-webhook-secret`, etc.
3. Se actualiza `IConfiguration` y `StripeConfiguration.ApiKey` en memoria

### 3. SubscriptionController Actualizado

`SubscriptionController` ahora lee las claves **dinámicamente** desde `IConfiguration` en lugar de guardarlas en campos readonly. Esto permite que:
- Las claves se actualicen sin reiniciar la aplicación
- Cada request use las claves correctas según el modo actual

## ⚠️ IMPORTANTE: URLs de Webhooks

**Las URLs de webhooks NO se actualizan automáticamente**. Debes configurarlas manualmente en Stripe Dashboard:

### Para Modo Development (Test):
1. Ve a: https://dashboard.stripe.com/test/webhooks
2. Crea o actualiza el endpoint con la URL de desarrollo (ej: `https://tu-ngrok-url.ngrok.io/api/Subscription/webhook`)
3. Copia el **Signing Secret** (empieza con `whsec_test_...`)
4. Guárdalo en Google Cloud Secret Manager como: `stripe-webhook-secret-dev`

### Para Modo Production (Live):
1. Ve a: https://dashboard.stripe.com/webhooks
2. Crea o actualiza el endpoint con la URL de producción (ej: `https://inspecciono.com/api/Subscription/webhook`)
3. Copia el **Signing Secret** (empieza con `whsec_live_...`)
4. Guárdalo en Google Cloud Secret Manager como: `stripe-webhook-secret`

## 🔑 Secretos Requeridos en Google Cloud Secret Manager

### Modo Development:
- `stripe-secret-key-dev` (clave secreta de test)
- `stripe-webhook-secret-dev` (signing secret del webhook de test)
- `stripe-general-webhook-secret-dev` (signing secret del webhook general de test)

### Modo Production:
- `stripe-secret-key` (clave secreta de live)
- `stripe-webhook-secret` (signing secret del webhook de live)
- `stripe-general-webhook-secret` (signing secret del webhook general de live)

## 📝 Cómo Usar

### Desde el Panel de Administración:

1. **Obtener modo actual:**
   ```http
   GET /api/Admin/stripe/mode
   Authorization: Bearer {token}
   ```

2. **Cambiar a modo específico:**
   ```http
   POST /api/Admin/stripe/mode
   Authorization: Bearer {token}
   Content-Type: application/json
   
   {
     "Mode": "production"
   }
   ```

3. **Alternar automáticamente:**
   ```http
   POST /api/Admin/stripe/toggle-mode
   Authorization: Bearer {token}
   ```

### Respuesta de Ejemplo:

```json
{
  "success": true,
  "message": "Modo Stripe cambiado de production a development",
  "previousMode": "production",
  "newMode": "development",
  "warning": "Las claves Stripe se han recargado. Las URLs de webhooks en Stripe Dashboard deben configurarse manualmente para cada modo."
}
```

## ✅ Verificación

Después de cambiar el modo, verifica en los logs:

```
✅ Modo Stripe cambiado a: development por usuario 36 - Claves recargadas
✅ Claves Stripe recargadas para modo development
   SecretKey presente: True
   WebhookSecret presente: True
   GeneralWebhookSecret presente: True
```

## 🔄 Flujo Completo

1. **Usuario cambia modo** desde panel de administración
2. **Backend actualiza** el modo en `SystemSettings`
3. **Backend recarga** las claves desde Secret Manager según el modo
4. **Backend actualiza** `IConfiguration` y `StripeConfiguration.ApiKey`
5. **Próximos requests** usan las nuevas claves automáticamente
6. **⚠️ IMPORTANTE**: Configurar URLs de webhooks manualmente en Stripe Dashboard

## 🚨 Notas Importantes

1. **Las claves se recargan dinámicamente** - No es necesario reiniciar la aplicación
2. **Las URLs de webhooks deben configurarse manualmente** en Stripe Dashboard para cada modo
3. **Cada modo tiene sus propios secretos** - Asegúrate de tener ambos configurados en Secret Manager
4. **Los webhook secrets son diferentes** entre test y live - No los mezcles

