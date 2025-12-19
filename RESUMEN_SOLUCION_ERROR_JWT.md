# 🚨 Solución Rápida: Error "JWT Key not found" en Desarrollo

## ❌ Error Actual
```
System.InvalidOperationException: 'JWT Key not found'
at Program.<Main>$(String[] args) in Program.cs:line 28
```

## ✅ Solución en 3 Pasos

### Paso 1: Instalar DotNetEnv

Abre una terminal en la carpeta del proyecto y ejecuta:

```bash
dotnet add package DotNetEnv
```

### Paso 2: Crear archivo `.env`

Crea un archivo llamado `.env` en la raíz del proyecto (`C:\Users\Diego\Downloads\App\App\NewApi\.env`) con este contenido:

```env
JWT_KEY=ThisIsA32CharacterLongSecretKey12345678901234567890
JWT_ISSUER=newApi
JWT_AUDIENCE=newApi
```

**⚠️ IMPORTANTE**: 
- El `JWT_KEY` debe tener **mínimo 32 caracteres** (el código valida esto)
- **NO** commitees este archivo a Git
- Agrega `.env` a `.gitignore`

### Paso 3: Modificar Program.cs

Abre `Program.cs` y:

1. **Agrega este `using` al inicio** (con los otros usings):
   ```csharp
   using DotNetEnv;
   ```

2. **Agrega este código JUSTO DESPUÉS de** `var builder = WebApplication.CreateBuilder(args);`:

   ```csharp
   var builder = WebApplication.CreateBuilder(args);

   // 🔧 CARGAR .env EN DESARROLLO
   if (builder.Environment.IsDevelopment())
   {
       var envPath = Path.Combine(Directory.GetCurrentDirectory(), ".env");
       if (File.Exists(envPath))
       {
           Env.Load(envPath);
           Console.WriteLine($"✅ Archivo .env cargado desde: {Path.GetFullPath(envPath)}");
       }
       else
       {
           Console.WriteLine($"⚠️ Archivo .env no encontrado en: {Path.GetFullPath(envPath)}");
       }
   }

   // Configurar logging básico
   builder.Logging.ClearProviders();
   // ... resto del código continúa igual ...
   ```

---

## 🎯 Resultado Esperado

Después de aplicar estos cambios, al ejecutar la aplicación deberías ver:

```
✅ Archivo .env cargado desde: C:\Users\Diego\Downloads\App\App\NewApi\.env
✅ JWT Key length validated: XX bytes (XXX bits) - SECURE
```

Y el error **NO** debería aparecer.

---

## 🔐 Obtener JWT_KEY Real (Opcional)

Si quieres usar el mismo `JWT_KEY` que en producción:

```bash
# Desde tu máquina local (con gcloud configurado)
gcloud secrets versions access latest --secret=jwt-key --project=grup-441318
```

Copia el valor y úsalo en tu `.env`.

---

## 📋 Checklist

- [ ] Instalé DotNetEnv: `dotnet add package DotNetEnv`
- [ ] Creé archivo `.env` con `JWT_KEY` (mínimo 32 caracteres)
- [ ] Agregué `using DotNetEnv;` en Program.cs
- [ ] Agregué código para cargar `.env` en desarrollo
- [ ] Agregué `.env` a `.gitignore`
- [ ] Probé ejecutar la aplicación y funciona

---

## 📚 Documentación Completa

Para más detalles y opciones alternativas, ver:
- `/root/newapi/SOLUCION_DESARROLLO_LOCAL.md` - Soluciones detalladas
- `/root/newapi/PARCHES_PROGRAM_CS.md` - Código completo del parche
- `/root/newapi/setup-desarrollo-local.ps1` - Script de configuración automática

---

## ❓ ¿Sigue sin funcionar?

1. Verifica que el archivo `.env` está en la raíz del proyecto
2. Verifica que `JWT_KEY` tiene al menos 32 caracteres
3. Verifica que agregaste el código para cargar `.env` en Program.cs
4. Verifica que instalaste DotNetEnv correctamente
5. Revisa la consola para ver mensajes de error adicionales

---

**¿Necesitas ayuda?** Revisa la documentación completa o comparte el mensaje de error específico.

