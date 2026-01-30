# 🔍 Análisis: Error BAD_AUTHENTICATION en Google Play Services

## 📋 Resumen del Error

```
BAD_AUTHENTICATION - Long live credential not available
App: com.google.android.apps.tachyon
Service: oauth2:https://www.googleapis.com/auth/tachyon
```

**⚠️ IMPORTANTE**: Este error es de **Google Play Services** intentando obtener tokens para **Tachyon (Google Meet/Duo)**, NO es directamente de tu app.

**PERO** puede afectar tu app si Google Play Services no puede obtener credenciales OAuth2 en general.

---

## 🔍 ¿Qué Significa?

### Error Principal:
- **BAD_AUTHENTICATION**: Las credenciales de autenticación no son válidas
- **Long live credential not available**: Las credenciales a largo plazo (refresh tokens) no están disponibles

### Posibles Causas:

1. **Sesión de Google expirada o inválida**
   - El usuario necesita volver a iniciar sesión en su cuenta de Google
   - Las credenciales almacenadas están obsoletas

2. **Caché de Google Play Services corrupto**
   - Los datos almacenados localmente están dañados
   - Necesita limpiar el caché

3. **Configuración OAuth2 incorrecta en Google Cloud Console**
   - El Web Client ID no coincide
   - Los permisos OAuth2 no están configurados correctamente

4. **Problemas de sincronización de cuentas**
   - La cuenta de Google del dispositivo no está sincronizada
   - Hay conflictos entre múltiples cuentas

---

## ✅ Verificación de Tu Configuración

### 1. Web Client ID en `nativeAuthService.ts`

**Estado**: ✅ **CORRECTO**

```typescript
const googleWebClientId = '61603823707-qdtl859lc1cktfh8m77ppl1brtdkndsv.apps.googleusercontent.com';
```

**Coincide con**: `appsettings.json` línea 17

---

### 2. Backend Client IDs

**Estado**: ✅ **CONFIGURADO CORRECTAMENTE**

```json
{
  "Google": {
    "ClientIds": [
      "61603823707-4vsp43naifci8t893hdc276kkhbvn49a.apps.googleusercontent.com",
      "61603823707-qdtl859lc1cktfh8m77ppl1brtdkndsv.apps.googleusercontent.com"
    ]
  }
}
```

**El segundo Client ID coincide con el de Android** ✅

---

## 🔧 Soluciones Específicas

### Solución 1: Limpiar Caché de Google Play Services

**En el dispositivo Android:**

1. **Configuración** → **Apps** → **Google Play Services**
2. **Almacenamiento** → **Borrar caché**
3. **Reiniciar el dispositivo**
4. **Intentar login de nuevo**

**O desde ADB:**
```bash
adb shell pm clear com.google.android.gms
```

⚠️ **ADVERTENCIA**: Esto eliminará TODAS las credenciales de Google del dispositivo. El usuario tendrá que volver a iniciar sesión en todas las apps de Google.

---

### Solución 2: Verificar Sesión de Google en el Dispositivo

**En el dispositivo Android:**

1. **Configuración** → **Cuentas** → **Google**
2. Verificar que la cuenta esté activa y sincronizada
3. Si hay problemas, **Eliminar cuenta** y **Volver a añadir**

---

### Solución 3: Verificar Configuración en Google Cloud Console

**Verificar que el Web Client ID esté correctamente configurado:**

1. Ir a [Google Cloud Console](https://console.cloud.google.com/)
2. **APIs & Services** → **Credentials**
3. Buscar el Client ID: `61603823707-qdtl859lc1cktfh8m77ppl1brtdkndsv.apps.googleusercontent.com`
4. Verificar:
   - ✅ Tipo: **Web application**
   - ✅ **Authorized JavaScript origins**: Debe incluir tu dominio
   - ✅ **Authorized redirect URIs**: Debe incluir tu callback URL

---

### Solución 4: Revocar y Regenerar Tokens

**Si el problema persiste:**

1. Ir a [myaccount.google.com/permissions](https://myaccount.google.com/permissions)
2. Buscar tu app o "Inspecciono"
3. **Revocar acceso**
4. Volver a intentar login desde tu app

---

## 🧪 Pruebas de Diagnóstico

### Test 1: Verificar que el Plugin se Inicializa Correctamente

**Agregar logs adicionales en `nativeAuthService.ts`:**

```typescript
async signInWithGoogle(): Promise<{ success: boolean; user: any; requiresMFA: boolean }> {
    try {
        console.log('🚀 [NativeAuth] Iniciando Google Sign-In...');
        console.log('🔍 [NativeAuth] Web Client ID:', googleWebClientId);
        console.log('🔍 [NativeAuth] Platform:', Capacitor.getPlatform());
        
        // ✅ PASO 1: Inicializar el plugin
        await (SocialLogin as any).initialize(initConfig);
        console.log('✅ [NativeAuth] Plugin inicializado correctamente');
        
        // ... resto del código
    }
}
```

**Verificar en Logcat:**
- Buscar `[NativeAuth]` en los logs
- Verificar que el Web Client ID sea correcto
- Verificar que el plugin se inicialice sin errores

---

### Test 2: Verificar el `aud` del Token

**El token debe tener el `aud` correcto:**

```typescript
decoded = jwtDecode(result.idToken);
console.log('🔍 [DEBUG] aud del token:', decoded.aud);
console.log('🔍 [DEBUG] Web Client ID esperado:', googleWebClientId);
console.log('🔍 [DEBUG] ¿Coinciden?:', decoded.aud === googleWebClientId);
```

**Si NO coinciden:**
- El problema está en la configuración del Web Client ID
- Verificar Google Cloud Console

---

### Test 3: Verificar Respuesta del Backend

**Agregar logs en el backend:**

```csharp
// En UserService.cs - GoogleAuth
var payload = await GoogleJsonWebSignature.ValidateAsync(request.AccessToken, settings);
_logger.LogInformation($"✅ Token validado. Email: {payload.Email}, Aud: {payload.Audience}");
```

**Verificar:**
- Que el token se valide correctamente
- Que el `aud` del token coincida con uno de los Client IDs configurados

---

## 🚨 Si el Error Persiste

### Opción 1: Reinstalar Google Play Services

**Solo si es absolutamente necesario:**

```bash
# Desinstalar actualizaciones (volver a versión de fábrica)
adb shell pm uninstall -k --user 0 com.google.android.gms

# Reiniciar dispositivo
adb reboot

# Google Play Services se actualizará automáticamente
```

⚠️ **ADVERTENCIA**: Esto puede causar problemas con otras apps de Google.

---

### Opción 2: Verificar SHA-1 en Google Cloud Console

**El SHA-1 del certificado de firma debe estar configurado:**

1. Obtener SHA-1:
```bash
cd android
./gradlew signingReport
```

2. Ir a Google Cloud Console → Credentials → Android Client ID
3. Verificar que el SHA-1 esté en la lista
4. Si no está, agregarlo

---

### Opción 3: Usar Modo Debug Temporal

**Para desarrollo, puedes usar un Web Client ID de prueba:**

```typescript
// Solo para desarrollo/debug
const googleWebClientId = process.env.NODE_ENV === 'development'
    ? 'TU_CLIENT_ID_DE_PRUEBA'
    : '61603823707-qdtl859lc1cktfh8m77ppl1brtdkndsv.apps.googleusercontent.com';
```

---

## 📝 Checklist de Verificación

### En el Dispositivo:
- [ ] Google Play Services está actualizado
- [ ] La cuenta de Google está activa y sincronizada
- [ ] El caché de Google Play Services está limpio
- [ ] No hay múltiples cuentas de Google conflictivas

### En Google Cloud Console:
- [ ] Web Client ID existe y está habilitado
- [ ] Authorized JavaScript origins está configurado
- [ ] Authorized redirect URIs está configurado
- [ ] Android Client ID tiene el SHA-1 correcto

### En el Código:
- [ ] `webClientId` en `nativeAuthService.ts` coincide con el del backend
- [ ] El backend tiene ambos Client IDs configurados
- [ ] Los logs muestran que el token se genera correctamente

---

## 🎯 Conclusión

**El error que estás viendo es de Google Play Services, NO de tu app directamente.**

**Sin embargo, puede afectar tu app si:**
1. Google Play Services no puede obtener credenciales OAuth2
2. El usuario no tiene una sesión válida de Google
3. Hay problemas de configuración en Google Cloud Console

**Soluciones prioritarias:**
1. ✅ Limpiar caché de Google Play Services
2. ✅ Verificar sesión de Google en el dispositivo
3. ✅ Verificar configuración en Google Cloud Console
4. ✅ Verificar que el `aud` del token coincida con los Client IDs

**Tu código está correctamente configurado** ✅ - El problema es del entorno del dispositivo o de Google Play Services.

---

## 📞 Si Nada Funciona

1. **Probar en otro dispositivo** - Para descartar problemas del dispositivo específico
2. **Probar con otra cuenta de Google** - Para descartar problemas de la cuenta
3. **Verificar logs completos de Google Play Services** - Para ver más detalles del error
4. **Contactar soporte de Google** - Si el problema persiste en múltiples dispositivos

---

**Última actualización**: 2026-01-30
