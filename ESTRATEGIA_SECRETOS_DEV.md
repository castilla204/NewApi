# 🎯 Estrategia de Secretos: Desarrollo vs Producción

## 📋 Situación Actual

En Google Cloud Secret Manager tienes:
- **Secretos normales** (usados en producción y desarrollo): `jwt-key`, `postgres-password`, etc.
- **Secretos específicos de desarrollo** (solo algunos): `stripe-secret-key-dev`, `stripe-webhook-secret-dev`, `stripe-general-webhook-secret-dev`

## ✅ Estrategia Correcta

**Por defecto**: Usar los mismos secretos en desarrollo y producción (los normales sin sufijo)

**Excepciones**: Solo para secretos que TENGAN versión `-dev`, usar esa en desarrollo

## 🔄 Lógica a Implementar

```
Para cada secreto:
1. En DESARROLLO:
   - Intentar primero: {secretName}-dev
   - Si existe → usarlo
   - Si NO existe → usar {secretName} (el normal)

2. En PRODUCCIÓN:
   - Usar siempre: {secretName} (sin sufijo)
```

## 📝 Ejemplo

- `jwt-key`: No tiene `-dev` → Usar `jwt-key` en dev y prod
- `stripe-secret-key`: Tiene `-dev` → Usar `stripe-secret-key-dev` en dev, `stripe-secret-key` en prod

## ✅ Ventajas

1. **Flexibilidad**: Solo creas secretos `-dev` cuando realmente necesitas valores diferentes
2. **Simplicidad**: La mayoría de secretos se comparten entre entornos
3. **Seguridad**: Puedes tener valores de prueba solo para servicios específicos (como Stripe)
4. **Compatibilidad**: Si no existe `-dev`, funciona igual que antes

