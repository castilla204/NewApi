# 🔧 Solución: Error de Versión de API de Stripe

## 🚨 Problema

```
Received event with API version 2024-12-18.acacia, but Stripe.net 50.0.0 expects API version 2025-11-17.clover
```

**Causa:** El webhook endpoint en Stripe Dashboard está configurado con una versión de API diferente a la que espera el SDK de Stripe.NET.

---

## ✅ Solución Aplicada (Temporal)

Se actualizó el código para **permitir diferentes versiones de API** con advertencias en los logs:

```csharp
var stripeEvent = EventUtility.ConstructEvent(
    json, 
    signatureHeader, 
    webhookSecret,
    throwOnApiVersionMismatch: false // ⚠️ Permite procesar eventos de diferentes versiones
);
```

**✅ Ventajas:**
- El webhook funciona inmediatamente sin errores
- La validación de signature sigue siendo segura
- Se registra una advertencia en los logs para actualizar después

**⚠️ Desventajas:**
- Puede haber pequeñas diferencias en la estructura de objetos entre versiones
- Se recomienda actualizar el webhook endpoint para usar la versión correcta

---

## 🎯 Solución Recomendada (Permanente)

### Actualizar Versión de API en Stripe Dashboard

1. **Ir a Stripe Dashboard:**
   - https://dashboard.stripe.com/webhooks

2. **Seleccionar tu Webhook Endpoint:**
   - Busca el endpoint que está recibiendo los eventos
   - Haz clic en él para editarlo

3. **Actualizar Versión de API:**
   - En la sección "API version", cambia de `2024-12-18.acacia` a `2025-11-17.clover`
   - O simplemente selecciona "Use the latest API version"

4. **Guardar Cambios:**
   - Haz clic en "Save changes"

5. **Verificar:**
   - Los próximos eventos deberían llegar con la versión correcta
   - Revisa los logs para confirmar que ya no hay advertencias

---

## 📋 Pasos Detallados

### Opción 1: Actualizar Versión Específica

1. Dashboard → Developers → Webhooks
2. Click en tu endpoint (ej: `https://tu-dominio.com/api/stripe/general-webhook`)
3. Scroll hasta "API version"
4. Cambiar de `2024-12-18.acacia` a `2025-11-17.clover`
5. Click "Save changes"

### Opción 2: Usar Última Versión Automáticamente

1. Dashboard → Developers → Webhooks
2. Click en tu endpoint
3. En "API version", seleccionar "Use the latest API version"
4. Click "Save changes"

**✅ Ventaja:** Siempre usará la versión más reciente automáticamente

---

## 🔍 Verificación

Después de actualizar, verifica en los logs:

**✅ Correcto:**
```
✅ Webhook processed successfully
```

**⚠️ Si aún hay advertencias:**
```
⚠️ Stripe webhook API version mismatch: '2024-12-18.acacia' vs '2025-11-17.clover'
```

Si ves la advertencia, significa que el cambio aún no se ha aplicado. Espera unos minutos y prueba enviando un evento de prueba desde Stripe Dashboard.

---

## 🧪 Probar el Webhook

1. **En Stripe Dashboard:**
   - Ve a tu webhook endpoint
   - Click en "Send test webhook"
   - Selecciona un evento (ej: `checkout.session.completed`)
   - Click "Send test webhook"

2. **Verificar en tu aplicación:**
   - Revisa los logs para confirmar que se procesó correctamente
   - No debería haber errores de versión de API

---

## 📝 Notas Importantes

### ¿Es Seguro Usar `throwOnApiVersionMismatch: false`?

**Sí, es seguro** porque:
- ✅ La validación de signature (HMAC SHA-256) sigue funcionando
- ✅ Previene ataques de replay
- ✅ Solo permite eventos auténticos de Stripe

**⚠️ Precauciones:**
- Puede haber pequeñas diferencias en la estructura de objetos
- Algunos campos nuevos pueden no estar disponibles en versiones antiguas
- Se recomienda actualizar el webhook endpoint para usar la versión correcta

### ¿Por Qué Ocurre Este Error?

- Stripe actualiza su API periódicamente
- El SDK de Stripe.NET se actualiza para soportar nuevas versiones
- Si el webhook endpoint en Stripe Dashboard no se actualiza, puede quedar con una versión antigua
- El SDK detecta esta diferencia y lanza una excepción por seguridad

---

## 🚀 Resumen

**Estado Actual:**
- ✅ Código actualizado para permitir diferentes versiones de API
- ✅ Webhooks funcionan correctamente
- ⚠️ Se registran advertencias en logs para actualizar después

**Recomendación:**
- Actualizar el webhook endpoint en Stripe Dashboard a la versión `2025-11-17.clover`
- O usar "Use the latest API version" para actualización automática

**Próximos Pasos:**
1. Actualizar webhook endpoint en Stripe Dashboard (5 minutos)
2. Verificar que no hay más advertencias en logs
3. Probar con un evento de prueba

---

## 📚 Referencias

- [Stripe API Versioning](https://stripe.com/docs/api/versioning)
- [Stripe Webhooks Guide](https://stripe.com/docs/webhooks)
- [Stripe.NET Documentation](https://github.com/stripe/stripe-dotnet)


