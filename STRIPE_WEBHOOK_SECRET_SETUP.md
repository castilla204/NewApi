# 🔐 **CONFIGURACIÓN DE WEBHOOK SECRET DE STRIPE**

## 📋 **PROBLEMA**

El error `"Webhook secret not configured"` ocurre cuando Stripe intenta enviar webhooks a tu aplicación pero no encuentra el secret necesario para validar la firma.

## ✅ **SOLUCIÓN: Obtener y Configurar el Webhook Secret**

### **Paso 1: Obtener el Webhook Secret desde Stripe Dashboard**

1. **Ve al Dashboard de Stripe:**
   - https://dashboard.stripe.com/test/webhooks (modo test)
   - https://dashboard.stripe.com/webhooks (modo producción)

2. **Encuentra tu endpoint de webhook:**
   - Busca el endpoint que apunta a tu URL (ej: `https://tu-ngrok-url.ngrok.io/api/Subscription/webhook`)
   - Si no existe, créalo:
     - Click en **"Add endpoint"**
     - URL: `https://tu-ngrok-url.ngrok.io/api/Subscription/webhook`
     - Eventos a escuchar: Selecciona los eventos que necesitas (ej: `account.updated`, `account.application.authorized`, etc.)

3. **Obtén el Signing Secret:**
   - Click en tu endpoint de webhook
   - En la sección **"Signing secret"**, click en **"Reveal"** o **"Click to reveal"**
   - Copia el secret (empieza con `whsec_...`)

### **Paso 2: Configurar el Secret en tu Aplicación**

Tienes **3 opciones** para configurar el secret:

#### **Opción A: User Secrets (Recomendado para Desarrollo)**

```bash
dotnet user-secrets set "Stripe:WebhookSecret" "whsec_tu_secret_aqui"
```

Para el webhook general (si lo usas):
```bash
dotnet user-secrets set "Stripe:GeneralWebhookSecret" "whsec_tu_secret_general_aqui"
```

#### **Opción B: Variable de Entorno**

**Windows (PowerShell):**
```powershell
$env:STRIPE_WEBHOOK_SECRET="whsec_tu_secret_aqui"
$env:STRIPE_GENERAL_WEBHOOK_SECRET="whsec_tu_secret_general_aqui"
```

**Windows (CMD):**
```cmd
set STRIPE_WEBHOOK_SECRET=whsec_tu_secret_aqui
set STRIPE_GENERAL_WEBHOOK_SECRET=whsec_tu_secret_general_aqui
```

**Linux/Mac:**
```bash
export STRIPE_WEBHOOK_SECRET="whsec_tu_secret_aqui"
export STRIPE_GENERAL_WEBHOOK_SECRET="whsec_tu_secret_general_aqui"
```

#### **Opción C: appsettings.json (NO RECOMENDADO - Solo para Desarrollo)**

```json
{
  "Stripe": {
    "WebhookSecret": "whsec_tu_secret_aqui",
    "GeneralWebhookSecret": "whsec_tu_secret_general_aqui"
  }
}
```

⚠️ **ADVERTENCIA:** No commitees `appsettings.json` con secrets reales. Usa `appsettings.Development.json` y agrégalo a `.gitignore`.

### **Paso 3: Reiniciar la Aplicación**

Después de configurar el secret, **reinicia tu aplicación** para que cargue la nueva configuración.

## 🔍 **VERIFICACIÓN**

Una vez configurado, cuando Stripe envíe un webhook, deberías ver en los logs:

```
✅ Webhook event received: account.updated
✅ Webhook signature validated successfully
```

En lugar de:

```
❌ Webhook secret not configured
```

## 📝 **NOTAS IMPORTANTES**

1. **Cada endpoint tiene su propio secret único:**
   - Si cambias la URL de ngrok, necesitas crear un nuevo endpoint en Stripe y obtener su nuevo secret
   - El secret de desarrollo es diferente al de producción

2. **Webhook Secret vs General Webhook Secret:**
   - **`WebhookSecret`**: Para eventos de Stripe Connect (cuentas conectadas)
   - **`GeneralWebhookSecret`**: Para eventos generales (pagos, suscripciones, etc.)
   - Si solo tienes un endpoint, puedes usar el mismo secret para ambos

3. **Modo Test vs Producción:**
   - En modo test: Usa el secret del endpoint de test (`whsec_test_...`)
   - En producción: Usa el secret del endpoint de producción (`whsec_live_...`)

4. **Seguridad:**
   - **NUNCA** hardcodees el webhook secret en el código
   - **NUNCA** commitees secrets a Git
   - Usa User Secrets o variables de entorno en desarrollo
   - En producción, usa Google Cloud Secret Manager (ya configurado)

## 🚨 **TROUBLESHOOTING**

### Error: "Webhook secret not configured"

**Causa:** El secret no está configurado o no se está cargando correctamente.

**Solución:**
1. Verifica que configuraste el secret correctamente (User Secrets o variable de entorno)
2. Reinicia la aplicación
3. Verifica que el secret empieza con `whsec_`
4. Verifica que estás usando el secret correcto (test vs producción)

### Error: "Invalid webhook signature"

**Causa:** El secret no coincide con el endpoint de Stripe.

**Solución:**
1. Verifica que estás usando el secret del endpoint correcto
2. Si cambiaste la URL de ngrok, obtén el nuevo secret del nuevo endpoint
3. Asegúrate de que el secret no tiene espacios extra al copiarlo

### El webhook funciona pero luego deja de funcionar

**Causa:** Probablemente cambiaste la URL de ngrok.

**Solución:**
1. Obtén el nuevo secret del nuevo endpoint en Stripe
2. Actualiza la configuración con el nuevo secret
3. Reinicia la aplicación

## 📚 **REFERENCIAS**

- [Stripe Webhooks Documentation](https://stripe.com/docs/webhooks)
- [Stripe Webhook Signing Secrets](https://stripe.com/docs/webhooks/signatures)
- [ASP.NET Core User Secrets](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets)

