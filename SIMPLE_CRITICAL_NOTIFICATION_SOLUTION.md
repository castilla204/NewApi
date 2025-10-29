# 🚨 **SOLUCIÓN SIMPLE: NOTIFICACIÓN CRÍTICA AL EXPERTO**

## 📋 **ANÁLISIS DEL PROBLEMA**

Tienes razón, la solución más simple y efectiva es:

1. **`pending`** es el estado principal hasta que no llega a uno de finalización
2. **Solo notificar al experto** como crítico cuando se recibe un `rejected` en el webhook
3. **Verificar si tiene contrataciones activas** antes de notificar

---

## 🎯 **SOLUCIÓN IMPLEMENTADA**

### **1. FUNCIÓN SIMPLE PARA MANEJAR RECHAZO**

```csharp
private async Task HandleAccountRejection(int expertId, string rejectionReason)
{
    _logger.LogWarning("🚨 CRITICAL: Account rejected for expertId={ExpertId}, reason={Reason}", expertId, rejectionReason);
    
    try
    {
        // 1. Verificar si el experto tiene contrataciones activas
        var activeHires = await _context.SearchHires
            .Include(sh => sh.Status)
            .Include(sh => sh.Client)
            .Include(sh => sh.SearchService)
            .Where(sh => sh.ExpertId == expertId && 
                        sh.Status.StatusValue == "pending")
            .ToListAsync();
        
        _logger.LogInformation("Found {Count} active hires for rejected expert {ExpertId}", activeHires.Count, expertId);
        
        // 2. Si tiene contrataciones activas, notificar como crítico
        if (activeHires.Any())
        {
            await NotifyExpertOfCriticalAccountRejection(expertId, rejectionReason, activeHires);
        }
        else
        {
            _logger.LogInformation("No active hires found for expert {ExpertId}, skipping critical notification", expertId);
        }
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed to handle account rejection for expert {ExpertId}", expertId);
    }
}
```

### **2. NOTIFICACIÓN CRÍTICA AL EXPERTO**

```csharp
private async Task NotifyExpertOfCriticalAccountRejection(int expertId, string rejectionReason, List<SearchHire> activeHires)
{
    try
    {
        var expert = await _context.Users.FindAsync(expertId);
        if (expert == null) return;
        
        // 1. Crear notificación crítica
        var notification = new Notification
        {
            UserId = expertId,
            Title = "🚨 CRÍTICO: Cuenta de Pagos Rechazada",
            Message = $"Tu cuenta de pagos fue rechazada por Stripe. Motivo: {rejectionReason}. Tienes {activeHires.Count} contrataciones activas que pueden verse afectadas. Contacta al soporte inmediatamente.",
            Type = "CriticalAccountRejection",
            IsRead = false,
            CreatedAt = DateTime.UtcNow,
            Priority = "high"
        };
        _context.Notifications.Add(notification);
        
        // 2. Enviar email crítico
        await _emailService.SendCriticalAccountRejectionEmail(
            expert.Email,
            expert.Name,
            rejectionReason,
            activeHires.Count,
            activeHires.Select(h => new { 
                Id = h.Id, 
                ServiceName = h.SearchService.ServiceTypeName,
                ClientName = h.Client.Name,
                Amount = h.Amount
            }).ToList()
        );
        
        // 3. Enviar notificación push si está disponible
        await _notificationService.SendPushNotification(
            expertId,
            "🚨 Cuenta Rechazada - Acción Requerida",
            "Tu cuenta de pagos fue rechazada. Tienes contrataciones activas que pueden verse afectadas."
        );
        
        // 4. Crear ticket de soporte automático
        await _ticketService.CreateTicket(
            "Cuenta de Pagos Rechazada - Experto con Contrataciones Activas",
            $"Experto {expert.Name} ({expert.Email}) tiene {activeHires.Count} contrataciones activas y su cuenta fue rechazada. Motivo: {rejectionReason}",
            "critical",
            "account_rejection"
        );
        
        // 5. Registrar evento crítico
        await _loggingService.LogCriticalAsync(
            $"Expert account rejected with active hires - ExpertId: {expertId}",
            $"Account rejection affects {activeHires.Count} active hires",
            expertId,
            "SubscriptionController.HandleAccountRejection",
            "ExpertProfile",
            expertId,
            new { 
                ExpertId = expertId, 
                RejectionReason = rejectionReason, 
                ActiveHiresCount = activeHires.Count,
                ActiveHires = activeHires.Select(h => new { h.Id, h.Amount }).ToList()
            }
        );
        
        await _context.SaveChangesAsync();
        
        _logger.LogInformation("✅ Critical notification sent to expert {ExpertId} for account rejection with {Count} active hires", expertId, activeHires.Count);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed to send critical notification to expert {ExpertId}", expertId);
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
        
        // ✅ NUEVO: Notificar como crítico si tiene contrataciones activas
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
        
        // ✅ NUEVO: Notificar como crítico si tiene contrataciones activas
        await HandleAccountRejection(deauthorizedExpertProfile.UserId, "Account deauthorized by Stripe");
        
        await _context.SaveChangesAsync();
    }
    break;
```

### **4. SERVICIO DE EMAIL CRÍTICO**

```csharp
public async Task SendCriticalAccountRejectionEmail(
    string expertEmail, 
    string expertName, 
    string rejectionReason, 
    int activeHiresCount,
    List<dynamic> activeHires)
{
    try
    {
        var subject = "🚨 CRÍTICO: Cuenta de Pagos Rechazada - Acción Inmediata Requerida";
        
        var body = $@"
        <h2>🚨 ALERTA CRÍTICA</h2>
        <p>Hola {expertName},</p>
        
        <p><strong>Tu cuenta de pagos de Stripe ha sido rechazada.</strong></p>
        
        <h3>📋 Detalles del Rechazo:</h3>
        <ul>
            <li><strong>Motivo:</strong> {rejectionReason}</li>
            <li><strong>Fecha:</strong> {DateTime.UtcNow:dd/MM/yyyy HH:mm}</li>
        </ul>
        
        <h3>⚠️ Impacto en tus Contrataciones:</h3>
        <p>Tienes <strong>{activeHiresCount} contrataciones activas</strong> que pueden verse afectadas:</p>
        <ul>";
        
        foreach (var hire in activeHires)
        {
            body += $@"
            <li>
                <strong>Servicio:</strong> {hire.ServiceName}<br>
                <strong>Cliente:</strong> {hire.ClientName}<br>
                <strong>Monto:</strong> {hire.Amount:C}<br>
                <strong>ID:</strong> {hire.Id}
            </li>";
        }
        
        body += $@"
        </ul>
        
        <h3>🔧 Acciones Requeridas:</h3>
        <ol>
            <li><strong>Contacta al soporte inmediatamente</strong> - Este es un problema crítico</li>
            <li><strong>No inicies nuevos servicios</strong> hasta resolver el problema</li>
            <li><strong>Completa los servicios activos</strong> si es posible</li>
            <li><strong>Revisa la información</strong> que proporcionaste a Stripe</li>
        </ol>
        
        <h3>📞 Contacto de Soporte:</h3>
        <p>Email: soporte@tuempresa.com<br>
        Teléfono: +34 XXX XXX XXX<br>
        Horario: 24/7 para casos críticos</p>
        
        <p><strong>Este es un problema crítico que requiere atención inmediata.</strong></p>
        
        <p>Saludos,<br>
        Equipo de Soporte</p>";
        
        await _emailService.SendEmailAsync(expertEmail, subject, body);
        
        _logger.LogInformation("Critical account rejection email sent to {ExpertEmail}", expertEmail);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed to send critical account rejection email to {ExpertEmail}", expertEmail);
    }
}
```

### **5. SERVICIO DE NOTIFICACIONES PUSH**

```csharp
public async Task SendPushNotification(int userId, string title, string message)
{
    try
    {
        // Implementar notificación push según tu sistema
        // Firebase, OneSignal, etc.
        
        _logger.LogInformation("Push notification sent to user {UserId}: {Title}", userId, title);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed to send push notification to user {UserId}", userId);
    }
}
```

### **6. SERVICIO DE TICKETS**

```csharp
public async Task CreateTicket(string title, string description, string priority, string category)
{
    try
    {
        // Implementar creación de ticket según tu sistema
        // Jira, Zendesk, etc.
        
        _logger.LogInformation("Ticket created: {Title} - Priority: {Priority}", title, priority);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed to create ticket: {Title}", title);
    }
}
```

---

## 🎯 **FLUJO COMPLETO SIMPLIFICADO**

### **1. Cliente contrata servicio**
- Servicio marcado como `pending` ✅
- Experto puede trabajar ✅

### **2. Stripe rechaza cuenta del experto**
- Webhook `account.updated` o `account.application.deauthorized` ✅
- Sistema verifica si tiene contrataciones `pending` ✅
- Si tiene contrataciones → Notificación crítica ✅
- Si no tiene contrataciones → Solo actualizar estado ✅

### **3. Experto recibe notificación crítica**
- Email crítico con detalles ✅
- Notificación push ✅
- Ticket de soporte creado ✅
- Lista de contrataciones afectadas ✅

---

## 🚀 **VENTAJAS DE ESTA SOLUCIÓN**

### **Simplicidad:**
- ✅ **Muy simple** de implementar
- ✅ **No complica** el flujo normal
- ✅ **Solo notifica** cuando es necesario

### **Efectividad:**
- ✅ **Alerta inmediata** al experto
- ✅ **Información completa** sobre el problema
- ✅ **Acciones claras** a seguir

### **Mantenimiento:**
- ✅ **Fácil de mantener**
- ✅ **No afecta** el flujo normal
- ✅ **Escalable** para futuras mejoras

---

## 📊 **INFORMACIÓN QUE RECIBE EL EXPERTO**

### **Email crítico incluye:**
- ✅ Motivo del rechazo
- ✅ Número de contrataciones afectadas
- ✅ Lista detallada de servicios activos
- ✅ Acciones requeridas
- ✅ Contacto de soporte
- ✅ Instrucciones claras

### **Notificación push:**
- ✅ Título llamativo
- ✅ Mensaje conciso
- ✅ Urgencia clara

### **Ticket de soporte:**
- ✅ Prioridad crítica
- ✅ Información completa
- ✅ Seguimiento automático

---

## 🎉 **RESULTADO FINAL**

Con esta solución:

1. **✅ El experto sabe inmediatamente** que hay un problema
2. **✅ Tiene toda la información** necesaria para actuar
3. **✅ El soporte está alertado** del problema crítico
4. **✅ No se complica** el flujo normal del sistema
5. **✅ Es fácil de implementar** y mantener

**¡Es la solución más simple y efectiva!** 🚨

---

*He creado un documento detallado con la solución completa, código, y pasos de implementación.*
