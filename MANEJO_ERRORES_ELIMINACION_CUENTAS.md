# Manejo de Errores en Eliminación de Cuentas

## Resumen Ejecutivo

El proceso de eliminación de cuentas está diseñado con **atomicidad total** y **fallbacks robustos**. Si falla en cualquier punto, se garantiza que:

1. **Nada se modifica** (rollback completo de la transacción)
2. **Todo queda registrado** (logging detallado)
3. **El usuario permanece intacto** (no se elimina parcialmente)
4. **Se crean disputas** como fallback cuando falla el procesamiento de dinero

---

## Flujo del Proceso y Puntos de Falla

### **FASE 1: Validaciones Iniciales** (Fuera de transacción)

#### ✅ **1.1 Verificar Usuario Existe**
- **Ubicación**: Línea 167-179
- **Qué hace**: Busca el usuario en la BD
- **Si falla**:
  - ❌ **No hay transacción activa** → No hay rollback
  - ✅ **Retorna error** con `Success = false`
  - ✅ **No se lanza excepción** → El controller maneja el error gracefully
  - 📝 **Resultado**: Usuario no encontrado, proceso termina sin cambios

#### ✅ **1.2 Verificar Usuario No Está Ya Eliminado**
- **Ubicación**: Línea 182-189
- **Qué hace**: Verifica `user.IsDeleted == false`
- **Si falla** (usuario ya eliminado):
  - ❌ **No hay transacción activa** → No hay rollback
  - ✅ **Retorna error** con `Success = false` y mensaje informativo
  - ✅ **Idempotencia**: Se puede llamar múltiples veces sin problemas
  - 📝 **Resultado**: Usuario ya eliminado, proceso termina sin cambios

---

### **FASE 2: Inicio de Transacción** (Dentro de transacción)

#### ✅ **2.1 Obtener Contrataciones Activas**
- **Ubicación**: Línea 194
- **Qué hace**: Busca contrataciones con estados activos (`pending`, `awaiting_client_decision`, `disputed`)
- **Si falla**:
  - ✅ **Transacción activa** → Se hace **ROLLBACK COMPLETO**
  - ✅ **Log crítico** registrado (línea 275-291)
  - ✅ **Excepción lanzada** → El controller recibe error 500
  - 📝 **Resultado**: **Nada se modifica**, usuario intacto, error registrado

---

### **FASE 3: Procesamiento de Contrataciones Activas** (Dentro de transacción)

#### ✅ **3.1 Procesar Dinero (Stripe) - ÉXITO**
- **Ubicación**: Líneas 415-419 (cliente) o 482-486 (experto)
- **Qué hace**: Llama a `ProcessMoneyDistributionAsync` con `updateState: true`
- **Si falla** (Stripe rechaza, error de red, etc.):
  - ✅ **Transacción activa** → Se hace **ROLLBACK COMPLETO**
  - ✅ **Log crítico** registrado (línea 423-437 o 490-505)
  - ✅ **Excepción lanzada** → Se propaga al catch principal
  - 📝 **Resultado**: **Nada se modifica**, usuario intacto, error registrado

#### ✅ **3.2 Procesar Dinero (Stripe) - FALLBACK A DISPUTA**
- **Ubicación**: Líneas 560-603
- **Qué hace**: Si falla el procesamiento de dinero, crea una disputa automáticamente
- **Comportamiento**:
  - ✅ **Crea disputa** con estado `pending`
  - ✅ **Cambia estado** del SearchHire a `disputed`
  - ✅ **Continúa el proceso** (no aborta la eliminación)
  - ✅ **Log crítico** registrado (línea 565-578)
  - 📝 **Resultado**: Disputa creada, proceso continúa, requiere intervención manual

**⚠️ NOTA IMPORTANTE**: Este fallback solo se activa si `ProcessMoneyDistributionAsync` falla pero NO lanza excepción que propague al catch principal. Si lanza excepción, se hace rollback completo.

---

### **FASE 4: Anonimización de Datos** (Dentro de transacción)

#### ✅ **4.1 Anonimización de Mensajes**
- **Ubicación**: Línea 654-658
- **Qué hace**: Ejecuta SQL raw para anonimizar mensajes
- **Si falla**:
  - ✅ **Transacción activa** → Se hace **ROLLBACK COMPLETO**
  - ✅ **Log crítico** registrado (línea 781-796)
  - ✅ **Excepción lanzada** → Se propaga al catch principal
  - 📝 **Resultado**: **Nada se modifica**, usuario intacto, error registrado

#### ✅ **4.2 Anonimización de Conversaciones**
- **Ubicación**: Línea 676-682
- **Si falla**: Mismo comportamiento que 4.1

#### ✅ **4.3 Anonimización de Reseñas**
- **Ubicación**: Línea 701-707
- **Si falla**: Mismo comportamiento que 4.1

#### ✅ **4.4 Anonimización de Transacciones Financieras**
- **Ubicación**: Línea 728-732
- **Si falla**: Mismo comportamiento que 4.1

#### ✅ **4.5 Anonimización de Notificaciones**
- **Ubicación**: Línea 758-762
- **Si falla**: Mismo comportamiento que 4.1

#### ✅ **4.6 Anonimización de SearchHires**
- **Ubicación**: Líneas 780-790
- **Si falla**: Mismo comportamiento que 4.1

---

### **FASE 5: Eliminación de Datos No Críticos** (Dentro de transacción)

#### ✅ **5.1 Eliminación de Likes, Búsquedas, Servicios, etc.**
- **Ubicación**: Líneas 851-925
- **Qué hace**: Elimina datos no críticos en batch
- **Si falla**:
  - ✅ **Transacción activa** → Se hace **ROLLBACK COMPLETO**
  - ✅ **Log crítico** registrado (línea 950-966)
  - ✅ **Excepción lanzada** → Se propaga al catch principal
  - 📝 **Resultado**: **Nada se modifica**, usuario intacto, error registrado

---

### **FASE 6: Soft Delete del Usuario** (Dentro de transacción)

#### ✅ **6.1 Marcar Usuario como Eliminado**
- **Ubicación**: Líneas 933-942
- **Qué hace**: Marca `IsDeleted = true` y `DeletedAt = DateTime.UtcNow`
- **Si falla**:
  - ✅ **Transacción activa** → Se hace **ROLLBACK COMPLETO**
  - ✅ **Log crítico** registrado (línea 950-966)
  - ✅ **Excepción lanzada** → Se propaga al catch principal
  - 📝 **Resultado**: **Nada se modifica**, usuario intacto, error registrado

---

### **FASE 7: Commit de Transacción** (Punto crítico)

#### ✅ **7.1 Commit de Transacción**
- **Ubicación**: Línea 208
- **Qué hace**: Confirma todos los cambios en la BD
- **Si falla**:
  - ✅ **Transacción activa** → Se hace **ROLLBACK COMPLETO** automático
  - ✅ **Log crítico** registrado (línea 275-291)
  - ✅ **Excepción lanzada** → El controller recibe error 500
  - 📝 **Resultado**: **Nada se modifica**, usuario intacto, error registrado

**🎯 PUNTO CLAVE**: Si el commit falla, **TODO** se revierte automáticamente. El usuario permanece intacto.

---

### **FASE 8: Notificaciones** (Fuera de transacción - NO crítico)

#### ✅ **8.1 Notificar Usuarios Afectados**
- **Ubicación**: Líneas 212-235
- **Qué hace**: Envía notificaciones a usuarios afectados por disputas
- **Si falla**:
  - ✅ **Transacción ya commitada** → **NO se revierte**
  - ✅ **Log warning** registrado (línea 222-234)
  - ✅ **NO lanza excepción** → El proceso continúa
  - 📝 **Resultado**: Cuenta eliminada exitosamente, pero notificaciones no enviadas (se pueden enviar manualmente)

#### ✅ **8.2 Notificar Usuario que Eliminó Cuenta**
- **Ubicación**: Líneas 237-258
- **Qué hace**: Envía notificación al usuario que eliminó su cuenta
- **Si falla**:
  - ✅ **Transacción ya commitada** → **NO se revierte**
  - ✅ **Log warning** registrado (línea 245-257)
  - ✅ **NO lanza excepción** → El proceso continúa
  - 📝 **Resultado**: Cuenta eliminada exitosamente, pero notificación no enviada (se puede enviar manualmente)

**🎯 PUNTO CLAVE**: Las notificaciones están **fuera de la transacción** y **no son críticas**. Si fallan, la eliminación ya se completó exitosamente.

---

## Timeout de Transacción

#### ✅ **Timeout de 5 Minutos**
- **Ubicación**: Línea 156
- **Qué hace**: Cancela la transacción si tarda más de 5 minutos
- **Si ocurre timeout**:
  - ✅ **Transacción activa** → Se hace **ROLLBACK COMPLETO** automático
  - ✅ **CancellationToken cancelado** → Todas las operaciones se cancelan
  - ✅ **Excepción `OperationCanceledException`** → Se propaga al catch principal
  - ✅ **Log crítico** registrado (línea 275-291)
  - 📝 **Resultado**: **Nada se modifica**, usuario intacto, timeout registrado

---

## Casos Especiales

### **Caso 1: Usuario Ya Eliminado (Idempotencia)**
- ✅ **No hay transacción** → No hay rollback
- ✅ **Retorna error** con mensaje informativo
- ✅ **Se puede llamar múltiples veces** sin problemas
- 📝 **Resultado**: Proceso termina sin cambios, idempotente

### **Caso 2: Procesamiento de Dinero Falla (Fallback a Disputa)**
- ✅ **Crea disputa** automáticamente
- ✅ **Continúa el proceso** de eliminación
- ✅ **Requiere intervención manual** para procesar el dinero
- 📝 **Resultado**: Cuenta eliminada, disputa creada, dinero pendiente de procesar

### **Caso 3: Error en Anonimización**
- ✅ **Rollback completo** de toda la transacción
- ✅ **Usuario intacto** (no se elimina nada)
- ✅ **Error registrado** para revisión
- 📝 **Resultado**: Nada se modifica, requiere revisión del error

### **Caso 4: Timeout de Transacción**
- ✅ **Rollback completo** automático
- ✅ **Usuario intacto** (no se elimina nada)
- ✅ **Timeout registrado** para revisión
- 📝 **Resultado**: Nada se modifica, requiere revisión del timeout

---

## Garantías del Sistema

### ✅ **Atomicidad Total**
- **Todo o nada**: Si falla cualquier paso dentro de la transacción, **TODO** se revierte
- **Usuario intacto**: El usuario nunca queda en estado parcial
- **Datos consistentes**: No hay estados intermedios inconsistentes

### ✅ **Idempotencia**
- **Múltiples llamadas**: Se puede llamar múltiples veces sin problemas
- **Usuario ya eliminado**: Retorna error informativo, no lanza excepción
- **Sin efectos secundarios**: Llamadas repetidas no causan efectos adversos

### ✅ **Trazabilidad Completa**
- **Logging detallado**: Todos los errores se registran con contexto completo
- **Stack traces**: Incluye stack traces para debugging
- **Metadata**: Incluye userId, error type, timestamps, etc.

### ✅ **Fallbacks Robustos**
- **Disputas automáticas**: Si falla el procesamiento de dinero, se crea disputa
- **Notificaciones no críticas**: Si fallan, no abortan el proceso
- **Intervención manual**: Disputas requieren procesamiento manual del dinero

---

## Resumen de Comportamiento por Tipo de Error

| Tipo de Error | Ubicación | Rollback | Usuario Intacto | Log | Resultado |
|--------------|-----------|----------|-----------------|-----|-----------|
| Usuario no encontrado | Fase 1 | ❌ No hay tx | ✅ Sí | ⚠️ Warning | Error retornado |
| Usuario ya eliminado | Fase 1 | ❌ No hay tx | ✅ Sí | ⚠️ Warning | Error retornado (idempotente) |
| Error en GetActiveContracts | Fase 2 | ✅ Sí | ✅ Sí | 🔴 Crítico | Rollback completo |
| Error en ProcessMoney (excepción) | Fase 3 | ✅ Sí | ✅ Sí | 🔴 Crítico | Rollback completo |
| Error en ProcessMoney (fallback) | Fase 3 | ❌ No | ✅ Sí | 🔴 Crítico | Disputa creada, continúa |
| Error en Anonimización | Fase 4 | ✅ Sí | ✅ Sí | 🔴 Crítico | Rollback completo |
| Error en Eliminación datos | Fase 5 | ✅ Sí | ✅ Sí | 🔴 Crítico | Rollback completo |
| Error en Soft Delete | Fase 6 | ✅ Sí | ✅ Sí | 🔴 Crítico | Rollback completo |
| Error en Commit | Fase 7 | ✅ Sí (auto) | ✅ Sí | 🔴 Crítico | Rollback automático |
| Error en Notificaciones | Fase 8 | ❌ No (ya commit) | ✅ Sí | ⚠️ Warning | Continúa, notificaciones fallidas |
| Timeout | Cualquier fase | ✅ Sí (auto) | ✅ Sí | 🔴 Crítico | Rollback automático |

---

## Conclusión

El sistema garantiza que:

1. **Si falla ANTES del commit**: **TODO** se revierte, usuario intacto
2. **Si falla EN el commit**: **TODO** se revierte automáticamente, usuario intacto
3. **Si falla DESPUÉS del commit** (notificaciones): Cuenta eliminada exitosamente, notificaciones fallidas (no crítico)
4. **Si falla el procesamiento de dinero**: Se crea disputa automáticamente, proceso continúa
5. **Si hay timeout**: **TODO** se revierte automáticamente, usuario intacto

**El usuario NUNCA queda en estado parcial o inconsistente.**

