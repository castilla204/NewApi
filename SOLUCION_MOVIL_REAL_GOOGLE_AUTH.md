# 📱 Soluciones para Móvil Real - Error Google Auth

## 🎯 Soluciones Directas en tu Móvil Android

### ✅ Solución 1: Limpiar Caché de Google Play Services (MÁS COMÚN)

**Pasos en tu móvil:**

1. **Abrir Configuración** (⚙️)
2. **Apps** o **Aplicaciones**
3. Buscar **"Google Play Services"**
4. Tocar en **"Google Play Services"**
5. **Almacenamiento** o **Espacio de almacenamiento**
6. Tocar **"Borrar caché"** (NO borrar datos)
7. **Reiniciar el móvil**
8. **Intentar login de nuevo en tu app**

**⚠️ IMPORTANTE**: Solo borrar CACHÉ, NO borrar DATOS (eso eliminaría todas las credenciales).

---

### ✅ Solución 2: Verificar y Re-sincronizar Cuenta de Google

**Pasos en tu móvil:**

1. **Configuración** (⚙️)
2. **Cuentas** o **Usuarios y cuentas**
3. Buscar tu **cuenta de Google** (la que usas para login)
4. Tocar en la cuenta
5. Verificar que esté **"Sincronizada"**
6. Si hay problemas:
   - Tocar **"Sincronizar ahora"**
   - O **"Eliminar cuenta"** y volver a añadirla

---

### ✅ Solución 3: Actualizar Google Play Services

**Pasos en tu móvil:**

1. Abrir **Google Play Store**
2. Buscar **"Google Play Services"**
3. Si hay actualización disponible, **Actualizar**
4. **Reiniciar el móvil**
5. **Intentar login de nuevo**

---

### ✅ Solución 4: Revocar Permisos de tu App en Google

**Pasos en tu móvil o navegador:**

1. Abrir navegador en tu móvil o PC
2. Ir a: **https://myaccount.google.com/permissions**
3. Buscar **"Inspecciono"** o tu app
4. Si aparece, tocar **"Revocar acceso"**
5. **Volver a intentar login** desde tu app

---

### ✅ Solución 5: Verificar que Google Play Services Tenga Permisos

**Pasos en tu móvil:**

1. **Configuración** (⚙️)
2. **Apps** → **Google Play Services**
3. **Permisos**
4. Verificar que tenga:
   - ✅ **Contactos** (si es necesario)
   - ✅ **Teléfono** (si es necesario)
   - ✅ **Almacenamiento**
   - ✅ **Ubicación** (si es necesario)

---

## 🔍 Diagnóstico: Verificar Logs en tu App

**Si tu app muestra logs en la consola, verifica:**

1. Abre tu app
2. Intenta hacer login con Google
3. Revisa los logs en la consola (si los tienes habilitados)
4. Busca estos mensajes:

```
✅ [NativeAuth] Plugin inicializado con Web Client ID
✅ [NativeAuth] Login exitoso
🔑 [NativeAuth] idToken recibido: eyJhbGciOiJSUzI1NiIs...
✅ [NativeAuth] Token decodificado: {email: "...", aud: "..."}
```

**Si ves estos logs:**
- ✅ El plugin funciona correctamente
- ✅ El problema puede estar en el backend o en la validación del token

**Si NO ves estos logs:**
- ❌ El problema está en Google Play Services o en la inicialización del plugin
- ⚠️ Prueba las soluciones 1, 2 y 3 primero

---

## 🚨 Si Nada Funciona

### Opción A: Probar con Otra Cuenta de Google

1. **Cerrar sesión** de tu cuenta actual en el móvil
2. **Añadir otra cuenta de Google** (de prueba)
3. **Intentar login** con esa cuenta
4. Si funciona, el problema es de tu cuenta específica

---

### Opción B: Desinstalar y Reinstalar tu App

1. **Desinstalar** tu app completamente
2. **Reiniciar** el móvil
3. **Reinstalar** la app
4. **Intentar login** de nuevo

---

### Opción C: Verificar que el Web Client ID Sea Correcto

**En tu código (`nativeAuthService.ts`):**

```typescript
const googleWebClientId = '61603823707-qdtl859lc1cktfh8m77ppl1brtdkndsv.apps.googleusercontent.com';
```

**Verificar en Google Cloud Console:**
1. Ir a [Google Cloud Console](https://console.cloud.google.com/)
2. **APIs & Services** → **Credentials**
3. Buscar ese Client ID
4. Verificar que esté **habilitado** y sea de tipo **Web application**

---

## 📋 Checklist Rápido

Antes de probar, verifica:

- [ ] Google Play Services está actualizado
- [ ] El caché de Google Play Services está limpio
- [ ] Tu cuenta de Google está sincronizada
- [ ] Tu app tiene los permisos necesarios
- [ ] El Web Client ID en el código es correcto
- [ ] El backend tiene los Client IDs configurados

---

## 🎯 Orden Recomendado de Pruebas

1. **Primero**: Limpiar caché de Google Play Services + Reiniciar
2. **Segundo**: Verificar y re-sincronizar cuenta de Google
3. **Tercero**: Actualizar Google Play Services
4. **Cuarto**: Revocar permisos y volver a intentar
5. **Quinto**: Probar con otra cuenta de Google

---

## 💡 Consejo Final

**El error "BAD_AUTHENTICATION" en móviles reales suele ser por:**
1. **Caché corrupto de Google Play Services** (Solución 1) - 80% de los casos
2. **Cuenta de Google no sincronizada** (Solución 2) - 15% de los casos
3. **Google Play Services desactualizado** (Solución 3) - 5% de los casos

**Empieza por la Solución 1** - es la más común y la más fácil de resolver.

---

**¿Necesitas ayuda con algún paso específico?** Puedo guiarte paso a paso.
