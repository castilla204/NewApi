# 🎯 Solución Completa: CORS en Capacitor Android

## ✅ **PROBLEMA IDENTIFICADO**

Las peticiones HTTP desde Capacitor Android fallan con errores CORS porque:
1. **El backend NO permite** los orígenes `https://localhost` y `capacitor://localhost`
2. **Algunas peticiones** en el frontend usan `fetch` nativo en lugar de `capacitorFetch`

---

## 🔧 **SOLUCIÓN 1: Configurar CORS en el Backend** ✅

**Ya está aplicado en `Program.cs`:**

```csharp
builder.Services.AddCors(options =>
{
    if (isDevelopment)
    {
        // ✅ DESARROLLO: Permitir cualquier origen
        options.AddPolicy("AllowSpecificOrigin", builder =>
        {
            builder.AllowAnyOrigin()
                   .AllowAnyMethod()
                   .AllowAnyHeader()
                   .SetPreflightMaxAge(TimeSpan.FromSeconds(600));
        });
    }
    else
    {
        // ✅ PRODUCCIÓN: Solo orígenes específicos con credenciales
        options.AddPolicy("AllowSpecificOrigin", builder =>
        {
            builder.WithOrigins(
                "http://localhost:3000",
                "http://localhost:5173",
                "https://localhost",              // ✅ Capacitor Android/iOS
                "capacitor://localhost",          // ✅ Capacitor Android/iOS (alternativo)
                "https://inspecciono.com",
                "https://www.inspecciono.com")
                   .AllowAnyMethod()
                   .AllowAnyHeader()
                   .AllowCredentials()
                   .SetPreflightMaxAge(TimeSpan.FromSeconds(600));
        });
    }
});
```

**⚠️ IMPORTANTE**: Después de este cambio, **reinicia el backend** para que tome efecto.

---

## 🔧 **SOLUCIÓN 2: Usar `capacitorFetch` en TODAS las Peticiones**

### **Buscar Peticiones Problemáticas**

**En el frontend, busca estas peticiones que usan `fetch` nativo:**

```bash
# Buscar peticiones a mfa/status
grep -r "mfa/status" src/

# Buscar peticiones a map-experts
grep -r "map-experts" src/

# Buscar todas las peticiones fetch que NO usan capacitorFetch
grep -r "fetch(" src/ | grep -v "capacitorFetch"
```

### **Reemplazar con `capacitorFetch`**

**❌ ANTES (causa CORS):**
```typescript
const response = await fetch(`${API_CONFIG.baseUrl}/api/auth/mfa/status`, {
    method: 'GET',
    headers: {
        'Authorization': `Bearer ${token}`,
    },
});
```

**✅ DESPUÉS (evita CORS):**
```typescript
import { capacitorFetch } from '../utils/capacitorFetch';

const response = await capacitorFetch(`${API_CONFIG.baseUrl}/api/auth/mfa/status`, {
    method: 'GET',
    headers: {
        'Authorization': `Bearer ${token}`,
    },
});
```

---

## 📋 **ARCHIVOS A REVISAR EN EL FRONTEND**

Busca estos archivos que pueden tener peticiones problemáticas:

1. **`src/services/authService.ts`** - Peticiones de autenticación
2. **`src/hooks/useServiceLoader.ts`** - Peticiones del mapa
3. **`src/services/api.ts`** - Servicio principal de API
4. **`src/utils/useApi.ts`** - Hook de API
5. **Cualquier archivo que use `fetch` directamente**

---

## 🔍 **CÓMO VERIFICAR QUE FUNCIONA**

**1. Verificar en los logs de Android Studio:**

```
✅ DEBE aparecer para TODAS las peticiones:
Capacitor/Plugin: To native (Capacitor plugin): pluginId: CapacitorHttp

❌ NO debe aparecer:
Access to fetch at '...' from origin 'https://localhost' has been blocked by CORS
```

**2. Verificar que las peticiones funcionen:**

- ✅ Login funciona
- ✅ MFA status funciona
- ✅ Map experts carga
- ✅ Todas las peticiones HTTP funcionan

---

## 🚀 **PASOS SIGUIENTES**

1. **✅ Backend**: Ya está configurado con los orígenes de Capacitor
2. **⏳ Frontend**: Buscar y reemplazar peticiones `fetch` con `capacitorFetch`
3. **⏳ Rebuild**: Reconstruir la app Android después de los cambios

---

## 💡 **NOTA IMPORTANTE**

**¿Por qué `capacitorFetch` evita CORS?**

`capacitorFetch` usa el plugin nativo `CapacitorHttp` que hace las peticiones **fuera del webview** (a nivel nativo de Android/iOS), por lo que **NO está sujeto a las restricciones CORS** del navegador.

Sin embargo, es **mejor práctica** configurar CORS correctamente en el backend también, por si alguna petición se escapa o para desarrollo web.

---

## 📝 **CHECKLIST**

- [x] Backend configurado con orígenes de Capacitor
- [ ] Buscar peticiones `fetch` problemáticas en el frontend
- [ ] Reemplazar con `capacitorFetch`
- [ ] Rebuild y probar en Android
- [ ] Verificar logs para confirmar que no hay errores CORS
