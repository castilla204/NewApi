# 🔍 ANÁLISIS COMPLETO: MIGRACIÓN POSTGRESQL → SUPABASE (PgBouncer)

## 📋 RESUMEN EJECUTIVO

**Problema Principal**: Supabase usa **PgBouncer Transaction Pooler** que **NO soporta savepoints automáticos** que EF Core intenta crear cuando se combina `EnableRetryOnFailure` con transacciones manuales.

**Error Típico**: 
```
The configured execution strategy 'NpgsqlRetryingExecutionStrategy' does not support user-initiated transactions.
```

---

## 🔴 POR QUÉ FALLA

### **1. Diferencia Clave: PostgreSQL Directo vs Supabase PgBouncer**

| Aspecto | PostgreSQL Directo | Supabase (PgBouncer) |
|---------|-------------------|---------------------|
| **Conexión** | Directa a PostgreSQL | A través de PgBouncer (pooler) |
| **Savepoints** | ✅ Soportados | ❌ **NO soportados en Transaction Mode** |
| **ExecutionStrategy** | ✅ Funciona con transacciones | ❌ **Falla con transacciones manuales** |
| **Prepared Statements** | ✅ Soportados | ❌ **NO soportados (MaxAutoPrepare=0)** |
| **Multiplexing** | ✅ Opcional | ❌ **Debe estar deshabilitado** |

### **2. El Problema Técnico**

Cuando EF Core tiene `EnableRetryOnFailure` habilitado:
1. Cualquier `SaveChangesAsync()` dentro de una transacción manual intenta usar `ExecutionStrategy`
2. `ExecutionStrategy` intenta crear **savepoints automáticos** para poder hacer rollback y retry
3. **PgBouncer Transaction Pooler NO soporta savepoints** (limitación de diseño)
4. Resultado: **Error `InvalidOperationException`**

---

## 🎯 DÓNDE MÁS FALLARÁ

### **✅ YA CORREGIDOS:**
1. ✅ `SubscriptionController.CreateExpertOnboarding` - Eliminada transacción manual
2. ✅ `SubscriptionController.HandleStripeWebhook` (account.updated) - Eliminada transacción manual
3. ✅ `LoggingService.LogAsync` - Eliminada transacción manual
4. ✅ `UserService.GoogleAuth` - Ya no usa ExecutionStrategy con transacciones

### **⚠️ PENDIENTES DE REVISAR (Usan `BeginTransactionAsync` + `ExecutionStrategy`):**

#### **1. `SubscriptionController.cs` - Webhook `account.application.deauthorized`**
```csharp
// Línea 1929
await using var deauthTransaction = await _context.Database.BeginTransactionAsync();
```
**Riesgo**: ⚠️ **ALTO** - Puede fallar con ExecutionStrategy si se agrega SaveChangesAsync
**Solución**: Eliminar transacción manual, usar recovery con `IServiceScopeFactory`

#### **2. `SubscriptionController.cs` - Webhook `transfer.failed`**
```csharp
// Línea 2268
await using var transferFailedTransaction = await _context.Database.BeginTransactionAsync();
```
**Riesgo**: ⚠️ **ALTO** - Puede fallar con ExecutionStrategy si se agrega SaveChangesAsync
**Solución**: Eliminar transacción manual, usar recovery con `IServiceScopeFactory`

#### **3. `SubscriptionController.cs` - `HandleStripeWebhook` (método interno)**
```csharp
// Línea 2989
var strategy = _context.Database.CreateExecutionStrategy();
await strategy.ExecuteAsync(async () =>
{
    using var transaction = await _context.Database.BeginTransactionAsync();
```
**Riesgo**: 🔴 **CRÍTICO** - Usa ExecutionStrategy + transacción manual
**Solución**: Eliminar ExecutionStrategy, mantener solo transacción

#### **4. `RefundService.cs`**
```csharp
// Línea 554
var stateStrategy = _context.Database.CreateExecutionStrategy();
await stateStrategy.ExecuteAsync(async () =>
{
    using var stateTransaction = await _context.Database.BeginTransactionAsync();
```
**Riesgo**: 🔴 **CRÍTICO** - Usa ExecutionStrategy + transacción manual
**Solución**: Eliminar ExecutionStrategy, mantener solo transacción

#### **5. `AccountDeletionService.cs`**
```csharp
// Línea 157
var strategy = _context.Database.CreateExecutionStrategy();
return await strategy.ExecuteAsync(async () =>
{
    using var transaction = await _context.Database.BeginTransactionAsync();
```
**Riesgo**: 🔴 **CRÍTICO** - Usa ExecutionStrategy + transacción manual
**Solución**: Eliminar ExecutionStrategy, mantener solo transacción

#### **6. `DisputeController.cs`**
```csharp
// Línea 114
var strategy = _context.Database.CreateExecutionStrategy();
return await strategy.ExecuteAsync(async () =>
{
    using var transaction = await _context.Database.BeginTransactionAsync();
```
**Riesgo**: 🔴 **CRÍTICO** - Usa ExecutionStrategy + transacción manual
**Solución**: Eliminar ExecutionStrategy, mantener solo transacción

#### **7. `SearchHireController.cs`**
```csharp
// Línea 791
await using var transaction = await _context.Database.BeginTransactionAsync();
// (dentro de strategy.ExecuteAsync)
```
**Riesgo**: 🔴 **CRÍTICO** - Usa ExecutionStrategy + transacción manual
**Solución**: Eliminar ExecutionStrategy, mantener solo transacción

#### **8. `SubscriptionService.cs`**
```csharp
// Línea 158
var strategy = _context.Database.CreateExecutionStrategy();
await strategy.ExecuteAsync(async () =>
{
    using var transaction = await _context.Database.BeginTransactionAsync();
```
**Riesgo**: 🔴 **CRÍTICO** - Usa ExecutionStrategy + transacción manual
**Solución**: Eliminar ExecutionStrategy, mantener solo transacción

#### **3. `AppointmentService.cs` - Múltiples métodos:**
- `CreateAppointmentAsync` (línea 363) - ✅ **YA TIENE RECOVERY** pero usa transacción
- `ProposeAppointmentAsync` (línea 798) - ✅ **YA TIENE RECOVERY** pero usa transacción
- `ConfirmAppointmentAsync` (línea 1366) - ✅ **YA TIENE RECOVERY** pero usa transacción
- `CancelAppointmentAsync` (línea 1933) - ✅ **YA TIENE RECOVERY** pero usa transacción
- `RescheduleAppointmentAsync` (línea 2658) - ✅ **YA TIENE RECOVERY** pero usa transacción
- `SubmitExpertReportAsync` (línea 6034) - ✅ **YA TIENE RECOVERY** pero usa transacción

**Riesgo**: ⚠️ **MEDIO** - Tienen recovery pero aún usan transacciones que pueden fallar
**Solución**: Las transacciones son necesarias para `FOR UPDATE`, pero deben manejar `ObjectDisposedException`

#### **4. `SearchController.cs` - `CreateSearchWithHire`**
```csharp
// Línea 398
var strategy = _context.Database.CreateExecutionStrategy();
await strategy.ExecuteAsync(async () =>
{
    using var transaction = await _context.Database.BeginTransactionAsync();
    // ... crea sesión de Stripe ...
    await transaction.CommitAsync();
```
**Riesgo**: ⚠️ **MEDIO-ALTO** - Usa ExecutionStrategy con transacción manual
**Nota**: Actualmente NO hace `SaveChangesAsync` dentro de la transacción, pero el patrón es problemático
**Solución**: Eliminar ExecutionStrategy, mantener solo transacción manual (o eliminar transacción si no es necesaria)

#### **5. Otros servicios que pueden tener problemas:**
- `RefundService.cs` - Revisar si usa transacciones
- `AccountDeletionService.cs` - Revisar si usa transacciones
- `SubscriptionService.cs` - Revisar si usa transacciones
- `DisputeController.cs` - Revisar si usa transacciones
- `SearchHireController.cs` - Revisar si usa transacciones

---

## 🔧 MÉTODOS QUE CAUSAN EL FALLO

### **Patrón Problemático #1: ExecutionStrategy + BeginTransactionAsync**
```csharp
// ❌ ESTO FALLA CON PGBOUNCER
var strategy = _context.Database.CreateExecutionStrategy();
await strategy.ExecuteAsync(async () =>
{
    using var transaction = await _context.Database.BeginTransactionAsync();
    // ... código ...
    await _context.SaveChangesAsync(); // ← Intenta crear savepoint, FALLA
    await transaction.CommitAsync();
});
```

**Por qué falla**: `SaveChangesAsync()` dentro de `ExecutionStrategy` intenta crear savepoint automático.

### **Patrón Problemático #2: BeginTransactionAsync + EnableRetryOnFailure**
```csharp
// ❌ ESTO FALLA CON PGBOUNCER
using var transaction = await _context.Database.BeginTransactionAsync();
try
{
    await _context.SaveChangesAsync(); // ← Si hay EnableRetryOnFailure, intenta savepoint
    await transaction.CommitAsync();
}
```

**Por qué falla**: `EnableRetryOnFailure` hace que `SaveChangesAsync()` use `ExecutionStrategy` internamente, que intenta crear savepoint.

### **Patrón Correcto #1: Solo BeginTransactionAsync (sin ExecutionStrategy)**
```csharp
// ✅ ESTO FUNCIONA (si no hay EnableRetryOnFailure activo)
using var transaction = await _context.Database.BeginTransactionAsync();
try
{
    await _context.SaveChangesAsync(); // ← Sin ExecutionStrategy, no intenta savepoint
    await transaction.CommitAsync();
}
catch (DbUpdateException dbEx) when (dbEx.InnerException is ObjectDisposedException)
{
    // Recovery con nuevo contexto
}
```

**Por qué funciona**: Sin `ExecutionStrategy`, no intenta crear savepoints.

### **Patrón Correcto #2: Sin Transacción (solo SaveChangesAsync)**
```csharp
// ✅ ESTO FUNCIONA PERFECTAMENTE
try
{
    await _context.SaveChangesAsync(); // ← ExecutionStrategy puede crear savepoints (no hay transacción manual)
}
catch (DbUpdateException dbEx) when (dbEx.InnerException is ObjectDisposedException)
{
    // Recovery con nuevo contexto
}
```

**Por qué funciona**: `ExecutionStrategy` puede crear savepoints porque NO hay transacción manual activa.

---

## 📊 MATRIZ DE RIESGO

| Método | Transacción Manual | ExecutionStrategy | Recovery | Riesgo | Estado |
|--------|-------------------|-------------------|----------|--------|--------|
| `CreateExpertOnboarding` | ❌ Eliminada | ❌ No | ✅ Sí | ✅ Bajo | ✅ Corregido |
| `HandleStripeWebhook` (account.updated) | ❌ Eliminada | ❌ No | ✅ Sí | ✅ Bajo | ✅ Corregido |
| `HandleStripeWebhook` (deauthorized) | ⚠️ Sí | ❌ No | ⚠️ Parcial | ⚠️ Medio | ⚠️ Pendiente |
| `HandleStripeWebhook` (transfer.failed) | ⚠️ Sí | ❌ No | ⚠️ Parcial | ⚠️ Medio | ⚠️ Pendiente |
| `CreateAppointmentAsync` | ⚠️ Sí (necesaria) | ❌ No | ✅ Sí | ⚠️ Medio | ✅ Con recovery |
| `CreateSearchWithHire` | ❌ Eliminada | ❌ No | ✅ Sí | ✅ Bajo | ✅ Corregido |
| `HandleStripeWebhook` (interno) | ⚠️ Sí | 🔴 **SÍ** | ❌ No | 🔴 **CRÍTICO** | 🔴 **URGENTE** |
| `RefundService` | ⚠️ Sí | 🔴 **SÍ** | ❌ No | 🔴 **CRÍTICO** | 🔴 **URGENTE** |
| `AccountDeletionService` | ⚠️ Sí | 🔴 **SÍ** | ❌ No | 🔴 **CRÍTICO** | 🔴 **URGENTE** |
| `DisputeController` | ⚠️ Sí | 🔴 **SÍ** | ❌ No | 🔴 **CRÍTICO** | 🔴 **URGENTE** |
| `SearchHireController` | ⚠️ Sí | 🔴 **SÍ** | ❌ No | 🔴 **CRÍTICO** | 🔴 **URGENTE** |
| `SubscriptionService` | ⚠️ Sí | 🔴 **SÍ** | ❌ No | 🔴 **CRÍTICO** | 🔴 **URGENTE** |
| `LoggingService.LogAsync` | ❌ Eliminada | ❌ No | ✅ Sí | ✅ Bajo | ✅ Corregido |

---

## 🚨 PRIORIDADES DE CORRECCIÓN

### **⚠️ MEDIO-ALTO (Corregir pronto):**
1. **`SearchController.CreateSearchWithHire`** - Usa ExecutionStrategy + transacción manual
   - **Impacto**: Creación de checkout sessions puede fallar si se agrega SaveChangesAsync en el futuro
   - **Nota**: Actualmente no hace SaveChangesAsync, pero el patrón es problemático
   - **Solución**: Eliminar ExecutionStrategy, mantener solo transacción (o eliminar transacción si no es necesaria)

### **⚠️ ALTO (Corregir pronto):**
2. **`SubscriptionController` - Webhook `account.application.deauthorized`**
   - **Impacto**: Webhooks de desautorización fallarán
   - **Solución**: Eliminar transacción manual, usar recovery

3. **`SubscriptionController` - Webhook `transfer.failed`**
   - **Impacto**: Webhooks de transferencias fallidas fallarán
   - **Solución**: Eliminar transacción manual, usar recovery

### **🟡 MEDIO (Revisar y mejorar):**
4. **`AppointmentService` - Todos los métodos con transacciones**
   - **Impacto**: Pueden fallar ocasionalmente con ObjectDisposedException
   - **Estado**: Ya tienen recovery, pero las transacciones pueden causar problemas
   - **Solución**: Las transacciones son necesarias para `FOR UPDATE`, mantener pero mejorar recovery

---

## 🔍 CÓMO IDENTIFICAR MÁS PROBLEMAS

### **Buscar en el código:**
```bash
# Buscar todos los lugares con transacciones manuales
grep -r "BeginTransactionAsync" .

# Buscar ExecutionStrategy con transacciones
grep -r "CreateExecutionStrategy" .
grep -r "strategy.ExecuteAsync" .
```

### **Patrones a buscar:**
1. `BeginTransactionAsync` dentro de `strategy.ExecuteAsync`
2. `BeginTransactionAsync` con `SaveChangesAsync` (puede activar ExecutionStrategy)
3. Métodos que usan `FOR UPDATE` (necesitan transacciones, pero deben manejar errores)

---

## ✅ SOLUCIÓN GENERAL

### **Regla de Oro:**
> **NUNCA combinar `ExecutionStrategy` con transacciones manuales cuando uses Supabase (PgBouncer)**

### **Estrategias de Corrección:**

#### **1. Si NO necesitas transacción:**
```csharp
// ✅ Eliminar transacción completamente
try
{
    await _context.SaveChangesAsync();
}
catch (DbUpdateException dbEx) when (dbEx.InnerException is ObjectDisposedException)
{
    // Recovery con IServiceScopeFactory
}
```

#### **2. Si SÍ necesitas transacción (ej: FOR UPDATE):**
```csharp
// ✅ Mantener transacción pero SIN ExecutionStrategy
using var transaction = await _context.Database.BeginTransactionAsync();
try
{
    // ... código con FOR UPDATE ...
    await _context.SaveChangesAsync();
    await transaction.CommitAsync();
}
catch (DbUpdateException dbEx) when (dbEx.InnerException is ObjectDisposedException)
{
    try { await transaction.RollbackAsync(); } catch { }
    // Recovery con IServiceScopeFactory
}
```

#### **3. Si usas ExecutionStrategy:**
```csharp
// ✅ ExecutionStrategy SOLO sin transacciones manuales
var strategy = _context.Database.CreateExecutionStrategy();
await strategy.ExecuteAsync(async () =>
{
    // NO usar BeginTransactionAsync aquí
    await _context.SaveChangesAsync(); // ← Esto funciona
});
```

---

## 📝 CONFIGURACIÓN ACTUAL (Program.cs)

### **✅ Configuración Correcta:**
```csharp
// Multiplexing=false - CRÍTICO
connectionStringBuilder.Multiplexing = false;

// Enlist=false - CRÍTICO
connectionStringBuilder.Enlist = false;

// MaxAutoPrepare=0 - CRÍTICO para PgBouncer
connectionStringBuilder.MaxAutoPrepare = 0;

// EnableRetryOnFailure - HABILITADO (pero NO con transacciones manuales)
npgsqlOptions.EnableRetryOnFailure(
    maxRetryCount: 5,
    maxRetryDelay: TimeSpan.FromSeconds(10)
);
```

**Nota**: `EnableRetryOnFailure` está bien habilitado, pero NO debe usarse con transacciones manuales.

---

## 🎯 CONCLUSIÓN

**Problema raíz**: PgBouncer Transaction Pooler no soporta savepoints que EF Core intenta crear cuando combinas `ExecutionStrategy` con transacciones manuales.

**Solución**: 
1. Eliminar `ExecutionStrategy` de métodos con transacciones manuales
2. Mantener transacciones solo cuando sean necesarias (ej: `FOR UPDATE`)
3. Implementar recovery robusto con `IServiceScopeFactory` para `ObjectDisposedException`

**Estado actual**: 
- ✅ 4 métodos críticos corregidos (`CreateExpertOnboarding`, `HandleStripeWebhook account.updated`, `LoggingService`, `CreateSearchWithHire`)
- ⚠️ 2 métodos pendientes de revisar (webhooks `deauthorized` y `transfer.failed`)
- 🔴 **7 métodos críticos** que usan ExecutionStrategy + transacción manual y necesitan corrección urgente:
  1. `SubscriptionController.HandleStripeWebhook` (método interno)
  2. `RefundService.cs`
  3. `AccountDeletionService.cs`
  4. `DisputeController.cs`
  5. `SearchHireController.cs`
  6. `SubscriptionService.cs`
  7. `AppointmentService` (6 métodos con transacciones, pero ya tienen recovery)
