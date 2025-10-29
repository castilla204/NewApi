# 🚨 **ANÁLISIS CRÍTICO: CUENTA APROBADA RECHAZADA CON CONTRATACIONES ACTIVAS**

## 📋 **ESCENARIO CRÍTICO**

**Situación**: Un experto tiene su cuenta de Stripe **aprobada**, tiene varias contrataciones activas donde recibirá dinero, y **de repente Stripe rechaza su cuenta**.

---

## 🔍 **¿QUÉ PASA EN TU SISTEMA ACTUAL?**

### **1. DETECCIÓN DEL RECHAZO** ✅ IMPLEMENTADO

**Webhook `account.updated`:**
```csharp
// Tu sistema detecta el rechazo automáticamente
bool isRejected = !string.IsNullOrEmpty(disabledReason) &&
                  (disabledReason.StartsWith("rejected.") || 
                   disabledReason == "under_review" || 
                   disabledReason == "listed" ||
                   disabledReason == "requirements.past_due" || 
                   disabledReason == "requirements.pending_verification" ||
                   disabledReason == "other" || 
                   disabledReason == "action_required.requested_capabilities");

if (isRejected) {
    expertProfile.StripeStatus = StripeStatus.Rejected;
    expertProfile.OnboardingCompleted = false;
    expertProfile.StripeStatusDetails = GetRejectionMessage(disabledReason, errorDetails);
}
```

**Webhook `account.application.deauthorized`:**
```csharp
// También detecta desautorización
deauthorizedExpertProfile.StripeStatus = StripeStatus.Rejected;
deauthorizedExpertProfile.OnboardingCompleted = false;
```

### **2. ACTUALIZACIÓN DEL ESTADO** ✅ IMPLEMENTADO

**Lo que SÍ pasa:**
- ✅ El `StripeStatus` cambia a `Rejected`
- ✅ `OnboardingCompleted` se pone en `false`
- ✅ Se actualiza `StripeStatusDetails` con el motivo
- ✅ Se registra en logs el cambio de estado

**Lo que NO pasa (PROBLEMA CRÍTICO):**
- ❌ **NO se cancelan las contrataciones activas**
- ❌ **NO se notifica a los clientes**
- ❌ **NO se procesan refunds automáticos**
- ❌ **NO se pausan los servicios del experto**

---

## ⚠️ **PROBLEMAS CRÍTICOS IDENTIFICADOS**

### **1. CONTRATACIONES ACTIVAS NO SE CANCELAN**

**Problema:**
```csharp
// ❌ PROBLEMA: Solo actualiza el perfil del experto
expertProfile.StripeStatus = StripeStatus.Rejected;
// ❌ NO hace nada con las SearchHires activas
```

**Consecuencias:**
- Los clientes siguen esperando servicios
- El experto no puede recibir pagos
- Dinero queda "atrapado" en el sistema
- Experiencia de usuario terrible

### **2. NO HAY NOTIFICACIÓN A CLIENTES**

**Problema:**
- Los clientes no saben que su experto fue rechazado
- Siguen esperando servicios que nunca llegarán
- No hay comunicación sobre el problema

### **3. NO HAY REFUNDS AUTOMÁTICOS**

**Problema:**
- Dinero ya pagado no se devuelve automáticamente
- Clientes tienen que abrir disputas manualmente
- Proceso manual y lento

---

## 🛠️ **SOLUCIÓN COMPLETA RECOMENDADA**

### **1. FUNCIÓN PARA MANEJAR RECHAZO DE CUENTA APROBADA**

```csharp
private async Task HandleApprovedAccountRejection(int expertId, string rejectionReason)
{
    _logger.LogWarning("🚨 CRITICAL: Handling rejection of previously approved account for expertId={ExpertId}, reason={Reason}", expertId, rejectionReason);
    
    // 1. Obtener todas las contrataciones activas del experto
    var activeHires = await _context.SearchHires
        .Include(sh => sh.Client)
        .Include(sh => sh.SearchService)
        .Where(sh => sh.ExpertId == expertId && 
                    (sh.Status.StatusValue == "pending" || 
                     sh.Status.StatusValue == "in_progress" ||
                     sh.Status.StatusValue == "appointment_scheduled"))
        .ToListAsync();
    
    _logger.LogInformation("Found {Count} active hires for rejected expert", activeHires.Count);
    
    foreach (var hire in activeHires)
    {
        // 2. Cancelar cada contratación
        await CancelHireDueToAccountRejection(hire, rejectionReason);
    }
    
    // 3. Notificar al experto
    await NotifyExpertOfAccountRejection(expertId, rejectionReason, activeHires.Count);
    
    // 4. Registrar evento crítico
    await _loggingService.LogCriticalAsync(
        $"Expert account rejected with active hires - ExpertId: {expertId}",
        $"Account rejection affected {activeHires.Count} active hires",
        expertId,
        "SubscriptionController.HandleApprovedAccountRejection",
        "ExpertProfile",
        expertId,
        new { ExpertId = expertId, RejectionReason = rejectionReason, ActiveHiresCount = activeHires.Count }
    );
}

private async Task CancelHireDueToAccountRejection(SearchHire hire, string rejectionReason)
{
    using var transaction = await _context.Database.BeginTransactionAsync();
    try
    {
        // 1. Cambiar estado a cancelado
        hire.StatusId = await GetStatusIdByValueAsync("cancelled_due_to_account_rejection");
        hire.UpdatedAt = DateTime.UtcNow;
        
        // 2. Agregar nota explicativa
        hire.Notes = $"Servicio cancelado: La cuenta de pagos del experto fue rechazada por Stripe. Motivo: {rejectionReason}";
        
        // 3. Procesar refund automático
        bool refundSuccess = await _refundService.ProcessMoneyDistributionAsync(
            hire.Id,
            "cancelled_due_to_account_rejection",
            $"Account rejected: {rejectionReason}",
            hire.ClientId);
        
        if (!refundSuccess)
        {
            _logger.LogError("Failed to process refund for hire {HireId} due to account rejection", hire.Id);
        }
        
        // 4. Notificar al cliente
        await NotifyClientOfServiceCancellation(hire, rejectionReason);
        
        await _context.SaveChangesAsync();
        await transaction.CommitAsync();
        
        _logger.LogInformation("Successfully cancelled hire {HireId} due to account rejection", hire.Id);
    }
    catch (Exception ex)
    {
        await transaction.RollbackAsync();
        _logger.LogError(ex, "Failed to cancel hire {HireId} due to account rejection", hire.Id);
        throw;
    }
}
```

### **2. ACTUALIZAR WEBHOOKS PARA USAR LA NUEVA FUNCIÓN**

```csharp
case "account.updated":
    // ... código existente ...
    
    if (isRejected)
    {
        var previousStatus = expertProfile.StripeStatus;
        expertProfile.StripeStatus = StripeStatus.Rejected;
        expertProfile.OnboardingCompleted = false;
        expertProfile.StripeStatusDetails = GetRejectionMessage(disabledReason, errorDetails);
        
        // ✅ NUEVO: Si antes estaba aprobado, manejar contrataciones activas
        if (previousStatus == StripeStatus.Approved)
        {
            await HandleApprovedAccountRejection(expertProfile.UserId, disabledReason);
        }
        
        _logger.LogWarning("❌ DEBUG: Account rejected for userId={UserId}, reason={Reason}", expertProfile.UserId, disabledReason);
    }
    break;

case "account.application.deauthorized":
    // ... código existente ...
    
    if (deauthorizedExpertProfile != null)
    {
        var previousStatus = deauthorizedExpertProfile.StripeStatus;
        deauthorizedExpertProfile.StripeStatus = StripeStatus.Rejected;
        deauthorizedExpertProfile.OnboardingCompleted = false;
        
        // ✅ NUEVO: Si antes estaba aprobado, manejar contrataciones activas
        if (previousStatus == StripeStatus.Approved)
        {
            await HandleApprovedAccountRejection(deauthorizedExpertProfile.UserId, "Account deauthorized by Stripe");
        }
        
        await _context.SaveChangesAsync();
    }
    break;
```

### **3. CREAR ESTADO ESPECÍFICO PARA CANCELACIÓN POR RECHAZO**

```sql
INSERT INTO "SystemStatuses" (
    "StatusType", 
    "StatusName", 
    "StatusValue", 
    "DisplayName", 
    "Description", 
    "Color", 
    "IsActive", 
    "IsFinalizationStatus", 
    "SortOrder", 
    "CreatedAt", 
    "UpdatedAt"
) VALUES (
    'SearchHireStatus',
    'Cancelled Due to Account Rejection',
    'cancelled_due_to_account_rejection',
    'Cancelado - Cuenta Rechazada',
    'Servicio cancelado porque la cuenta de pagos del experto fue rechazada por Stripe',
    '#DC2626',
    true,
    true,
    99,
    NOW(),
    NOW()
);
```

### **4. FUNCIÓN DE NOTIFICACIÓN A CLIENTES**

```csharp
private async Task NotifyClientOfServiceCancellation(SearchHire hire, string rejectionReason)
{
    try
    {
        // 1. Crear notificación en la base de datos
        var notification = new Notification
        {
            UserId = hire.ClientId,
            Title = "Servicio Cancelado - Cuenta de Pagos Rechazada",
            Message = $"Tu servicio '{hire.SearchService.ServiceTypeName}' fue cancelado porque la cuenta de pagos del experto fue rechazada por Stripe. Tu dinero ha sido reembolsado automáticamente.",
            Type = "ServiceCancelled",
            IsRead = false,
            CreatedAt = DateTime.UtcNow
        };
        _context.Notifications.Add(notification);
        
        // 2. Enviar email al cliente
        await _emailService.SendServiceCancellationEmail(
            hire.Client.Email,
            hire.Client.Name,
            hire.SearchService.ServiceTypeName,
            rejectionReason
        );
        
        // 3. Enviar notificación push si está disponible
        await _notificationService.SendPushNotification(
            hire.ClientId,
            "Servicio Cancelado",
            "Tu servicio fue cancelado. El reembolso se procesará automáticamente."
        );
        
        _logger.LogInformation("Notified client {ClientId} of service cancellation due to account rejection", hire.ClientId);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed to notify client {ClientId} of service cancellation", hire.ClientId);
    }
}
```

### **5. FUNCIÓN DE NOTIFICACIÓN AL EXPERTO**

```csharp
private async Task NotifyExpertOfAccountRejection(int expertId, string rejectionReason, int affectedHiresCount)
{
    try
    {
        var expert = await _context.Users.FindAsync(expertId);
        if (expert == null) return;
        
        // 1. Crear notificación
        var notification = new Notification
        {
            UserId = expertId,
            Title = "Cuenta de Pagos Rechazada",
            Message = $"Tu cuenta de pagos fue rechazada por Stripe. Motivo: {rejectionReason}. Se cancelaron {affectedHiresCount} servicios activos. Contacta al soporte para más información.",
            Type = "AccountRejected",
            IsRead = false,
            CreatedAt = DateTime.UtcNow
        };
        _context.Notifications.Add(notification);
        
        // 2. Enviar email
        await _emailService.SendAccountRejectionEmail(
            expert.Email,
            expert.Name,
            rejectionReason,
            affectedHiresCount
        );
        
        _logger.LogInformation("Notified expert {ExpertId} of account rejection", expertId);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed to notify expert {ExpertId} of account rejection", expertId);
    }
}
```

---

## 🎯 **IMPLEMENTACIÓN RECOMENDADA**

### **PASO 1: Crear el estado específico**
```sql
-- Ejecutar en la base de datos
INSERT INTO "SystemStatuses" (...) VALUES (...);
```

### **PASO 2: Agregar las funciones al SubscriptionController**
- `HandleApprovedAccountRejection()`
- `CancelHireDueToAccountRejection()`
- `NotifyClientOfServiceCancellation()`
- `NotifyExpertOfAccountRejection()`

### **PASO 3: Actualizar los webhooks**
- Modificar `account.updated` para detectar cambios de Approved → Rejected
- Modificar `account.application.deauthorized` para manejar contrataciones activas

### **PASO 4: Probar el flujo completo**
- Crear cuenta aprobada
- Crear contrataciones activas
- Simular rechazo de cuenta
- Verificar que se cancelen contrataciones y se procesen refunds

---

## ✅ **BENEFICIOS DE LA SOLUCIÓN**

1. **✅ Protección del Cliente**: Refunds automáticos
2. **✅ Comunicación Clara**: Notificaciones a todos los afectados
3. **✅ Transparencia**: Explicación clara del motivo
4. **✅ Automatización**: No requiere intervención manual
5. **✅ Auditoría**: Logs completos del proceso
6. **✅ Experiencia de Usuario**: Proceso fluido y transparente

---

## 🚨 **PRIORIDAD CRÍTICA**

**Este es un problema CRÍTICO que debe solucionarse ANTES de producción** porque:

1. **Pérdida de dinero**: Clientes pueden perder dinero
2. **Experiencia terrible**: Usuarios frustrados
3. **Problemas legales**: Reembolsos no procesados
4. **Reputación**: Daño a la marca
5. **Soporte**: Sobrecarga de tickets de soporte

**Recomendación: Implementar esta solución INMEDIATAMENTE antes de lanzar a producción.**
