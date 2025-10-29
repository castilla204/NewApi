# 🚨 **SOLUCIÓN FINAL: NOTIFICACIÓN AL ADMIN Y EXPERTO**

## 📋 **IMPLEMENTACIÓN CON SISTEMA EXISTENTE**

Voy a implementar la notificación tanto al admin como al experto usando el sistema de logs + notifications que ya existe.

---

## 🎯 **FUNCIÓN SIMPLE PARA MANEJAR RECHAZO**

```csharp
private async Task HandleAccountRejection(int expertId, string rejectionReason)
{
    _logger.LogWarning("🚨 CRITICAL: Account rejected for expertId={ExpertId}, reason={Reason}", expertId, rejectionReason);
    
    try
    {
        // 1. Verificar si el experto tiene contrataciones activas
        var activeHires = await _context.SearchHires
            .Include(sh => sh.Status)
            .Where(sh => sh.ExpertId == expertId && 
                        sh.Status.StatusValue == "pending")
            .CountAsync();
        
        _logger.LogInformation("Found {Count} active hires for rejected expert {ExpertId}", activeHires, expertId);
        
        // 2. Crear log crítico (esto automáticamente notifica al admin)
        await _loggingService.LogCriticalAsync(
            $"Expert account rejected - ExpertId: {expertId}",
            $"Stripe account rejected for expert {expertId}. Reason: {rejectionReason}. Active hires: {activeHires}",
            expertId,
            "SubscriptionController.HandleAccountRejection",
            "ExpertProfile",
            expertId,
            new { 
                ExpertId = expertId, 
                RejectionReason = rejectionReason, 
                ActiveHiresCount = activeHires,
                Timestamp = DateTime.UtcNow
            }
        );
        
        // 3. Crear notificación para el experto
        await NotifyExpertOfAccountRejection(expertId, rejectionReason, activeHires);
        
        _logger.LogInformation("✅ Account rejection handled for expert {ExpertId} with {Count} active hires", expertId, activeHires);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed to handle account rejection for expert {ExpertId}", expertId);
    }
}
```

### **2. NOTIFICACIÓN AL EXPERTO**

```csharp
private async Task NotifyExpertOfAccountRejection(int expertId, string rejectionReason, int activeHiresCount)
{
    try
    {
        var expert = await _context.Users.FindAsync(expertId);
        if (expert == null) return;
        
        // Crear notificación para el experto
        var notification = new Notification
        {
            Id = Guid.NewGuid(),
            Title = "🚨 Cuenta de Pagos Rechazada",
            Message = $"Tu cuenta de pagos fue rechazada por Stripe. Motivo: {rejectionReason}. Tienes {activeHiresCount} contrataciones activas que pueden verse afectadas. Contacta al soporte para más información.",
            Type = "account_rejected",
            UserId = expertId,
            Read = false,
            CreatedAt = DateTime.UtcNow
        };
        
        _context.Notifications.Add(notification);
        await _context.SaveChangesAsync();
        
        _logger.LogInformation("✅ Expert notification created for account rejection - ExpertId: {ExpertId}", expertId);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed to create expert notification for account rejection - ExpertId: {ExpertId}", expertId);
    }
}
```

### **3. ACTUALIZAR WEBHOOKS PARA USAR LA NUEVA FUNCIÓN**

```csharp
case "account.updated":
    // ... código existente ...
    
    if (isRejected)
    {
        var previousStatus = expertProfile.StripeStatus;
        expertProfile.StripeStatus = StripeStatus.Rejected;
        expertProfile.OnboardingCompleted = false;
        expertProfile.StripeStatusDetails = GetRejectionMessage(disabledReason, errorDetails);
        
        // ✅ NUEVO: Notificar al admin y experto
        await HandleAccountRejection(expertProfile.UserId, disabledReason);
        
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
        
        // ✅ NUEVO: Notificar al admin y experto
        await HandleAccountRejection(deauthorizedExpertProfile.UserId, "Account deauthorized by Stripe");
        
        await _context.SaveChangesAsync();
    }
    break;
```

---

## 🎯 **FLUJO COMPLETO**

### **1. Stripe rechaza cuenta del experto**
- Webhook `account.updated` o `account.application.deauthorized` ✅
- Sistema actualiza estado del experto a `Rejected` ✅

### **2. Sistema verifica contrataciones activas**
- Busca contrataciones con estado `pending` ✅
- Cuenta cuántas tiene el experto ✅

### **3. Log crítico automático (notifica al admin)**
- `_loggingService.LogCriticalAsync()` ✅
- Crea log con nivel "Critical" ✅
- Sistema automáticamente crea notificación para admin ✅

### **4. Notificación al experto**
- Crea notificación en la tabla `Notifications` ✅
- El experto la ve en su panel ✅

---

## 🚀 **VENTAJAS DE ESTA SOLUCIÓN**

### **Usa sistema existente:**
- ✅ **Aprovecha** el sistema de logs + notifications ya implementado
- ✅ **No requiere** cambios en la base de datos
- ✅ **Consistente** con el resto del sistema

### **Notificaciones automáticas:**
- ✅ **Admin** recibe notificación automática via logs críticos
- ✅ **Experto** recibe notificación directa
- ✅ **Ambos** tienen toda la información necesaria

### **Simplicidad:**
- ✅ **Muy simple** de implementar
- ✅ **Fácil** de mantener
- ✅ **No complica** el flujo normal

---

## 📊 **INFORMACIÓN QUE RECIBE CADA UNO**

### **Admin (via logs críticos):**
- ✅ Título: "🚨 CRITICAL ALERT"
- ✅ Mensaje: "Expert account rejected - ExpertId: X"
- ✅ Detalles: Motivo del rechazo + número de contrataciones activas
- ✅ Datos adicionales: JSON con toda la información

### **Experto (via notifications):**
- ✅ Título: "🚨 Cuenta de Pagos Rechazada"
- ✅ Mensaje: Motivo del rechazo + número de contrataciones activas
- ✅ Instrucciones: Contactar soporte
- ✅ Tipo: "account_rejected"

---

## 🎉 **RESULTADO FINAL**

Con esta solución:

1. **✅ Admin recibe notificación automática** via sistema de logs críticos
2. **✅ Experto recibe notificación directa** en su panel
3. **✅ Ambos tienen información completa** sobre el problema
4. **✅ Usa el sistema existente** sin complicaciones
5. **✅ Es simple y efectivo**

**¡Solución perfecta usando el sistema existente!** 🚨

---

*He creado un documento detallado con la solución completa, código, y pasos de implementación.*
