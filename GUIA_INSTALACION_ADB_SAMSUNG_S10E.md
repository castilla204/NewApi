# 📱 Guía: Instalar ADB para Samsung S10e - Depuración USB

## 📋 Requisitos Previos

- Windows 10/11
- Samsung Galaxy S10e
- Cable USB (preferiblemente el original)
- Conexión a Internet

---

## 🔧 Paso 1: Descargar Android SDK Platform Tools

1. **Descargar Platform Tools:**
   - Ve a: https://developer.android.com/tools/releases/platform-tools
   - O descarga directa: https://dl.google.com/android/repository/platform-tools-latest-windows.zip

2. **Extraer el archivo:**
   - Extrae el ZIP en una carpeta fácil de acceder, por ejemplo:
     ```
     C:\adb
     ```
   - Deberías tener una carpeta con estos archivos:
     - `adb.exe`
     - `fastboot.exe`
     - `AdbWinApi.dll`
     - `AdbWinUsbApi.dll`

---

## 🔌 Paso 2: Instalar Drivers USB de Samsung

1. **Descargar Samsung USB Drivers:**
   - Ve a: https://developer.samsung.com/mobile/android-usb-driver.html
   - O descarga directa: https://developer.samsung.com/downloads/file/version/1.7.50.0/Android%20USB%20Driver%20for%20Windows.exe

2. **Instalar los drivers:**
   - Ejecuta el instalador descargado
   - Sigue las instrucciones del asistente
   - Reinicia el PC si es necesario

---

## 📱 Paso 3: Habilitar Opciones de Desarrollador en el S10e

1. **Activar Opciones de Desarrollador:**
   - Ve a: **Ajustes** > **Acerca del teléfono**
   - Toca **Número de compilación** 7 veces
   - Verás el mensaje: "Ahora eres desarrollador"

2. **Habilitar Depuración USB:**
   - Ve a: **Ajustes** > **Opciones de desarrollador**
   - Activa **Depuración USB**
   - Activa **Instalar vía USB** (opcional pero recomendado)
   - Activa **Permitir siempre desde este equipo** (cuando aparezca el diálogo)

---

## 🔗 Paso 4: Agregar ADB al PATH de Windows

### Opción A: Agregar al PATH (Recomendado)

1. **Abrir Variables de Entorno:**
   - Presiona `Win + R`
   - Escribe: `sysdm.cpl` y presiona Enter
   - Ve a la pestaña **Opciones avanzadas**
   - Clic en **Variables de entorno**

2. **Agregar al PATH:**
   - En **Variables del sistema**, busca **Path**
   - Clic en **Editar**
   - Clic en **Nuevo**
   - Agrega la ruta donde extrajiste ADB (ej: `C:\adb`)
   - Clic en **Aceptar** en todas las ventanas

3. **Verificar instalación:**
   - Abre PowerShell o CMD
   - Ejecuta: `adb version`
   - Deberías ver la versión de ADB

### Opción B: Usar desde la carpeta (Más simple)

- Simplemente abre PowerShell/CMD en la carpeta donde extrajiste ADB
- Ejecuta los comandos desde ahí

---

## ✅ Paso 5: Conectar y Verificar el Dispositivo

1. **Conectar el S10e:**
   - Conecta el cable USB al PC y al teléfono
   - En el teléfono, cuando aparezca el diálogo "Permitir depuración USB":
     - ✅ Marca **Permitir siempre desde este equipo**
     - Clic en **Permitir**

2. **Verificar conexión:**
   - Abre PowerShell o CMD
   - Ejecuta: `adb devices`
   - Deberías ver algo como:
     ```
     List of devices attached
     R58M90XXXXX    device
     ```
   - Si ves `unauthorized`, acepta el diálogo en el teléfono

---

## 🛠️ Comandos ADB Útiles

```bash
# Ver dispositivos conectados
adb devices

# Reiniciar ADB (si hay problemas)
adb kill-server
adb start-server

# Ver información del dispositivo
adb shell getprop ro.product.model

# Instalar una APK
adb install ruta/al/archivo.apk

# Ver logs en tiempo real
adb logcat

# Reiniciar el dispositivo
adb reboot
```

---

## ❌ Solución de Problemas

### El dispositivo no aparece en `adb devices`

1. **Verificar drivers:**
   - Ve a **Administrador de dispositivos** (Win + X > Administrador de dispositivos)
   - Busca tu dispositivo (puede aparecer como "Samsung Android Phone" o con un signo de exclamación)
   - Si tiene signo de exclamación, clic derecho > **Actualizar controlador** > **Buscar automáticamente**

2. **Cambiar modo USB:**
   - En el S10e: **Ajustes** > **Conexiones** > **USB**
   - Cambia a **Transferencia de archivos (MTP)** o **PTP**

3. **Reiniciar ADB:**
   ```bash
   adb kill-server
   adb start-server
   adb devices
   ```

### Error "device unauthorized"

- Acepta el diálogo en el teléfono cuando aparezca
- Marca "Permitir siempre desde este equipo"
- Ejecuta `adb devices` de nuevo

### El dispositivo aparece como "offline"

- Desconecta y vuelve a conectar el cable
- Reinicia ADB: `adb kill-server && adb start-server`
- Verifica que la depuración USB esté activada

---

## 📝 Notas Importantes

- ✅ Usa el cable USB original o uno de buena calidad
- ✅ Algunos cables solo cargan, no transfieren datos
- ✅ Si usas Windows Defender, puede que necesites permitir ADB
- ✅ Mantén actualizados los drivers de Samsung

---

## 🔗 Enlaces Útiles

- **Android SDK Platform Tools:** https://developer.android.com/tools/releases/platform-tools
- **Samsung USB Drivers:** https://developer.samsung.com/mobile/android-usb-driver.html
- **Documentación ADB:** https://developer.android.com/tools/adb

---

¡Listo! Tu Samsung S10e debería estar listo para depuración USB. 🎉
