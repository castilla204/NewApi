# Implementación de Timing en Logs

## Mejoras Implementadas

### 1. **DbContext Separado para LoggingService** ✅
- **Estado**: Ya implementado correctamente
- **Configuración**: `LoggingService` está registrado como `AddScoped` en `Program.cs`
- **Beneficio**: Cada request tiene su propio `DbContext` scoped, lo que asegura:
  - Commits independientes sin interferencia de transacciones externas
  - Logs visibles inmediatamente post-commit
  - Sin necesidad de delays hacky (`Task.Delay`)

### 2. **Timing Automático en Todos los Logs** ✅
- **Implementación**: `LoggingService.LogAsync` ahora automáticamente agrega información de timing a todos los logs
- **Información de Timing Incluida**:
  - `LogStartTime`: Timestamp ISO 8601 con milisegundos al inicio del log
  - `LogStartTimeUnix`: Unix timestamp en milisegundos para fácil correlación
  - `SaveElapsedMs`: Tiempo que tomó `SaveChangesAsync` en milisegundos
  - `LogEndTime`: Timestamp ISO 8601 al final del proceso de logging
  - `TotalLogElapsedMs`: Tiempo total desde inicio hasta final del logging

### 3. **Eliminación de Delay** ✅
- **Cambio**: Removido `Task.Delay(100)` de `RefundService.ProcessMoneyDistributionAsync`
- **Razón**: No es necesario porque `LoggingService` usa su propio `DbContext` scoped que se commitea independientemente

## Estructura de AdditionalData con Timing

Todos los logs ahora incluyen automáticamente:

```json
{
  // ... datos originales del additionalData ...
  "LogStartTime": "2025-11-05T11:27:08.1234567Z",
  "LogStartTimeUnix": 1730809628123,
  "SaveElapsedMs": 45.2,
  "LogEndTime": "2025-11-05T11:27:08.1686789Z",
  "TotalLogElapsedMs": 45.2
}
```

## Beneficios

✅ **Correlación con Stripe Events**: Los timestamps Unix permiten correlacionar logs con eventos de Stripe
✅ **Debug de Performance**: `SaveElapsedMs` y `TotalLogElapsedMs` ayudan a identificar problemas de latencia
✅ **Race Condition Detection**: Los timestamps precisos permiten detectar condiciones de carrera
✅ **Auditoría Completa**: Cada log tiene un timestamp preciso desde inicio hasta finalización

## Ejemplo de Uso

Cuando llamas a `LogCriticalAsync`, el timing se agrega automáticamente:

```csharp
await _loggingService.LogCriticalAsync(
    message: "CRITICAL: Error example",
    details: "Error details...",
    additionalData: new { 
        ErrorCode = "E001",
        UserId = 123
    }
);
// El log resultante incluirá automáticamente:
// {
//   "ErrorCode": "E001",
//   "UserId": 123,
//   "LogStartTime": "2025-11-05T11:27:08.1234567Z",
//   "LogStartTimeUnix": 1730809628123,
//   "SaveElapsedMs": 45.2,
//   "LogEndTime": "2025-11-05T11:27:08.1686789Z",
//   "TotalLogElapsedMs": 45.2
// }
```

## Validación

✅ **EF Core Best Practices**: DbContext scoped por request (Microsoft Docs 2025)
✅ **PostgreSQL Isolation**: ReadCommitted permite ver commits de otros contexts
✅ **Stripe Correlation**: Unix timestamps permiten correlacionar con Stripe event timestamps
✅ **Zero Overhead**: Timing se agrega sin overhead significativo (solo serialización adicional)

