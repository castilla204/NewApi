# ✅ CHECKLIST - VERIFICACIÓN MFA/2FA

## 🎯 VERIFICACIÓN RÁPIDA (5 MINUTOS)

Marca cada ítem verificado:

---

### 1. ✅ CONFIGURACIÓN BÁSICA

- [ ] **Clave de cifrado MFA configurada**
  ```bash
  # Verificar en appsettings.json o Google Cloud Secret Manager
  # "Mfa:EncryptionKey" debe tener 32+ caracteres
  ```

- [ ] **Nombre de app configurado**
  ```json
  {
    "App": {
      "Name": "TuApp"  // Aparecerá en Google Authenticator
    }
  }
  ```

- [ ] **Tabla UserMfaSettings existe**
  ```bash
  # Verificar migración aplicada
  dotnet ef migrations list
  # Debe aparecer: "20251116023208_AddUserMfaSettings"
  ```

---

### 2. ✅ ENDPOINTS FUNCIONANDO

- [ ] **POST /api/auth/mfa/setup** (Obtener QR code)
  ```bash
  curl -X POST https://tu-api.com/api/auth/mfa/setup \
    -H "Authorization: Bearer YOUR_JWT"
  
  # Debe devolver:
  # - qrCodeBase64 (string)
  # - manualEntryKey (string con espacios)
  # - message (instrucciones)
  ```

- [ ] **POST /api/auth/mfa/enable** (Habilitar MFA)
  ```bash
  curl -X POST https://tu-api.com/api/auth/mfa/enable \
    -H "Authorization: Bearer YOUR_JWT" \
    -H "Content-Type: application/json" \
    -d '{"totpCode":"123456"}'
  
  # Debe devolver:
  # - recoveryCodes (array de 10 strings)
  # - qrCodeBase64
  # - manualEntryKey
  ```

- [ ] **POST /api/auth/mfa/verify** (Verificar código)
  ```bash
  curl -X POST https://tu-api.com/api/auth/mfa/verify \
    -H "Authorization: Bearer YOUR_JWT" \
    -H "Content-Type: application/json" \
    -d '{"code":"123456", "isRecoveryCode":false}'
  
  # Debe devolver:
  # - isValid: true
  # - accessToken (nuevo)
  # - refreshToken (nuevo)
  ```

- [ ] **GET /api/auth/mfa/status** (Estado de MFA)
  ```bash
  curl -X GET https://tu-api.com/api/auth/mfa/status \
    -H "Authorization: Bearer YOUR_JWT"
  
  # Debe devolver:
  # - isEnabled: boolean
  # - remainingRecoveryCodes: number
  # - enabledAt: timestamp
  ```

- [ ] **POST /api/auth/mfa/disable** (Deshabilitar MFA)
  ```bash
  curl -X POST https://tu-api.com/api/auth/mfa/disable \
    -H "Authorization: Bearer YOUR_JWT" \
    -H "Content-Type: application/json" \
    -d '{"password":"mipassword", "totpCode":"123456"}'
  
  # Debe devolver:
  # - message: "MFA disabled successfully"
  ```

---

### 3. ✅ SEGURIDAD

- [ ] **Secretos cifrados en BD**
  ```sql
  -- Verificar que los secretos NO estén en texto plano
  SELECT "TotpSecret", "RecoveryCodesEncrypted" 
  FROM "UserMfaSettings" 
  LIMIT 1;
  
  -- Debe verse algo como:
  -- "XyZ8fG3p9kL..." (Base64, NO texto legible)
  ```

- [ ] **Protección brute force funciona**
  ```bash
  # Intentar 6 veces con código incorrecto
  for i in {1..6}; do
    curl -X POST https://tu-api.com/api/auth/mfa/verify \
      -H "Authorization: Bearer YOUR_JWT" \
      -d '{"code":"000000"}'
  done
  
  # Intento 1-5: "Invalid code. X attempts remaining"
  # Intento 6: "Account locked for 15 minutes"
  ```

- [ ] **Rate limiting activo**
  ```bash
  # Verificar en Program.cs
  grep -A 5 "EnableRateLimiting" Controllers/AuthController.cs
  
  # Debe tener: [EnableRateLimiting("auth")]
  ```

- [ ] **Ventana de tiempo correcta (±30s)**
  ```csharp
  // Verificar en Services/MfaService.cs línea 100
  // Debe ser: new VerificationWindow(1, 1)
  ```

---

### 4. ✅ COMPATIBILIDAD

- [ ] **Google Authenticator funciona**
  - Escanear QR code con Google Authenticator
  - Ingresar código generado
  - Debe verificar correctamente

- [ ] **Microsoft Authenticator funciona**
  - Probar con Microsoft Authenticator
  - Debe generar códigos válidos

- [ ] **Entrada manual funciona**
  - Copiar `manualEntryKey` del response
  - Ingresar manualmente en cualquier app TOTP
  - Debe generar códigos correctos

- [ ] **Código se acepta en ventana de tiempo**
  ```bash
  # Generar código en Google Authenticator
  # Esperar 5-10 segundos
  # Ingresar código
  # Debe ACEPTARSE (ventana de ±30s)
  ```

---

### 5. ✅ CÓDIGOS DE RECUPERACIÓN

- [ ] **Se generan 10 códigos**
  ```bash
  # Al habilitar MFA, verificar que devuelve:
  # "recoveryCodes": ["XXXX-XXXX", "YYYY-YYYY", ...]
  # Debe haber exactamente 10
  ```

- [ ] **Formato correcto (XXXX-XXXX)**
  - Cada código: 8 caracteres
  - Formato: XXXX-XXXX (con guion)
  - Sin caracteres ambiguos (0, O, 1, I, l)

- [ ] **Un solo uso funciona**
  ```bash
  # Usar código de recuperación
  POST /api/auth/mfa/verify
  {"code":"ABCD-EFGH", "isRecoveryCode":true}
  
  # Verificar que:
  # 1. Código se acepta (200 OK)
  # 2. Intento 2 con mismo código falla (401)
  # 3. remainingRecoveryCodes = 9
  ```

- [ ] **Advertencia cuando quedan pocos**
  ```bash
  # Usar 7 códigos
  # Al usar el 8vo código, debe advertir:
  # "Only 2 recovery codes remaining!"
  ```

---

### 6. ✅ EXPERIENCIA DE USUARIO

- [ ] **QR code se muestra correctamente**
  - Base64 se decodifica a imagen PNG
  - Tamaño legible (no pixelado)
  - Escaneable con cámara del teléfono

- [ ] **Mensajes de error claros**
  ```bash
  # Código inválido:
  "Invalid code. 4 attempts remaining."
  
  # Cuenta bloqueada:
  "Account locked for 15 minutes."
  
  # Recovery code usado:
  "Warning: Only 3 recovery codes remaining!"
  ```

- [ ] **Proceso de 2 pasos funciona**
  1. `/mfa/setup` → Devuelve QR (MFA NO habilitado)
  2. Usuario escanea QR
  3. `/mfa/enable` → Verifica código Y ENTONCES habilita MFA
  4. No hay forma de habilitar sin verificar primero

---

### 7. ✅ AUDITORÍA

- [ ] **Timestamps se registran**
  ```sql
  SELECT "EnabledAt", "LastVerifiedAt", "CreatedAt", "UpdatedAt"
  FROM "UserMfaSettings"
  WHERE "UserId" = 1;
  
  -- Todos deben tener valores (excepto los opcionales)
  ```

- [ ] **Intentos fallidos se cuentan**
  ```sql
  -- Después de 3 intentos fallidos:
  SELECT "FailedAttempts" FROM "UserMfaSettings" WHERE "UserId" = 1;
  -- Debe mostrar: 3
  
  -- Después de login exitoso:
  -- Debe resetearse a: 0
  ```

- [ ] **Logging funciona**
  ```bash
  # Revisar logs de la aplicación
  grep "MFA enabled" logs/app.log
  grep "Invalid TOTP code" logs/app.log
  
  # Debe haber entradas para eventos importantes
  ```

---

### 8. ✅ DESHABILITACIÓN SEGURA

- [ ] **Requiere password + TOTP**
  ```bash
  # Solo password → FALLA
  POST /api/auth/mfa/disable
  {"password":"test", "totpCode":""}
  → 401 Unauthorized
  
  # Solo TOTP → FALLA
  POST /api/auth/mfa/disable
  {"password":"", "totpCode":"123456"}
  → 401 Unauthorized
  
  # Ambos correctos → ÉXITO
  POST /api/auth/mfa/disable
  {"password":"test", "totpCode":"123456"}
  → 200 OK
  ```

- [ ] **No elimina configuración (soft delete)**
  ```sql
  -- Después de deshabilitar:
  SELECT "IsEnabled", "EnabledAt", "TotpSecret"
  FROM "UserMfaSettings"
  WHERE "UserId" = 1;
  
  -- IsEnabled = false
  -- EnabledAt sigue teniendo valor (historial)
  -- TotpSecret NO se elimina (puede rehabilitar)
  ```

---

### 9. ✅ PAQUETES NUGET

- [ ] **OTP.NET instalado**
  ```bash
  dotnet list package | grep Otp.NET
  # Debe aparecer: Otp.NET    1.x.x
  ```

- [ ] **QRCoder instalado**
  ```bash
  dotnet list package | grep QRCoder
  # Debe aparecer: QRCoder    1.x.x
  ```

---

### 10. ✅ PRUEBA COMPLETA END-TO-END

Ejecutar esta secuencia completa:

```bash
# 1. Usuario sin MFA
GET /api/auth/mfa/status
→ {"isEnabled": false}

# 2. Iniciar setup
POST /api/auth/mfa/setup
→ Recibe QR code

# 3. Escanear con Google Authenticator
# (Acción manual del usuario)

# 4. Habilitar con código de la app
POST /api/auth/mfa/enable
{"totpCode": "123456"}
→ Recibe 10 códigos de recuperación

# 5. Verificar que está habilitado
GET /api/auth/mfa/status
→ {"isEnabled": true, "remainingRecoveryCodes": 10}

# 6. Simular login con MFA
POST /api/auth/mfa/verify
{"code": "789012", "isRecoveryCode": false}
→ Recibe nuevos tokens JWT

# 7. Probar código de recuperación
POST /api/auth/mfa/verify
{"code": "ABCD-EFGH", "isRecoveryCode": true}
→ Código aceptado, quedan 9

# 8. Verificar códigos restantes
GET /api/auth/mfa/status
→ {"remainingRecoveryCodes": 9}

# 9. Deshabilitar MFA
POST /api/auth/mfa/disable
{"password": "test", "totpCode": "456789"}
→ MFA deshabilitado

# 10. Verificar estado final
GET /api/auth/mfa/status
→ {"isEnabled": false}
```

**Si TODOS los pasos funcionan: ✅ TU MFA ESTÁ PERFECTA**

---

## 📊 PUNTUACIÓN

Total de ítems: **33**

- [ ] **33/33 (100%):** ⭐⭐⭐⭐⭐ PERFECTO - Producción lista
- [ ] **30-32 (91-97%):** ⭐⭐⭐⭐ EXCELENTE - Corregir detalles menores
- [ ] **25-29 (76-90%):** ⭐⭐⭐ BUENO - Revisar ítems faltantes
- [ ] **<25 (<76%):** ⚠️ REVISAR - Hay problemas importantes

---

## ⚠️ PROBLEMAS COMUNES

### "MFA encryption key not configured"
```bash
# Solución: Agregar clave en appsettings.json
{
  "Mfa": {
    "EncryptionKey": "YOUR-32-CHARACTER-SECRET-KEY-HERE"
  }
}
```

### "Invalid TOTP code" (pero el código es correcto)
- ✅ Verificar reloj del servidor (debe estar sincronizado)
- ✅ Verificar ventana de tiempo: `new VerificationWindow(1, 1)`
- ✅ Verificar que el secreto se cifra/descifra correctamente

### QR code no escaneable
- ✅ Verificar tamaño: `qrCode.GetGraphic(20)` (debe ser 20 o más)
- ✅ Verificar nivel de corrección: `ECCLevel.Q` (recomendado)
- ✅ Probar entrada manual como alternativa

### Recovery codes no funcionan
- ✅ Verificar normalización: `.Replace("-", "").Replace(" ", "")`
- ✅ Verificar case-insensitive: `.ToUpperInvariant()`
- ✅ Verificar que se eliminan después de usar

---

## ✅ SIGUIENTE PASO

**Si pasaste todas las verificaciones:**

1. ✅ Tu MFA/2FA está lista para producción
2. ✅ Documenta el proceso para usuarios finales
3. ✅ Considera agregar endpoint de regeneración de códigos (opcional)
4. ✅ Monitorea logs de intentos fallidos en producción

**Si encontraste problemas:**

1. ⚠️ Revisa `AUDITORIA_MFA_2FA_2025.md` para detalles técnicos
2. ⚠️ Consulta `MFA_COMPLETE_IMPLEMENTATION.md` para ejemplos
3. ⚠️ Verifica configuración en `appsettings.json`

---

**¡Felicitaciones por tu implementación de nivel empresarial!** 🎉🔒

