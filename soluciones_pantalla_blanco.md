# 🔧 Soluciones para Pantalla en Blanco en Android Studio

## 🔍 Diagnóstico Rápido

### 1. Verificar Logcat en Android Studio

1. Abre la pestaña **Logcat** en la parte inferior de Android Studio
2. Filtra por:
   - `Error`
   - `AndroidRuntime`
   - `chromium` (si usas WebView)
3. Busca errores en rojo

### 2. Verificar Consola de Ejecución (Run)

1. Abre la pestaña **Run** en la parte inferior
2. Busca mensajes de error durante la instalación/ejecución
3. Errores comunes:
   - `INSTALL_FAILED`
   - `ActivityNotFoundException`
   - `ClassNotFoundException`

---

## ✅ Soluciones Comunes

### Solución 1: Limpiar y Reconstruir el Proyecto

```
Build > Clean Project
Build > Rebuild Project
```

Luego ejecuta de nuevo la app.

---

### Solución 2: Verificar Configuración de Capacitor

Si estás usando **Capacitor** (veo que tienes carpetas de Capacitor):

1. **Revisa `capacitor.config.ts` o `capacitor.config.js`:**
   ```typescript
   {
     server: {
       url: "http://localhost:4200", // o tu URL de desarrollo
       cleartext: true
     }
   }
   ```

2. **Sincroniza Capacitor:**
   ```bash
   npx cap sync android
   ```

3. **Verifica que el servidor de desarrollo esté corriendo:**
   - Si usas Angular: `ng serve`
   - Si usas React: `npm start`
   - Si usas Vue: `npm run serve`

---

### Solución 3: Verificar MainActivity

Abre `android/app/src/main/java/.../MainActivity.java` o `.kt`:

```java
// Debe tener algo como:
public class MainActivity extends BridgeActivity {
    @Override
    public void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        // ...
    }
}
```

Verifica que esté cargando la URL correcta.

---

### Solución 4: Verificar Permisos de Internet

En `AndroidManifest.xml` debe tener:

```xml
<uses-permission android:name="android.permission.INTERNET" />
<application
    android:usesCleartextTraffic="true"
    ...>
```

---

### Solución 5: Reinstalar la App

```bash
# Desinstalar
adb uninstall com.tu.paquete

# Reinstalar desde Android Studio
```

O desde Android Studio:
- **Run > Edit Configurations**
- Marca **Uninstall apk before installing**

---

### Solución 6: Verificar WebView

Si usas WebView, verifica que esté habilitado:

```bash
# Verificar WebView
adb shell pm list packages | findstr webview
```

---

### Solución 7: Verificar Logs en Tiempo Real

Ejecuta este comando mientras abres la app:

```powershell
.\diagnostico_android_studio.ps1
```

O directamente:
```bash
adb logcat | findstr /i "error exception crash"
```

---

## 🐛 Errores Específicos

### Error: "Web page not available"
- **Causa:** El servidor de desarrollo no está corriendo
- **Solución:** Inicia el servidor (ng serve, npm start, etc.)

### Error: "net::ERR_CLEARTEXT_NOT_PERMITTED"
- **Causa:** Android bloquea HTTP (solo permite HTTPS)
- **Solución:** Agrega `android:usesCleartextTraffic="true"` en AndroidManifest.xml

### Error: "ActivityNotFoundException"
- **Causa:** La actividad principal no está configurada correctamente
- **Solución:** Verifica AndroidManifest.xml y MainActivity

### Pantalla en blanco sin errores
- **Causa:** La app se carga pero no muestra contenido
- **Solución:** 
  1. Verifica la URL en capacitor.config
  2. Verifica que el servidor esté accesible desde el emulador
  3. Usa `10.0.2.2` en lugar de `localhost` en el emulador

---

## 🔍 Comandos Útiles de Diagnóstico

```bash
# Ver todos los logs
adb logcat

# Ver solo errores
adb logcat *:E

# Ver logs de una app específica
adb logcat | findstr "com.tu.paquete"

# Limpiar logs
adb logcat -c

# Ver información del dispositivo
adb shell getprop

# Ver apps instaladas
adb shell pm list packages

# Forzar detener una app
adb shell am force-stop com.tu.paquete
```

---

## 📱 Para Emulador vs Dispositivo Real

### Emulador
- Usa `10.0.2.2` en lugar de `localhost` o `127.0.0.1`
- Ejemplo: `http://10.0.2.2:4200`

### Dispositivo Real (Samsung S10e)
- Usa la IP de tu PC en la red local
- Ejemplo: `http://192.168.1.100:4200`
- Asegúrate de que el firewall permita la conexión

---

## 🚀 Pasos de Depuración Recomendados

1. ✅ Ejecuta `.\diagnostico_android_studio.ps1`
2. ✅ Revisa Logcat en Android Studio
3. ✅ Verifica la consola de Run
4. ✅ Limpia y reconstruye el proyecto
5. ✅ Verifica capacitor.config
6. ✅ Verifica que el servidor esté corriendo
7. ✅ Reinstala la app

---

¿Necesitas ayuda con algún error específico que veas en Logcat?
