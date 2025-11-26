# 🔒 Configuración de Content Security Policy (CSP) para Frontend

## 🚨 PROBLEMA ACTUAL

El frontend está bloqueando conexiones a `https://api.atrapo.io` debido a la política de Content Security Policy (CSP).

**Error en consola:**
```
Refused to connect because it violates the document's Content Security Policy.
Fetch API cannot load https://api.atrapo.io/api/...
```

## ✅ SOLUCIÓN

El frontend (`inspecciono.com`) necesita configurar su CSP para permitir conexiones a `https://api.atrapo.io`.

## 📋 CONFIGURACIÓN REQUERIDA

### Opción 1: Si usas Nginx (Recomendado)

Agrega el header CSP en la configuración de Nginx:

```nginx
add_header Content-Security-Policy "default-src 'self'; script-src 'self' 'unsafe-inline' https://accounts.google.com; style-src 'self' 'unsafe-inline'; img-src 'self' data: https:; font-src 'self' data:; connect-src 'self' https://api.atrapo.io https://accounts.google.com;";
```

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
            value: "default-src 'self'; script-src 'self' 'unsafe-inline' https://accounts.google.com; style-src 'self' 'unsafe-inline'; img-src 'self' data: https:; font-src 'self' data:; connect-src 'self' https://api.atrapo.io https://accounts.google.com;"
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
      'Content-Security-Policy': "default-src 'self'; script-src 'self' 'unsafe-inline' https://accounts.google.com; style-src 'self' 'unsafe-inline'; img-src 'self' data: https:; font-src 'self' data:; connect-src 'self' https://api.atrapo.io https://accounts.google.com;"
    }
  }
}
```

### Opción 4: Meta Tag en HTML (No recomendado para producción)

Si no puedes configurar headers del servidor, puedes usar un meta tag en `index.html`:

```html
<meta http-equiv="Content-Security-Policy" content="default-src 'self'; script-src 'self' 'unsafe-inline' https://accounts.google.com; style-src 'self' 'unsafe-inline'; img-src 'self' data: https:; font-src 'self' data:; connect-src 'self' https://api.atrapo.io https://accounts.google.com;">
```

⚠️ **Nota:** Los meta tags tienen limitaciones y no funcionan para todas las directivas. Es mejor usar headers del servidor.

## 🔍 DIRECTIVA CRÍTICA

La directiva más importante es `connect-src`, que debe incluir:

```
connect-src 'self' https://api.atrapo.io https://accounts.google.com;
```

Esto permite:
- ✅ Conexiones al mismo origen (`'self'`)
- ✅ Conexiones a la API: `https://api.atrapo.io`
- ✅ Conexiones a Google OAuth: `https://accounts.google.com`

## 📝 CSP COMPLETO RECOMENDADO

```http
Content-Security-Policy: default-src 'self'; script-src 'self' 'unsafe-inline' https://accounts.google.com; style-src 'self' 'unsafe-inline'; img-src 'self' data: https:; font-src 'self' data:; connect-src 'self' https://api.atrapo.io https://accounts.google.com;
```

### Explicación de cada directiva:

- `default-src 'self'` - Por defecto, solo recursos del mismo origen
- `script-src 'self' 'unsafe-inline' https://accounts.google.com` - Scripts propios, inline (necesario para algunos frameworks), y Google OAuth
- `style-src 'self' 'unsafe-inline'` - Estilos propios e inline
- `img-src 'self' data: https:` - Imágenes propias, data URIs, y cualquier HTTPS
- `font-src 'self' data:` - Fuentes propias y data URIs
- `connect-src 'self' https://api.atrapo.io https://accounts.google.com` - **CRÍTICO:** Permite fetch/XMLHttpRequest a la API y Google

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

2. **Verifica que `api.atrapo.io` esté en `connect-src`:**
   - El CSP debe incluir exactamente: `connect-src ... https://api.atrapo.io ...`

3. **Limpia la caché del navegador:**
   - Ctrl+Shift+Delete → Limpiar caché
   - O usa modo incógnito para probar

4. **Verifica que no haya múltiples headers CSP:**
   - Si hay múltiples headers CSP, el navegador usa el más restrictivo
   - Asegúrate de tener solo uno

## 📞 CONTACTO

Si tienes problemas configurando el CSP, contacta al equipo de backend para más ayuda.

---

**Última actualización:** 2025-01-XX
**Aplicable a:** Frontend de inspecciono.com

