# 📥 Cómo Descargar Credenciales JSON desde Google Cloud Console

## 🎯 Pasos Detallados

### Paso 1: Ir a la Consola de Google Cloud

1. Abre tu navegador y ve a:
   ```
   https://console.cloud.google.com/
   ```

2. **Asegúrate de estar en el proyecto correcto**:
   - En la parte superior, verifica que el proyecto seleccionado sea: **grup-441318**
   - Si no, haz clic en el selector de proyectos y selecciona `grup-441318`

### Paso 2: Ir a Credenciales

1. En el menú lateral izquierdo, busca **"APIs y servicios"** o **"APIs & Services"**
2. Haz clic en **"Credenciales"** o **"Credentials"**

   O directamente ve a:
   ```
   https://console.cloud.google.com/apis/credentials?project=grup-441318
   ```

### Paso 3: Crear o Usar Cuenta de Servicio

Tienes dos opciones:

#### Opción A: Usar Cuenta de Servicio Existente

1. En la sección **"Cuentas de servicio"** o **"Service accounts"**, busca una cuenta existente
2. Si ya tienes una que usa Secret Manager, úsala
3. Si no, ve a la **Opción B** para crear una nueva

#### Opción B: Crear Nueva Cuenta de Servicio

1. Haz clic en **"Crear credenciales"** o **"Create credentials"** (botón azul arriba)
2. Selecciona **"Cuenta de servicio"** o **"Service account"**
3. Completa el formulario:
   - **Nombre**: `secret-manager-dev` (o el que prefieras)
   - **ID**: Se genera automáticamente
   - **Descripción**: `Cuenta de servicio para desarrollo local`
4. Haz clic en **"Crear y continuar"** o **"Create and continue"**

### Paso 4: Asignar Permisos

1. En **"Otorgar acceso a este proyecto"** o **"Grant this service account access to project"**:
   - Selecciona el rol: **"Secret Manager Secret Accessor"** o **"roles/secretmanager.secretAccessor"**
   - Este rol permite leer secretos de Secret Manager
2. Haz clic en **"Continuar"** o **"Continue"**
3. Opcional: Agrega usuarios que puedan usar esta cuenta (puedes saltar esto)
4. Haz clic en **"Listo"** o **"Done"**

### Paso 5: Crear y Descargar la Clave JSON

1. En la lista de cuentas de servicio, haz clic en la cuenta que acabas de crear (o la que quieres usar)
2. Ve a la pestaña **"Claves"** o **"Keys"**
3. Haz clic en **"Agregar clave"** o **"Add key"**
4. Selecciona **"Crear nueva clave"** o **"Create new key"**
5. Selecciona el formato: **JSON**
6. Haz clic en **"Crear"** o **"Create"**

### Paso 6: Descargar el Archivo

1. El archivo JSON se descargará automáticamente a tu carpeta de Descargas
2. El nombre del archivo será algo como:
   - `grup-441318-xxxxx-xxxxx.json`
   - O el nombre que le diste a la cuenta de servicio

## 📁 Ubicación del Archivo Descargado

El archivo se guardará en:
```
C:\Users\Diego\Downloads\[nombre-del-archivo].json
```

## 🔧 Siguiente Paso: Configurar el Archivo

Una vez descargado:

1. **Copia el archivo** desde `Downloads` a `C:\`
2. **Renómbralo** a `cloudcredential.json`

O en PowerShell:
```powershell
# Copiar y renombrar
Copy-Item "C:\Users\Diego\Downloads\[nombre-del-archivo].json" "C:\cloudcredential.json"
```

## 🔗 Enlaces Directos

- **Consola de Credenciales**: https://console.cloud.google.com/apis/credentials?project=grup-441318
- **Cuentas de Servicio**: https://console.cloud.google.com/iam-admin/serviceaccounts?project=grup-441318

## ⚠️ Importante

- **NO compartas** este archivo JSON con nadie
- **NO lo subas** a Git o repositorios públicos
- Contiene credenciales que dan acceso a tus secretos
- Si se compromete, elimínalo y crea uno nuevo

## ✅ Verificación

Después de descargar y configurar:

```powershell
# Verificar que existe
Test-Path C:\cloudcredential.json

# Verificar contenido (debe tener project_id: "grup-441318")
Get-Content C:\cloudcredential.json | Select-String "grup-441318"
```

## 🆘 Si Tienes Problemas

### No veo "APIs y servicios"
- Usa el buscador en la parte superior de la consola
- Busca: "credentials" o "credenciales"

### No puedo crear cuenta de servicio
- Verifica que tienes permisos de "Editor" o "Owner" en el proyecto
- Si no, pide a un administrador que te los otorgue

### El archivo no se descarga
- Verifica que no tienes bloqueadores de descargas
- Intenta con otro navegador
- Verifica la carpeta de Descargas

