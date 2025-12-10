# ⚠️ Endpoint `/api/User/profile` NO EXISTE

## Problema
El frontend está intentando llamar a `GET /api/User/profile` pero **este endpoint no existe** en el backend.

## Soluciones Alternativas

### 1. **Para usuarios Expertos** ✅ RECOMENDADO
Usar el endpoint existente que devuelve información completa del experto:

```http
GET /api/User/expert-profile
Authorization: Bearer {token}
```

**Response (200 OK):**
```json
{
  "id": 52,
  "profilePictureUrl": "https://...",
  "description": "...",
  "user": {
    "id": 0,
    "email": "user@example.com",
    "name": "User Name",
    "profilePictureUrl": null
  },
  "latitude": "...",
  "longitude": "...",
  "stripeStatus": 2,
  "onboardingCompleted": true,
  "isOnVacation": false,
  "currentAvailability": { ... }
}
```

**Nota:** Este endpoint devuelve `404` si el usuario no es experto.

### 2. **Información del Usuario desde el Token JWT**
El token JWT contiene el `userId` en el claim `NameIdentifier`. Puedes decodificar el token para obtener el ID del usuario.

### 3. **Guardar información del usuario al hacer login**
Los endpoints de autenticación ya devuelven información del usuario:

**`POST /api/User/google-auth`** devuelve:
```json
{
  "token": "...",
  "user": {
    "id": 123,
    "name": "User Name",
    "email": "user@example.com",
    "phoneVerified": true,
    "role": "Client"
  }
}
```

**`POST /api/User/become-expert`** devuelve:
```json
{
  "message": "Successfully became an expert",
  "token": "...",
  "user": {
    "id": 123,
    "name": "User Name",
    "email": "user@example.com",
    "phoneVerified": true,
    "role": "Expert",
    "expertProfile": { ... }
  }
}
```

**Recomendación:** Guardar esta información en el estado de la aplicación (Redux, Context, etc.) al hacer login y reutilizarla en lugar de hacer una llamada adicional.

### 4. **Para Configuraciones del Usuario**
Si necesitas las configuraciones del usuario:

```http
GET /api/UserSettings
Authorization: Bearer {token}
```

**Response:**
```json
{
  "isWhatsAppEnabled": true,
  "isEmailEnabled": true,
  "theme": "light"
}
```

## Resumen

- ❌ **NO usar:** `GET /api/User/profile` (no existe)
- ✅ **Para Expertos:** `GET /api/User/expert-profile`
- ✅ **Guardar info al login:** Usar la respuesta de `google-auth` o `become-expert`
- ✅ **Para settings:** `GET /api/UserSettings`

## Acción Requerida en Frontend

1. **Eliminar** la llamada a `/api/User/profile`
2. **Usar** `/api/User/expert-profile` si el usuario es experto
3. **Guardar** la información del usuario al hacer login y reutilizarla
4. **Manejar** el caso cuando el usuario no es experto (404 en expert-profile)



