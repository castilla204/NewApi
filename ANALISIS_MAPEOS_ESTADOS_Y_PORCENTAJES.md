# Análisis de Mapeos de Estado y Porcentajes en Eventos

## 📋 Resumen Ejecutivo

Este documento analiza todos los mapeos de estado entre `AppointmentStatus` y `SearchHireStatus`, así como los porcentajes de distribución de dinero configurados para cada evento.

---

## 🔍 Eventos Analizados

### 1. **Timer "proposal" - Cliente no propone cita en 24h**

**Ubicación**: `AppointmentService.cs` líneas 3618-3671

**Estado Appointment**: `appointment_cancelled_by_no_response`

**Estado SearchHire**:
- ✅ **Primario**: `cancelled_by_client_no_proposal` (si existe)
- ⚠️ **Fallback**: `cancelled_by_no_response` (genérico)

**Porcentajes Esperados** (según `MIGRACION_ESTADOS_NO_RESPONSE.md`):
- Cliente: **0%** (culpa del cliente)
- Experto: **100%** (recibe todo)
- Plataforma: **0%**

**Estado usado para dinero**: `cancelled_by_client_no_proposal` o `cancelled_by_no_response`

**✅ Estado**: CORRECTO - El código busca primero el estado específico y luego el genérico.

---

### 2. **Timer "response" - Experto no responde a propuesta en 24h**

**Ubicación**: `AppointmentService.cs` líneas 3673-3726

**Estado Appointment**: `appointment_cancelled_by_no_response`

**Estado SearchHire**:
- ✅ **Primario**: `cancelled_by_expert_no_response` (si existe)
- ⚠️ **Fallback**: `cancelled_by_no_response` (genérico)

**Porcentajes Esperados** (según `MIGRACION_ESTADOS_NO_RESPONSE.md`):
- Cliente: **100%** (recibe todo, culpa del experto)
- Experto: **0%** (culpa del experto)
- Plataforma: **0%**

**Estado usado para dinero**: `cancelled_by_expert_no_response` o `cancelled_by_no_response`

**✅ Estado**: CORRECTO - El código busca primero el estado específico y luego el genérico.

---

### 3. **Timer "expert_report" - Experto no envía reporte en 24h**

**Ubicación**: `AppointmentService.cs` líneas 3728-3767

**Estado Appointment**: `appointment_cancelled_by_no_report`

**Estado SearchHire**:
- ⚠️ **Solo genérico**: `cancelled` (no hay estado específico)

**Porcentajes Configurados** (según `AppointmentConfigController.cs` líneas 1122-1174):
- Cliente: **100%** (según `appointment_cancelled_by_no_response` en defaults)
- Experto: **0%**
- Plataforma: **0%**

**Estado usado para dinero**: `appointment_cancelled_by_no_report`

**⚠️ PROBLEMA IDENTIFICADO**: 
- No hay un estado específico de SearchHire para "experto no envía reporte"
- Usa el estado genérico `cancelled` que puede tener porcentajes diferentes
- El estado `appointment_cancelled_by_no_report` no está en los porcentajes por defecto del controlador

**Recomendación**: Crear estado `cancelled_by_expert_no_report` con porcentajes Cliente 100%, Experto 0%, Plataforma 0%

---

### 4. **Timer "client_decision" - Cliente no decide (aprueba/disputa) en 24h**

**Ubicación**: `AppointmentService.cs` líneas 3769-3943

**Estado Appointment**: No cambia (permanece en `appointment_report_sent`)

**Estado SearchHire**:
- ✅ **Específico**: `completed_without_client_approval`

**Porcentajes Esperados**:
- Debería ser similar a `completed` (Experto recibe la mayoría)
- Cliente: **0%** (no respondió)
- Experto: **95%** (similar a completed)
- Plataforma: **5%** (similar a completed)

**Estado usado para dinero**: `completed_without_client_approval`

**⚠️ PROBLEMA IDENTIFICADO**: 
- El estado `completed_without_client_approval` no está en los porcentajes por defecto del controlador
- No se puede verificar si tiene configuración de porcentajes

**Recomendación**: Verificar que existe configuración de porcentajes para este estado en la BD

---

## 🚨 PROBLEMAS CRÍTICOS ENCONTRADOS

### Problema 1: Inconsistencia en Timer "response" (Línea 2722) - ✅ CORREGIDO

**Ubicación**: `AppointmentService.cs` línea 2722-2783

**Problema**: El comentario y mensajes decían "Si el cliente no responde en 24h" pero debería decir "Si el experto no responde en 24h"

**Correcciones realizadas**:
- ✅ Comentario corregido: "Si el experto no responde en 24h"
- ✅ Mensaje de log corregido: "Expert did not respond within 24h"
- ✅ Actualización del estado SearchHire agregada (buscando `cancelled_by_expert_no_response`)
- ✅ Uso del estado correcto para procesar dinero
- ✅ Mensajes de notificación corregidos (cliente recibe notificación de que experto no respondió, experto recibe advertencia)

---

### Problema 2: Falta estado específico para "expert_report" - ✅ CORREGIDO EN CÓDIGO

**Problema**: Cuando el experto no envía reporte, se usa el estado genérico `cancelled` que puede tener porcentajes incorrectos.

**Estado actual en BD**: 
- `cancelled` (genérico) tiene: Cliente 95%, Experto 0%, Plataforma 5%
- `appointment_cancelled_by_no_report` tiene: Cliente 95%, Experto 0%, Plataforma 5%

**Solución implementada**: 
- ✅ Código actualizado para buscar `cancelled_by_expert_no_report` primero
- ✅ Fallback a `cancelled` si no existe
- ⚠️ **FALTA CREAR EN BD**: Estado `cancelled_by_expert_no_report` con Cliente 95%, Experto 0%, Plataforma 5%

---

### Problema 3: Estados específicos NO EXISTEN en la Base de Datos

**Verificación en BD realizada**:
1. ❌ `cancelled_by_client_no_proposal` - **NO EXISTE EN BD** (código lo busca pero no está)
2. ❌ `cancelled_by_expert_no_response` - **NO EXISTE EN BD** (código lo busca pero no está)
3. ❌ `cancelled_by_expert_no_report` - **NO EXISTE EN BD** (código actualizado para buscarlo)
4. ✅ `completed_without_client_approval` - **EXISTE EN BD** con Cliente 0%, Experto 100%, Plataforma 0%

**Impacto crítico**: 
- El código hace fallback a `cancelled_by_no_response` (genérico) que tiene Cliente 100%, Experto 0%
- Esto es **INCORRECTO** para cuando el cliente no propone (debería ser Cliente 0%, Experto 100%)
- Los porcentajes se están aplicando incorrectamente hasta que se creen los estados específicos

---

## 📊 Tabla Resumen de Estados y Porcentajes

| Evento | AppointmentStatus | SearchHireStatus (Primario) | SearchHireStatus (Fallback) | Cliente % | Experto % | Plataforma % | Estado |
|--------|------------------|----------------------------|---------------------------|-----------|-----------|--------------|--------|
| Cliente no propone | `appointment_cancelled_by_no_response` | `cancelled_by_client_no_proposal` | `cancelled_by_no_response` | 0% | 100% | 0% | ✅ OK |
| Experto no responde | `appointment_cancelled_by_no_response` | `cancelled_by_expert_no_response` | `cancelled_by_no_response` | 100% | 0% | 0% | ✅ OK |
| Experto no envía reporte | `appointment_cancelled_by_no_report` | `cancelled` (genérico) | - | 100%* | 0%* | 0%* | ⚠️ Falta específico |
| Cliente no decide | `appointment_report_sent` (no cambia) | `completed_without_client_approval` | - | 0%* | 95%* | 5%* | ⚠️ Verificar config |

*Porcentajes esperados, pero no confirmados en código

---

## ✅ Recomendaciones

### 1. Corregir comentario incorrecto
- **Archivo**: `Services/AppointmentService.cs`
- **Línea**: 2724
- **Cambio**: "Si el cliente no responde" → "Si el experto no responde"

### 2. Crear estado `cancelled_by_expert_no_report`
- **Tipo**: SearchHireStatus
- **StatusValue**: `cancelled_by_expert_no_report`
- **DisplayName**: "Cancelado por Experto No Envía Reporte"
- **Porcentajes**: Cliente 100%, Experto 0%, Plataforma 0%

### 3. Verificar configuración de `completed_without_client_approval`
- Verificar que existe en la BD
- Verificar que tiene porcentajes configurados
- Si no existe, crear con porcentajes: Cliente 0%, Experto 95%, Plataforma 5%

### 4. Agregar estados faltantes a porcentajes por defecto
- Agregar `cancelled_by_expert_no_report` a `GetDefaultClientPercentage`
- Agregar `completed_without_client_approval` a los métodos de porcentajes por defecto

---

## 🔍 Estados de SearchHire que FALTAN

Según el análisis, estos estados deberían existir pero no están en el enum `SearchHireStatus`:

1. ✅ `cancelled_by_client_no_proposal` - Documentado, código lo busca
2. ✅ `cancelled_by_expert_no_response` - Documentado, código lo busca
3. ❌ `cancelled_by_expert_no_report` - **FALTA** - Código usa genérico `cancelled`
4. ✅ `completed_without_client_approval` - Código lo busca, pero no está en enum

**Nota**: El sistema usa `SystemStatus` en la BD, no el enum, así que estos estados pueden existir en la BD aunque no estén en el enum.

---

## 📝 Próximos Pasos

1. ✅ Verificar en BD qué estados de SearchHireStatus existen - **COMPLETADO**
2. ✅ Verificar qué estados tienen configuración de porcentajes - **COMPLETADO**
3. ⚠️ **CRÍTICO**: Ejecutar script SQL para crear estados faltantes - **PENDIENTE**
4. ⚠️ **CRÍTICO**: Crear configuraciones de porcentajes para estados sin configuración - **PENDIENTE**
5. ✅ Corregir comentario incorrecto en línea 2724 - **COMPLETADO**
6. ✅ Actualizar código para usar estado específico `cancelled_by_expert_no_report` - **COMPLETADO**
7. ✅ Actualizar código para usar estados específicos en todos los eventos - **COMPLETADO**

## 🚨 ACCIÓN REQUERIDA URGENTE

**Ejecutar el script SQL**: `SQL_CREAR_ESTADOS_FALTANTES.sql`

Este script crea los 3 estados faltantes y sus configuraciones de porcentajes:
- `cancelled_by_client_no_proposal` (Cliente 0%, Experto 100%, Plataforma 0%)
- `cancelled_by_expert_no_response` (Cliente 100%, Experto 0%, Plataforma 0%)
- `cancelled_by_expert_no_report` (Cliente 95%, Experto 0%, Plataforma 5%)

Sin estos estados, el sistema está aplicando porcentajes incorrectos cuando ocurren estos eventos.

