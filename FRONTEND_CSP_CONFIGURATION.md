# 🔒 Configuración de Content Security Policy (CSP) para Frontend

## 🚨 PROBLEMAS ACTUALES

### 1. **Conexiones a la API bloqueadas**
El frontend está bloqueando conexiones a `https://api.atrapo.io` debido a la política de Content Security Policy (CSP).

**Error en consola:**
```
Refused to connect because it violates the document's Content Security Policy.
Fetch API cannot load https://api.atrapo.io/api/...
```

### 2. **⚠️ NUEVO: Conexiones WebSocket a Supabase bloqueadas (CRÍTICO)**
El frontend está bloqueando conexiones WebSocket a Supabase Realtime, lo que impide que el chat funcione correctamente cuando se accede como experto.

**Error en consola:**
```
Connecting to 'wss://rveqsehzlvbttlpmsbmi.supabase.co/realtime/v1/websocket' 
violates the following Content Security Policy directive: "connect-src ..."
```

**Síntomas:**
- ✅ El chat funciona como cliente (desde ServiceReview)
- ❌ El chat NO funciona como experto (no se conecta a Supabase)
- ❌ No aparece el mensaje "Conectado a Supabase Realtime"
- ❌ Los mensajes no aparecen en tiempo real

## ✅ SOLUCIÓN

El frontend (`inspecciono.com`) necesita configurar su CSP para permitir:
1. ✅ Conexiones a `https://api.atrapo.io`
2. ✅ **NUEVO:** Conexiones WebSocket y HTTPS a Supabase (`wss://` y `https://rveqsehzlvbttlpmsbmi.supabase.co`)

## 📋 CONFIGURACIÓN REQUERIDA

### Opción 1: Si usas Nginx (Recomendado)

Agrega el header CSP en la configuración de Nginx:

```nginx
add_header Content-Security-Policy "default-src 'self'; script-src 'self' 'unsafe-inline' https://accounts.google.com; style-src 'self' 'unsafe-inline'; img-src 'self' data: https:; font-src 'self' data:; connect-src 'self' http://localhost:7124 ws://localhost:7124 https://newapi-yn9v.onrender.com https://api.atrapo.io https://accounts.google.com https://api.stripe.com https://maps.googleapis.com https://*.googleapis.com https://*.gstatic.com wss://rveqsehzlvbttlpmsbmi.supabase.co https://rveqsehzlvbttlpmsbmi.supabase.co;";
```

**✅ IMPORTANTE:** Agregar `wss://rveqsehzlvbttlpmsbmi.supabase.co` y `https://rveqsehzlvbttlpmsbmi.supabase.co` a `connect-src` para permitir conexiones WebSocket y HTTPS a Supabase Realtime.

**Ubicación del archivo:** `/etc/nginx/sites-available/inspecciono.com` o similar

**Después de cambiar:**
```bash
sudo nginx -t  # Verificar configuración
sudo systemctl reload nginx  # Recargar Nginx
```

### Opción 2: Si usas Next.js

En `next.config.js`:

```javascript
module.exports = {
  async headers() {
    return [
      {
        source: '/:path*',
        headers: [
          {
            key: 'Content-Security-Policy',
            value: "default-src 'self'; script-src 'self' 'unsafe-inline' https://accounts.google.com; style-src 'self' 'unsafe-inline'; img-src 'self' data: https:; font-src 'self' data:; connect-src 'self' http://localhost:7124 ws://localhost:7124 https://newapi-yn9v.onrender.com https://api.atrapo.io https://accounts.google.com https://api.stripe.com https://maps.googleapis.com https://*.googleapis.com https://*.gstatic.com wss://rveqsehzlvbttlpmsbmi.supabase.co https://rveqsehzlvbttlpmsbmi.supabase.co;"
          }
        ]
      }
    ]
  }
}
```

### Opción 3: Si usas Vite/React

En `vite.config.js` o en el servidor de desarrollo:

```javascript
export default {
  server: {
    headers: {
      'Content-Security-Policy': "default-src 'self'; script-src 'self' 'unsafe-inline' https://accounts.google.com; style-src 'self' 'unsafe-inline'; img-src 'self' data: https:; font-src 'self' data:; connect-src 'self' http://localhost:7124 ws://localhost:7124 https://newapi-yn9v.onrender.com https://api.atrapo.io https://accounts.google.com https://api.stripe.com https://maps.googleapis.com https://*.googleapis.com https://*.gstatic.com wss://rveqsehzlvbttlpmsbmi.supabase.co https://rveqsehzlvbttlpmsbmi.supabase.co;"
    }
  }
}
```

### Opción 4: Meta Tag en HTML (No recomendado para producción)

Si no puedes configurar headers del servidor, puedes usar un meta tag en `index.html`:

```html
<meta http-equiv="Content-Security-Policy" content="default-src 'self'; script-src 'self' 'unsafe-inline' https://accounts.google.com; style-src 'self' 'unsafe-inline'; img-src 'self' data: https:; font-src 'self' data:; connect-src 'self' http://localhost:7124 ws://localhost:7124 https://newapi-yn9v.onrender.com https://api.atrapo.io https://accounts.google.com https://api.stripe.com https://maps.googleapis.com https://*.googleapis.com https://*.gstatic.com wss://rveqsehzlvbttlpmsbmi.supabase.co https://rveqsehzlvbttlpmsbmi.supabase.co;">
```

⚠️ **Nota:** Los meta tags tienen limitaciones y no funcionan para todas las directivas. Es mejor usar headers del servidor.

## 🔍 DIRECTIVA CRÍTICA

La directiva más importante es `connect-src`, que debe incluir:

```
connect-src 'self' 
  http://localhost:7124 
  ws://localhost:7124 
  https://newapi-yn9v.onrender.com 
  https://api.atrapo.io 
  https://accounts.google.com 
  https://api.stripe.com 
  https://maps.googleapis.com 
  https://*.googleapis.com 
  https://*.gstatic.com 
  wss://rveqsehzlvbttlpmsbmi.supabase.co 
  https://rveqsehzlvbttlpmsbmi.supabase.co;
```

Esto permite:
- ✅ Conexiones al mismo origen (`'self'`)
- ✅ Conexiones de desarrollo local (`http://localhost:7124`, `ws://localhost:7124`)
- ✅ Conexiones a la API de producción (`https://newapi-yn9v.onrender.com`, `https://api.atrapo.io`)
- ✅ Conexiones a Google OAuth y servicios (`https://accounts.google.com`, `https://maps.googleapis.com`, etc.)
- ✅ **CRÍTICO:** Conexiones WebSocket y HTTPS a Supabase Realtime (`wss://rveqsehzlvbttlpmsbmi.supabase.co`, `https://rveqsehzlvbttlpmsbmi.supabase.co`)

## 📝 CSP COMPLETO RECOMENDADO

```http
Content-Security-Policy: default-src 'self'; script-src 'self' 'unsafe-inline' https://accounts.google.com; style-src 'self' 'unsafe-inline'; img-src 'self' data: https:; font-src 'self' data:; connect-src 'self' http://localhost:7124 ws://localhost:7124 https://newapi-yn9v.onrender.com https://api.atrapo.io https://accounts.google.com https://api.stripe.com https://maps.googleapis.com https://*.googleapis.com https://*.gstatic.com wss://rveqsehzlvbttlpmsbmi.supabase.co https://rveqsehzlvbttlpmsbmi.supabase.co;
```

### Explicación de cada directiva:

- `default-src 'self'` - Por defecto, solo recursos del mismo origen
- `script-src 'self' 'unsafe-inline' https://accounts.google.com` - Scripts propios, inline (necesario para algunos frameworks), y Google OAuth
- `style-src 'self' 'unsafe-inline'` - Estilos propios e inline
- `img-src 'self' data: https:` - Imágenes propias, data URIs, y cualquier HTTPS
- `font-src 'self' data:` - Fuentes propias y data URIs
- `connect-src 'self' ...` - **CRÍTICO:** Permite fetch/XMLHttpRequest/WebSocket a:
  - Desarrollo local: `http://localhost:7124`, `ws://localhost:7124`
  - API de producción: `https://newapi-yn9v.onrender.com`, `https://api.atrapo.io`
  - Google: `https://accounts.google.com`, `https://maps.googleapis.com`, etc.
  - Stripe: `https://api.stripe.com`
  - **Supabase Realtime (CRÍTICO para chat):** `wss://rveqsehzlvbttlpmsbmi.supabase.co`, `https://rveqsehzlvbttlpmsbmi.supabase.co`

## ✅ VERIFICACIÓN

Después de aplicar los cambios:

1. **Recarga la página** (Ctrl+F5 para limpiar caché)
2. **Abre la consola del navegador** (F12)
3. **Verifica que no haya errores de CSP**
4. **Prueba hacer una petición a la API:**
   ```javascript
   fetch('https://api.atrapo.io/api/auth/mfa/status', {
     headers: { 'Authorization': 'Bearer YOUR_TOKEN' }
   })
   ```

## 🚨 TROUBLESHOOTING

### Si sigue bloqueando:

1. **Verifica que el header CSP esté presente:**
   - Abre DevTools → Network
   - Selecciona cualquier petición
   - Ve a la pestaña "Headers"
   - Busca "Content-Security-Policy" en Response Headers

2. **Verifica que los dominios necesarios estén en `connect-src`:**
   - El CSP debe incluir: `connect-src ... https://api.atrapo.io ...`
   - **CRÍTICO:** Debe incluir `wss://rveqsehzlvbttlpmsbmi.supabase.co` y `https://rveqsehzlvbttlpmsbmi.supabase.co` para que el chat funcione
   - Si falta Supabase, verás el error: `Connecting to 'wss://rveqsehzlvbttlpmsbmi.supabase.co' violates the following Content Security Policy directive`

3. **Limpia la caché del navegador:**
   - Ctrl+Shift+Delete → Limpiar caché
   - O usa modo incógnito para probar

4. **Verifica que no haya múltiples headers CSP:**
   - Si hay múltiples headers CSP, el navegador usa el más restrictivo
   - Asegúrate de tener solo uno

## 📞 CONTACTO

Si tienes problemas configurando el CSP, contacta al equipo de backend para más ayuda.

---

---

## 🚨 PROBLEMA ESPECÍFICO: Chat Pre-Contratación como Experto

### Síntomas:
- ✅ El chat funciona correctamente cuando accedes como **cliente** (desde ServiceReview)
- ❌ El chat **NO funciona** cuando accedes como **experto**
- ❌ No aparece el mensaje "✅ Conectado a Supabase Realtime"
- ❌ Los mensajes no aparecen en tiempo real
- ❌ Error en consola: `Connecting to 'wss://rveqsehzlvbttlpmsbmi.supabase.co' violates CSP`

### Causa:
La CSP está bloqueando las conexiones WebSocket a Supabase Realtime porque `wss://rveqsehzlvbttlpmsbmi.supabase.co` no está en la directiva `connect-src`.

### Solución:
Agregar a `connect-src`:
- `wss://rveqsehzlvbttlpmsbmi.supabase.co` (WebSocket seguro)
- `https://rveqsehzlvbttlpmsbmi.supabase.co` (HTTPS para API de Supabase)

### Verificación:
Después de actualizar la CSP:
1. Recarga la página (Ctrl+F5)
2. Abre la consola del navegador
3. Debes ver: `✅ [PreHireChat] Conectado a Supabase Realtime`
4. Debe aparecer: `📡 [PreHireChat] Estado de suscripción: SUBSCRIBED`
5. Los mensajes deben aparecer en tiempo real

---

**Última actualización:** 2026-01-26  
**Aplicable a:** Frontend de inspecciono.com  
**Problema crítico:** Chat pre-contratación como experto bloqueado por CSP

