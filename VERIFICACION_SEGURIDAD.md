# 🔐 VERIFICACIÓN DE SEGURIDAD - CHECKLIST RÁPIDO

## ✅ CHECKLIST DE VALIDACIÓN

Usa este documento para verificar que tu implementación de autenticación está correctamente configurada.

---

## 1. ✅ VERIFICAR LONGITUD DE CLAVE JWT

### ¿Por qué es importante?
Una clave JWT demasiado corta puede ser vulnerable a ataques de fuerza bruta.

### Requisitos:
- ⚠️ **MÍNIMO:** 32 caracteres (256 bits)
- ✅ **RECOMENDADO:** 64 caracteres (512 bits)

### Cómo verificar:

#### Opción 1: Ejecutar la aplicación
La aplicación ahora valida automáticamente la longitud de la clave al iniciar:

```bash
dotnet run
```

**Salidas esperadas:**

✅ **EXCELENTE (64+ caracteres):**
```
✅ JWT Key length validated: 64 bytes (512 bits) - EXCELLENT
```

✅ **SEGURO (32-63 caracteres):**
```
✅ JWT Key length validated: 42 bytes (336 bits) - SECURE
```

⚠️ **WARNING (32-63 en producción):**
```
⚠️ WARNING: JWT Key length (42 bytes / 336 bits) is below recommended length...
```

❌ **ERROR CRÍTICO (<32 caracteres):**
```
⚠️ CRITICAL SECURITY ERROR: JWT Key is too short (24 bytes / 192 bits)...
```

#### Opción 2: Verificar manualmente en Google Cloud

```bash
# Ver valor del secreto (solo primeros 10 caracteres)
gcloud secrets versions access latest --secret="jwt-key" | head -c 10
echo "..."

# Ver longitud completa
gcloud secrets versions access latest --secret="jwt-key" | wc -c
```

**Interpretación:**
- Si muestra `64` o más → ✅ EXCELENTE
- Si muestra `32-63` → ⚠️ SEGURO pero considera mejorar
- Si muestra menos de `32` → ❌ **CRÍTICO - Genera una nueva clave**

---

## 2. ✅ GENERAR NUEVA CLAVE JWT SEGURA

Si tu clave es demasiado corta, genera una nueva:

### Opción 1: PowerShell (Windows)
```powershell
[Convert]::ToBase64String((1..64 | ForEach-Object {Get-Random -Minimum 0 -Maximum 256}))
```

### Opción 2: Bash (Linux/Mac)
```bash
openssl rand -base64 64
```

### Opción 3: Online (NO recomendado para producción)
```
https://generate-secret.vercel.app/64
```

### Actualizar en Google Cloud Secret Manager:
```bash
# Crear nueva versión del secreto
echo -n "TU_NUEVA_CLAVE_AQUI" | gcloud secrets versions add jwt-key --data-file=-

# Verificar que se actualizó
gcloud secrets versions access latest --secret="jwt-key" | wc -c
```

---

## 3. ✅ VERIFICAR CONFIGURACIÓN DE TOKENS

### Access Token (JWT)
```bash
# Verificar en Services/UserService.cs línea 929
# Debe ser: expires: DateTime.UtcNow.AddMinutes(30)
```

✅ **Configuración actual:** 30 minutos  
✅ **OWASP Recomendación:** 5-60 minutos  
✅ **Estado:** ÓPTIMO

### Refresh Token
```bash
# Verificar en Services/UserService.cs línea 958
# Debe ser: ExpiresAt = DateTime.UtcNow.AddDays(7)
```

✅ **Configuración actual:** 7 días  
✅ **OWASP Recomendación:** 7-30 días  
✅ **Estado:** ÓPTIMO

---

## 4. ✅ VERIFICAR RATE LIMITING

### Probar límite de autenticación:
```bash
# Hacer 6 requests rápidos (debe bloquear el 6to)
for i in {1..6}; do
  curl -X POST http://localhost:5000/api/auth/refresh-token \
    -H "Content-Type: application/json" \
    -d '{"refreshToken":"test"}' \
    -w "\nStatus: %{http_code}\n"
done
```

**Resultado esperado:**
- Requests 1-5: `401 Unauthorized` (token inválido, pero permite el intento)
- Request 6: `429 Too Many Requests` ✅

### Verificar respuesta 429:
```json
{
  "error": "Too many requests. Please try again later.",
  "retryAfter": 300
}
```

✅ **Estado:** Rate limiting funcionando correctamente

---

## 5. ✅ VERIFICAR MFA (MULTI-FACTOR AUTHENTICATION)

### Verificar que MFA está habilitado:
```bash
# GET /api/auth/mfa/status (con token JWT válido)
curl -X GET http://localhost:5000/api/auth/mfa/status \
  -H "Authorization: Bearer TU_TOKEN_JWT"
```

**Respuesta esperada:**
```json
{
  "mfaEnabled": true,
  "hasRecoveryCodes": true,
  "remainingRecoveryCodes": 10
}
```

✅ **Estado:** MFA implementado y funcionando

---

## 6. ✅ VERIFICAR HTTPS EN PRODUCCIÓN

### Verificar en Program.cs línea 369:
```csharp
options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
```

✅ **Estado:** HTTPS obligatorio en producción

### Probar en producción:
```bash
# Debe fallar si se intenta con HTTP en producción
curl http://tu-dominio.com/api/user/profile

# Debe funcionar con HTTPS
curl https://tu-dominio.com/api/user/profile
```

---

## 7. ✅ VERIFICAR ROTACIÓN DE REFRESH TOKENS

### Probar rotación:
```bash
# 1. Login (obtiene refresh token)
curl -X POST http://localhost:5000/api/user/google-auth \
  -H "Content-Type: application/json" \
  -d '{"idToken":"TU_GOOGLE_TOKEN"}' \
  -o response1.json

# Extraer refresh token
REFRESH_TOKEN=$(cat response1.json | jq -r '.refreshToken')

# 2. Usar refresh token (debe generar nuevo token)
curl -X POST http://localhost:5000/api/auth/refresh-token \
  -H "Content-Type: application/json" \
  -d "{\"refreshToken\":\"$REFRESH_TOKEN\"}" \
  -o response2.json

NEW_REFRESH_TOKEN=$(cat response2.json | jq -r '.refreshToken')

# 3. Intentar usar el token antiguo (debe fallar con revocación total)
curl -X POST http://localhost:5000/api/auth/refresh-token \
  -H "Content-Type: application/json" \
  -d "{\"refreshToken\":\"$REFRESH_TOKEN\"}"
```

**Resultado esperado:**
- Paso 2: ✅ `200 OK` con nuevo refresh token
- Paso 3: ❌ `401 Unauthorized` + mensaje "Token revoked. All sessions terminated."

✅ **Estado:** Rotación de tokens funcionando (detecta reuso)

---

## 8. ✅ VERIFICAR GESTIÓN DE SECRETOS

### Verificar que no hay secretos hardcodeados:

#### Opción 1: Trivy (Scanner de seguridad)
```bash
trivy fs . --scanners secret
```

**Resultado esperado:**
```
0 CRITICAL, 0 HIGH vulnerabilities
```

#### Opción 2: Búsqueda manual
```bash
# No debe encontrar nada (excepto comentarios)
grep -r "sk_test_" . --exclude-dir={bin,obj,node_modules}
grep -r "sk_live_" . --exclude-dir={bin,obj,node_modules}
grep -r "whsec_" . --exclude-dir={bin,obj,node_modules}
```

✅ **Estado:** Sin secretos hardcodeados

---

## 9. ✅ VERIFICAR ALGORITMO DE FIRMA JWT

### Verificar en AuthController.cs línea 193:
```csharp
var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
```

✅ **Algoritmo:** HMAC-SHA256  
✅ **Estado:** SEGURO (estándar de la industria)

### ⚠️ NO vulnerable al ataque "none algorithm"
El middleware de .NET valida automáticamente que el algoritmo coincida.

---

## 10. ✅ VERIFICAR LIMPIEZA DE TOKENS EXPIRADOS

### Verificar servicio de limpieza:
```bash
# Verificar que RefreshTokenCleanupService está registrado
grep -A 5 "RefreshTokenCleanupService" Program.cs
```

✅ **Configurado:** Limpieza automática cada 24 horas (Hangfire)  
✅ **Retención:** 30 días después de expiración/revocación  
✅ **Estado:** Implementado correctamente

---

## 📊 RESUMEN DE VERIFICACIÓN

Marca con ✅ cada ítem verificado:

- [ ] **1. Longitud de clave JWT:** ≥32 caracteres (recomendado 64)
- [ ] **2. Access Token expiration:** 30 minutos
- [ ] **3. Refresh Token expiration:** 7 días
- [ ] **4. Rate Limiting:** 5 intentos/5min en auth
- [ ] **5. MFA/TOTP:** Habilitado con recovery codes
- [ ] **6. HTTPS:** Obligatorio en producción
- [ ] **7. Token Rotation:** Detecta reuso y revoca
- [ ] **8. Secretos:** No hardcodeados
- [ ] **9. Algoritmo JWT:** HMAC-SHA256
- [ ] **10. Limpieza automática:** Tokens antiguos eliminados

---

## 🎯 ACCIONES SI FALLAS ALGUNA VERIFICACIÓN

### Si falla #1 (Longitud de clave JWT):
1. Genera una nueva clave (ver sección 2)
2. Actualiza en Google Cloud Secret Manager
3. Reinicia la aplicación
4. ⚠️ **IMPORTANTE:** Invalida todas las sesiones activas

### Si falla #4 (Rate Limiting):
1. Verifica `Program.cs` líneas 272-308
2. Verifica que `app.UseRateLimiter()` está en el pipeline
3. Verifica atributo `[EnableRateLimiting("auth")]` en AuthController

### Si falla #7 (Token Rotation):
1. Verifica `AuthController.cs` líneas 71-87
2. Verifica tabla `RefreshTokens` en BD tiene columnas:
   - `IsRevoked`
   - `RevokedAt`
   - `ReplacedByToken`

### Si falla #8 (Secretos hardcodeados):
1. Mueve secretos a Google Cloud Secret Manager
2. Actualiza `Program.cs` para cargarlos
3. Elimina del código y archivos de configuración
4. **NUNCA** commitees secretos a Git

---

## 🔒 VERIFICACIÓN DE PRODUCCIÓN

### Antes de desplegar a producción, verifica:

```bash
# 1. Todas las pruebas pasan
dotnet test

# 2. Build exitoso
dotnet build -c Release

# 3. Scanner de seguridad
trivy fs . --scanners vuln,secret

# 4. Variables de entorno configuradas
kubectl get secrets -n default

# 5. HTTPS configurado
curl -I https://tu-dominio.com | grep "HTTP/2 200"
```

✅ **Todo debe pasar antes de desplegar**

---

## 📞 SOPORTE

Si alguna verificación falla y no sabes cómo solucionarlo:

1. 📖 Revisa `AUDITORIA_SEGURIDAD_JWT_2025.md`
2. 📖 Consulta `SECURITY_FINAL_STATUS.md`
3. 📖 Mira ejemplos en `MFA_COMPLETE_IMPLEMENTATION.md`
4. 🔍 Busca en la documentación del error específico

---

**✅ ÚLTIMA VERIFICACIÓN:** Ejecuta la aplicación y verifica que inicia sin errores:

```bash
dotnet run
```

**Salida esperada:**
```
✅ JWT Key length validated: 64 bytes (512 bits) - EXCELLENT
✅ Rate Limiting configured: 5 policies active
✅ MFA Service registered
✅ Refresh Token Cleanup scheduled (daily)
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: https://localhost:5001
```

---

**¡Tu aplicación está lista para producción con seguridad de nivel empresarial!** 🎉🔒

