# 🔧 Cambio: ProfilePictureUrl en ExpertProfileDto

## ⚠️ Cambio Importante

Se ha corregido un problema donde se devolvían **dos campos `profilePictureUrl`**, uno de ellos `null`.

---

## ✅ Solución Implementada

### **Antes (Incorrecto)**
```json
{
  "id": 39,
  "profilePictureUrl": "https://...",
  "user": {
    "id": 13,
    "email": "expert@example.com",
    "name": "Juan Pérez"
    // ❌ profilePictureUrl no se establecía explícitamente, podía ser null
  }
}
```

### **Ahora (Correcto)**
```json
{
  "id": 39,
  "profilePictureUrl": "https://storage.googleapis.com/atrapobucket/experts/abc123.jpg",
  "user": {
    "id": 13,
    "email": "expert@example.com",
    "name": "Juan Pérez",
    "profilePictureUrl": null  // ✅ Explícitamente null
  }
}
```

---

## 🎯 Qué Cambiar en el Frontend

### **✅ CORRECTO: Usar profilePictureUrl del nivel superior**

```typescript
// ✅ CORRECTO
const profileImageUrl = expertProfile.profilePictureUrl || '/default-avatar.png';

// ❌ INCORRECTO - user.profilePictureUrl siempre será null
const profileImageUrl = expertProfile.user.profilePictureUrl || '/default-avatar.png';
```

### **Ejemplo en React/TypeScript**

```typescript
interface ExpertProfile {
  id: number;
  profilePictureUrl: string; // ✅ URL de la imagen de perfil
  description: string;
  user: {
    id: number;
    email: string;
    name: string;
    profilePictureUrl: null; // ✅ SIEMPRE null - no usar este campo
  };
  // ... otros campos
}

function ExpertProfileDisplay({ expertProfile }: { expertProfile: ExpertProfile }) {
  return (
    <div>
      {/* ✅ CORRECTO: Usar profilePictureUrl del nivel superior */}
      <img 
        src={expertProfile.profilePictureUrl || '/default-avatar.png'} 
        alt="Profile" 
      />
      
      {/* ❌ INCORRECTO: NO usar user.profilePictureUrl */}
      {/* <img src={expertProfile.user.profilePictureUrl} /> */}
      
      <h2>{expertProfile.user.name}</h2>
      <p>{expertProfile.user.email}</p>
      <p>{expertProfile.description}</p>
    </div>
  );
}
```

---

## 📋 Resumen

| Campo | Ubicación | Valor | ¿Usar? |
|-------|-----------|-------|--------|
| `profilePictureUrl` | Nivel superior (`ExpertProfileDto`) | URL de la imagen | ✅ **SÍ** |
| `user.profilePictureUrl` | Dentro de `user` (`UserDto`) | `null` | ❌ **NO** |

---

## 🔍 Verificación

Si tu código actual usa `expertProfile.user.profilePictureUrl`, cámbialo a `expertProfile.profilePictureUrl`:

```typescript
// ❌ ANTES (incorrecto)
const imageUrl = expertProfile.user.profilePictureUrl || '/default-avatar.png';

// ✅ AHORA (correcto)
const imageUrl = expertProfile.profilePictureUrl || '/default-avatar.png';
```

---

## ✅ Checklist

- [ ] Buscar en el código todas las referencias a `expertProfile.user.profilePictureUrl`
- [ ] Reemplazarlas por `expertProfile.profilePictureUrl`
- [ ] Verificar que las imágenes se muestran correctamente
- [ ] Probar con expertos que tienen imagen y sin imagen

---

## 🎯 Motivo del Cambio

El perfil de imagen del experto está almacenado en `ExpertProfile`, no en `User`. Por lo tanto:
- `ExpertProfileDto.profilePictureUrl` → Contiene la URL de la imagen del experto
- `UserDto.profilePictureUrl` → Siempre será `null` para expertos (se establece explícitamente)

Esto evita confusión y asegura que siempre uses el campo correcto.
