# 🔧 Modificación: GetSecretValue para Desarrollo vs Producción

## 📋 Objetivo

Modificar la función `GetSecretValue` para que use secretos diferentes según el entorno:
- **Desarrollo**: Intenta obtener secretos con sufijo `-dev` (ej: `jwt-key-dev`)
- **Producción**: Usa secretos sin sufijo (ej: `jwt-key`)

## 🔄 Estrategia

1. En **desarrollo**: Intenta `{secretName}-dev`, si no existe, usa `{secretName}` como fallback
2. En **producción**: Usa directamente `{secretName}`

Esto permite:
- ✅ Secretos separados para dev/prod
- ✅ Compatibilidad hacia atrás (si no existen secretos `-dev`, usa los normales)
- ✅ Flexibilidad para migrar gradualmente

## 📝 Código Modificado

### Función GetSecretValue Actualizada

Reemplaza la función `GetSecretValue` (línea ~174) con esta versión:

```csharp
// Función para obtener secretos
// En desarrollo: intenta secretos con sufijo -dev, luego sin sufijo
// En producción: usa secretos sin sufijo
string? GetSecretValue(string secretName, string? defaultValue = null)
{
    // Intentar usar Secret Manager si está disponible (tanto en desarrollo como producción)
    if (secretClient != null && secretManagerAvailable)
    {
        var projectId = "grup-441318";
        var secretLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger("Program");
        
        // Determinar qué nombres de secretos intentar según el entorno
        var secretNamesToTry = new List<string>();
        
        if (isDevelopment)
        {
            // En desarrollo: intentar primero con -dev, luego sin sufijo
            secretNamesToTry.Add($"{secretName}-dev");
            secretNamesToTry.Add(secretName);
            secretLogger.LogInformation($"🔧 DESARROLLO: Intentando secretos: {string.Join(" -> ", secretNamesToTry)}");
        }
        else
        {
            // En producción: usar directamente el nombre sin sufijo
            secretNamesToTry.Add(secretName);
            secretLogger.LogInformation($"🏭 PRODUCCIÓN: Usando secreto: {secretName}");
        }
        
        // Intentar cada nombre de secreto en orden
        foreach (var secretNameToTry in secretNamesToTry)
        {
            try
            {
                var secretPath = $"projects/{projectId}/secrets/{secretNameToTry}/versions/latest";
                secretLogger.LogInformation($"Intentando obtener secreto: {secretNameToTry} desde {secretPath}");
                
                // Configurar call settings con timeout y reintentos mejorados
                var callSettings = CallSettings.FromRetry(
                    RetrySettings.FromExponentialBackoff(
                        maxAttempts: 3,
                        initialBackoff: TimeSpan.FromSeconds(5),
                        maxBackoff: TimeSpan.FromSeconds(20),
                        backoffMultiplier: 2.0,
                        retryFilter: RetrySettings.FilterForStatusCodes(
                            Grpc.Core.StatusCode.Unavailable, 
                            Grpc.Core.StatusCode.DeadlineExceeded,
                            Grpc.Core.StatusCode.Internal,
                            Grpc.Core.StatusCode.ResourceExhausted
                        )
                    )
                ).WithTimeout(TimeSpan.FromSeconds(60));
                
                var startTime = DateTime.UtcNow;
                var secretVersion = secretClient.AccessSecretVersion(secretPath, callSettings: callSettings);
                var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
                
                secretLogger.LogInformation($"✅ Secreto {secretNameToTry} obtenido exitosamente en {duration}ms");
                return secretVersion.Payload.Data.ToStringUtf8();
            }
            catch (Grpc.Core.RpcException rpcEx)
            {
                // Si el secreto no existe (NotFound), intentar el siguiente
                if (rpcEx.StatusCode == Grpc.Core.StatusCode.NotFound)
                {
                    secretLogger.LogWarning($"⚠️ Secreto {secretNameToTry} no encontrado, intentando siguiente...");
                    continue; // Intentar siguiente nombre
                }
                
                // Para otros errores, marcar como no disponible y retornar
                if (secretManagerAvailable)
                {
                    secretManagerAvailable = false;
                    secretLogger.LogError($"ERROR gRPC al obtener secreto {secretNameToTry}:");
                    secretLogger.LogError($"  Status Code: {rpcEx.StatusCode}");
                    secretLogger.LogError($"  Status Detail: {rpcEx.Status.Detail}");
                    secretLogger.LogWarning("Secret Manager no está disponible. Usando solo variables de entorno.");
                }
                return defaultValue;
            }
            catch (Exception ex)
            {
                // Para errores inesperados, marcar como no disponible
                if (secretManagerAvailable)
                {
                    secretManagerAvailable = false;
                    secretLogger.LogError($"ERROR inesperado al obtener secreto {secretNameToTry}: {ex.GetType().Name} - {ex.Message}");
                    secretLogger.LogWarning("Secret Manager no está disponible. Usando solo variables de entorno.");
                }
                return defaultValue;
            }
        }
        
        // Si llegamos aquí, ningún secreto fue encontrado
        secretLogger.LogWarning($"⚠️ Ningún secreto encontrado para: {secretName} (intentados: {string.Join(", ", secretNamesToTry)})");
        return defaultValue;
    }
    
    // Si Secret Manager no está disponible, usar valor por defecto
    return defaultValue;
}
```

## 🔑 Secretos a Crear en GCSM

Para desarrollo, crea estos secretos en Google Cloud Secret Manager con sufijo `-dev`:

```bash
# Ejemplo: Crear secretos de desarrollo
gcloud secrets create jwt-key-dev --project=grup-441318
gcloud secrets create jwt-issuer-dev --project=grup-441318
gcloud secrets create jwt-audience-dev --project=grup-441318
gcloud secrets create postgres-password-dev --project=grup-441318
# ... etc para todos los secretos que necesites en desarrollo
```

O usa el script que crearemos para crear todos los secretos de desarrollo.

## ✅ Ventajas de este Enfoque

1. **Separación clara**: Secretos diferentes para dev/prod
2. **Compatibilidad**: Si no existe `-dev`, usa el secreto normal
3. **Migración gradual**: Puedes crear secretos `-dev` poco a poco
4. **Seguridad**: No mezclas secretos de producción en desarrollo
5. **Flexibilidad**: Fácil de extender para otros entornos (staging, test, etc.)

## 📋 Checklist de Implementación

- [ ] Modificar función `GetSecretValue` en Program.cs
- [ ] Crear secretos `-dev` en Google Cloud Secret Manager
- [ ] Probar en desarrollo local (debe usar `-dev`)
- [ ] Verificar que producción sigue usando secretos normales
- [ ] Documentar qué secretos existen para cada entorno

## 🚀 Próximos Pasos

1. Aplicar la modificación al código
2. Crear script para crear secretos `-dev` en GCSM
3. Probar en desarrollo local
4. Verificar logs para confirmar que usa los secretos correctos

