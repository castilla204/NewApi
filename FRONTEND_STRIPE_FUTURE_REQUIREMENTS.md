# 🚨 **GUÍA FRONTEND - STRIPE FUTURE REQUIREMENTS**

## 📋 **RESUMEN DE CAMBIOS**

Se ha implementado el **monitoreo proactivo de Future Requirements de Stripe** según la documentación oficial de Stripe (Nov 2025). Esto permite notificar a los expertos sobre requirements que deben completar **antes** de que pasen a `past_due` y bloqueen su cuenta.

---

## 🎯 **NUEVOS CAMPOS EN DTOs**

Se han agregado **2 nuevos campos** a todos los DTOs relacionados con el perfil de experto:

### **Campos Agregados:**
```typescript
interface ExpertProfileDto {
  // ... campos existentes ...
  
  // ✅ NUEVO: Requirements que se deben completar en el futuro
  stripeFutureRequirements: string | null;
  // Formato: "individual.verification.document, business_profile.url, etc."
  // Separados por comas si hay múltiples
  
  // ✅ NUEVO: Fecha estimada de vencimiento
  stripeFutureDueAt: string | null; // ISO 8601 date (ej: "2025-12-15T00:00:00Z")
  // null si no hay requirements pendientes
}
```

**Mismos campos agregados en:**
- `OnboardingStatusDto`
- `ExpertStatusDto`

---

## 🔍 **ENDPOINTS AFECTADOS**

Los siguientes endpoints ahora incluyen los nuevos campos:

### **1. GET /api/User/expert-profile**
```typescript
GET /api/User/expert-profile
Authorization: Bearer {token}
```

**Response:**
```json
{
  "id": 52,
  "profilePictureUrl": "https://...",
  "description": "Descripción del experto",
  "stripeAccountId": "acct_1S7K9dR92l5GeyCp",
  "stripeStatus": 2,
  "stripeStatusDetails": "✅ **Cuenta Aprobada**: ...",
  "onboardingCompleted": true,
  "isOnVacation": false,
  "stripeFutureRequirements": "individual.verification.document, business_profile.url",
  "stripeFutureDueAt": "2025-12-15T00:00:00Z"
}
```

### **2. GET /api/Subscription/onboarding-status**
```typescript
GET /api/Subscription/onboarding-status
Authorization: Bearer {token}
```

**Response:**
```json
{
  "hasStripeAccount": true,
  "hasPendingOnboarding": false,
  "onboardingCompleted": true,
  "stripeAccountId": "acct_1S7K9dR92l5GeyCp",
  "stripeStatus": "Approved",
  "stripeStatusDetails": "✅ **Cuenta Aprobada**: ...",
  "canAccessStripe": true,
  "stripeFutureRequirements": "individual.verification.document",
  "stripeFutureDueAt": "2025-12-15T00:00:00Z"
}
```

### **3. GET /api/Subscription/expert-status**
```typescript
GET /api/Subscription/expert-status
Authorization: Bearer {token}
```

**Response:**
```json
{
  "hasStripeAccount": true,
  "onboardingCompleted": true,
  "stripeStatus": "Approved",
  "stripeStatusDetails": "✅ **Cuenta Aprobada**: ...",
  "canCreateServices": true,
  "canReceivePayments": true,
  "statusMessage": "Tu cuenta está activa y lista para recibir pagos",
  "stripeFutureRequirements": "individual.verification.document",
  "stripeFutureDueAt": "2025-12-15T00:00:00Z"
}
```

### **4. GET /api/SearchService**
```typescript
GET /api/SearchService
```

**Response:**
```json
[
  {
    "id": 140,
    "expert": {
      "id": 52,
      "stripeStatus": 2,
      "stripeFutureRequirements": "individual.verification.document",
      "stripeFutureDueAt": "2025-12-15T00:00:00Z"
    }
  }
]
```

### **5. GET /api/Search/{searchId}/details-complete**
```typescript
GET /api/Search/{searchId}/details-complete
```

**Response:**
```json
{
  "expertProfile": {
    "id": 52,
    "stripeStatus": 2,
    "stripeFutureRequirements": "individual.verification.document",
    "stripeFutureDueAt": "2025-12-15T00:00:00Z"
  }
}
```

---

## 📊 **VALORES POSIBLES**

### **stripeFutureRequirements:**
- **`null`**: No hay requirements futuros pendientes ✅
- **`"individual.verification.document"`**: Un solo requirement
- **`"individual.verification.document, business_profile.url"`**: Múltiples requirements separados por comas

### **stripeFutureDueAt:**
- **`null`**: No hay requirements pendientes
- **`"2025-12-15T00:00:00Z"`**: Fecha estimada de vencimiento (ISO 8601)
- **Para `eventually_due`**: Fecha estimada (~30 días desde ahora)
- **Para `past_due`**: Fecha en el pasado (ya vencido)

---

## 💡 **IMPLEMENTACIÓN RECOMENDADA EN FRONTEND**

### **1. Panel de Experto - Alerta de Future Requirements**

```typescript
interface ExpertProfile {
  // ... otros campos ...
  stripeFutureRequirements: string | null;
  stripeFutureDueAt: string | null;
}

// En el componente del panel experto
const hasFutureRequirements = expertProfile.stripeFutureRequirements !== null;

if (hasFutureRequirements) {
  const requirements = expertProfile.stripeFutureRequirements.split(', ');
  const dueDate = new Date(expertProfile.stripeFutureDueAt);
  const isPastDue = dueDate < new Date();
  
  // Mostrar alerta
  <Alert severity="warning">
    <AlertTitle>
      {isPastDue ? '⚠️ Requirements Vencidos' : '⚠️ Requirements Pendientes'}
    </AlertTitle>
    <p>
      Debes completar los siguientes requirements de Stripe:
    </p>
    <ul>
      {requirements.map(req => (
        <li key={req}>{req}</li>
      ))}
    </ul>
    <p>
      {isPastDue 
        ? '⚠️ Estos requirements ya están vencidos. Completa tu información en Stripe para evitar problemas.'
        : `📅 Fecha límite estimada: ${dueDate.toLocaleDateString()}`
      }
    </p>
    <Button 
      onClick={() => window.open(`https://dashboard.stripe.com/connect/accounts/${expertProfile.stripeAccountId}`, '_blank')}
    >
      Completar en Stripe Dashboard
    </Button>
  </Alert>
}
```

### **2. Badge/Indicador en Header**

```typescript
// Badge en el header del panel experto
{hasFutureRequirements && (
  <Badge badgeContent="!" color="warning">
    <Icon>warning</Icon>
  </Badge>
)}
```

### **3. Notificación Toast al Cargar Panel**

```typescript
useEffect(() => {
  if (expertProfile.stripeFutureRequirements) {
    const requirements = expertProfile.stripeFutureRequirements.split(', ');
    toast.warning(
      `⚠️ Tienes ${requirements.length} requirement(s) pendiente(s) de Stripe. 
       Completa tu información para evitar problemas.`,
      { duration: 5000 }
    );
  }
}, [expertProfile.stripeFutureRequirements]);
```

---

## 🔔 **TIPOS DE REQUIREMENTS COMUNES**

Según Stripe, los requirements más comunes son:

- `individual.verification.document` - Documento de identidad
- `individual.verification.additional_document` - Documento adicional
- `business_profile.url` - URL del sitio web del negocio
- `business_profile.mcc` - Código MCC del negocio
- `external_account` - Información bancaria
- `individual.id_number` - Número de identificación

**Formato:** Siempre separados por `, ` (coma + espacio) si hay múltiples.

---

## ✅ **COMPORTAMIENTO**

1. **Si no hay requirements**: Ambos campos son `null`
2. **Si hay requirements**: 
   - `stripeFutureRequirements` contiene la lista separada por comas
   - `stripeFutureDueAt` contiene la fecha estimada
3. **Actualización automática**: Los campos se actualizan automáticamente cuando Stripe envía el webhook `account.updated`
4. **Solo para expertos con cuenta Stripe**: Los campos solo tienen valores si `stripeAccountId` no es `null`

---

## 📝 **EJEMPLO COMPLETO DE RESPUESTA**

```json
{
  "id": 52,
  "profilePictureUrl": "https://storage.googleapis.com/...",
  "description": "Experto en motos",
  "stripeAccountId": "acct_1S7K9dR92l5GeyCp",
  "stripeStatus": 2,
  "stripeStatusDetails": "✅ **Cuenta Aprobada**: ¡Excelente! Tu cuenta de pagos está completamente verificada y lista para recibir pagos.",
  "onboardingCompleted": true,
  "isOnVacation": false,
  "stripeFutureRequirements": "individual.verification.document, business_profile.url",
  "stripeFutureDueAt": "2025-12-15T00:00:00Z",
  "currentAvailability": {
    "id": 1,
    "daysOfWeek": ["Monday", "Tuesday", "Wednesday"],
    "startTime": "09:00:00",
    "endTime": "18:00:00",
    "effectiveFrom": "2025-01-15T10:30:00Z"
  }
}
```

---

## 🚨 **IMPORTANTE**

- **Estos campos son informativos**: No bloquean la cuenta, pero deben completarse para mantener el estado activo
- **Monitoreo proactivo**: Se actualizan automáticamente cuando Stripe notifica cambios
- **Compatibilidad**: Si los campos no existen en versiones antiguas, serán `null` (no rompe la compatibilidad)

---

## 📚 **REFERENCIA OFICIAL**

- [Stripe Connect Future Requirements Docs](https://stripe.com/docs/connect/future-requirements)
- [Stripe Webhooks Best Practices](https://stripe.com/docs/webhooks/best-practices)

