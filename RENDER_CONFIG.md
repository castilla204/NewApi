# Configuración de Render.com para .NET/C# API

## Verificaciones Requeridas en Render.com Dashboard

### 1. Build Command
```
dotnet publish -c Release -o ./publish
```

O si Render detecta automáticamente el Dockerfile (que es el caso), el build se hace automáticamente.

### 2. Start Command
Si usas Docker (recomendado, ya que existe Dockerfile):
```
dotnet newApi.dll
```

Si NO usas Docker y usas el build command con publish:
```
dotnet ./publish/newApi.dll
```

### 3. Health Check Path
```
/health
```

### 4. Variables de Entorno
Asegúrate de que estas variables estén configuradas:
- `ASPNETCORE_ENVIRONMENT=Production` (ya está en Dockerfile)
- `PORT` - Render lo establece automáticamente, NO configurar manualmente
- `ASPNETCORE_URLS` - NO configurar manualmente, Program.cs lo configura automáticamente

### 5. Runtime
- Si usas Docker: Render detecta automáticamente el Dockerfile
- Si NO usas Docker: Seleccionar "Docker" como runtime y Render usará el Dockerfile

## Diagnóstico de Problemas

### Si solo /health funciona pero /api no:

1. **Verificar logs en Render Dashboard**:
   - Buscar el mensaje "📋 ENDPOINTS REGISTRADOS DESPUÉS DE MapControllers()"
   - Verificar que aparezcan endpoints con ruta "/api/*"
   - Si no aparecen, hay un problema con el registro de controladores

2. **Verificar logs de requests a /api**:
   - Buscar mensajes con prefijo "[API-DIAG]"
   - Estos logs muestran si las requests están llegando y qué está pasando

3. **Verificar configuración de CORS**:
   - Los endpoints /api deben estar permitidos en la política CORS
   - Verificar que el origen del frontend esté en la lista de orígenes permitidos

4. **Verificar autenticación**:
   - Algunos endpoints /api requieren autenticación
   - Verificar que los endpoints públicos estén marcados con [AllowAnonymous]

## Referencias

- [Render.com Troubleshooting](https://render.com/docs/troubleshooting-deploys)
- [Render.com Docker Guide](https://render.com/docs/docker)
- [Render.com Web Services](https://render.com/docs/web-services)
