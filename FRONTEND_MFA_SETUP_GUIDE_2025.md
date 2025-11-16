# 🚀 GUÍA FRONTEND 2025: CONFIGURACIÓN DE MFA OBLIGATORIO

**Target:** Equipo de Frontend React  
**Backend:** Ya implementado y listo ✅  
**Fecha:** Noviembre 2025  
**Tiempo estimado:** 4-6 horas  

---

## 📋 TABLA DE CONTENIDOS

1. [Resumen Ejecutivo](#resumen-ejecutivo)
2. [Arquitectura Visual](#arquitectura-visual)
3. [Implementación Rápida (30 min)](#implementación-rápida)
4. [Implementación Completa (4-6h)](#implementación-completa)
5. [Testing](#testing)
6. [Deployment](#deployment)

---

## RESUMEN EJECUTIVO

### 🎯 ¿Qué vamos a hacer?

Hacer que **MFA sea OBLIGATORIO** para:
- ✅ Administradores (role = 2)
- ✅ Expertos (role = 1)
- ⚠️ Clientes (role = 0) → Opcional

### 🔄 Flujo Visual Simplificado

```
Usuario → Google Login
    ↓
¿Es Admin/Expert?
    ↓
   SÍ → ¿Tiene MFA?
    ↓         ↓
   NO        SÍ
    ↓         ↓
 SETUP    VERIFY
    ↓         ↓
    ↓→→→→→→→→→↓
         ↓
     DASHBOARD ✅
```

### ⏱️ Tiempo de implementación

| Fase | Tiempo |
|------|--------|
| **Setup rápido (MVP)** | 30 min |
| **Implementación completa** | 4-6 horas |
| **Testing** | 1-2 horas |
| **Total** | 5-8 horas |

---

## ARQUITECTURA VISUAL

### 📂 Estructura de archivos a crear

```
src/
├── utils/
│   └── roleChecker.ts          🆕 CREAR (15 min)
├── hooks/
│   └── useMfaEnforcement.tsx   🆕 CREAR (30 min)
├── components/
│   ├── auth/
│   │   ├── GoogleAuth.tsx      ⚠️ MODIFICAR (10 min)
│   │   ├── MFASetup.tsx        ✅ Ya existe
│   │   └── MFAVerify.tsx       ✅ Ya existe
│   └── layout/
│       ├── ProtectedRoute.tsx  🆕 CREAR (20 min)
│       ├── MFABanner.tsx       🆕 CREAR (30 min)
│       └── MFASetupPage.tsx    🆕 CREAR (45 min)
└── App.tsx                     ⚠️ MODIFICAR (15 min)
```

**Total:** ~3 horas de código + 1-2 horas de testing

---

## IMPLEMENTACIÓN RÁPIDA

### MVP en 30 minutos ⚡

Si necesitas algo funcionando **YA**, sigue estos pasos:

#### PASO 1: Instalar dependencia (1 min)

```bash
npm install jwt-decode
```

#### PASO 2: Crear verificador de roles (5 min)

**Archivo:** `src/utils/roleChecker.ts`

```typescript
import jwtDecode from 'jwt-decode';

export enum UserRole {
  Client = 0,
  Expert = 1,
  Admin = 2
}

interface DecodedToken {
  'http://schemas.microsoft.com/ws/2008/06/identity/claims/role': string;
}

export const getRoleFromToken = (token: string): UserRole | null => {
  try {
    const decoded = jwtDecode<DecodedToken>(token);
    const roleClaim = 'http://schemas.microsoft.com/ws/2008/06/identity/claims/role';
    const roleString = decoded[roleClaim];
    
    switch (roleString) {
      case 'Client': return UserRole.Client;
      case 'Expert': return UserRole.Expert;
      case 'Admin': return UserRole.Admin;
      default: return null;
    }
  } catch {
    return null;
  }
};

export const requiresMfa = (role: UserRole | null): boolean => {
  return role === UserRole.Admin || role === UserRole.Expert;
};
```

#### PASO 3: Modificar GoogleAuth (10 min)

**Archivo:** `src/components/auth/GoogleAuth.tsx`

```typescript
import { authService } from '../../services/authService';
import { mfaService } from '../../services/mfaService';
import { getRoleFromToken, requiresMfa } from '../../utils/roleChecker';

// En handleGoogleSuccess, después del login:
const handleGoogleSuccess = async (credentialResponse: any) => {
  try {
    const result = await authService.googleAuth(credentialResponse.credential);
    
    if (!result.success) {
      throw new Error('Auth failed');
    }

    const token = authService.getAccessToken();
    if (!token) throw new Error('No token');

    const role = getRoleFromToken(token);
    
    // ✅ NUEVA LÓGICA MFA
    if (requiresMfa(role)) {
      const mfaStatus = await mfaService.getMFAStatus();
      
      if (!mfaStatus.isEnabled) {
        // ⚠️ MFA requerido pero NO configurado
        navigate('/mfa/setup-required');
        return;
      }
      
      // ✅ MFA configurado → Verificar
      setShowMfaVerify(true);
      return;
    }

    // Cliente → Dashboard directo
    navigate('/dashboard');

  } catch (error) {
    console.error('Login error:', error);
    setError('Error al iniciar sesión');
  }
};
```

#### PASO 4: Crear ruta protegida simple (10 min)

**Archivo:** `src/components/layout/ProtectedRoute.tsx`

```typescript
import React from 'react';
import { Navigate } from 'react-router-dom';
import { authService } from '../../services/authService';
import { getRoleFromToken, requiresMfa } from '../../utils/roleChecker';

export const ProtectedRoute: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  const token = authService.getAccessToken();
  
  if (!token) {
    return <Navigate to="/login" replace />;
  }

  return <>{children}</>;
};
```

#### PASO 5: Crear página de setup mínima (5 min)

**Archivo:** `src/pages/MFASetupPage.tsx`

```typescript
import React from 'react';
import { useNavigate } from 'react-router-dom';
import { MFASetup } from '../components/auth/MFASetup';

export const MFASetupPage: React.FC = () => {
  const navigate = useNavigate();

  return (
    <div className="min-h-screen bg-gray-50 py-8">
      <div className="max-w-2xl mx-auto">
        <div className="bg-red-50 border-2 border-red-500 rounded-lg p-6 mb-6">
          <h1 className="text-2xl font-bold mb-2">
            🔒 MFA Obligatorio
          </h1>
          <p>Como Admin o Experto, debes configurar MFA para continuar.</p>
        </div>
        
        <div className="bg-white rounded-lg p-6">
          <MFASetup 
            onComplete={() => navigate('/dashboard')} 
          />
        </div>
      </div>
    </div>
  );
};
```

#### PASO 6: Actualizar rutas en App.tsx (5 min)

```typescript
import { ProtectedRoute } from './components/layout/ProtectedRoute';
import { MFASetupPage } from './pages/MFASetupPage';

// En Routes:
<Route 
  path="/mfa/setup-required" 
  element={
    <ProtectedRoute>
      <MFASetupPage />
    </ProtectedRoute>
  } 
/>
```

### ✅ MVP Listo!

Con estos 6 pasos tienes:
- ✅ Detección de roles
- ✅ MFA obligatorio para Admin/Expert
- ✅ Redirección a setup si no está configurado
- ✅ Verificación en login

**Tiempo total: 30-40 minutos**

---

## IMPLEMENTACIÓN COMPLETA

Para una implementación **production-ready**, sigue la guía completa con:

### 🎯 Características adicionales

1. **Período de gracia** (3 días para configurar MFA)
2. **Advertencias graduales** (banners informativos)
3. **Session management** (control de sesiones activas)
4. **Error boundaries** (manejo robusto de errores)
5. **Analytics** (tracking de eventos MFA)
6. **Accessibility** (A11y compliant)
7. **Testing completo** (unit + integration + E2E)

### 📚 Componentes avanzados

#### 1. Hook de Enforcement con Período de Gracia

**Archivo:** `src/hooks/useMfaEnforcement.tsx`

```typescript
import { useState, useEffect } from 'react';
import { useNavigate } from 'react-router-dom';
import { authService } from '../services/authService';
import { mfaService } from '../services/mfaService';
import { getRoleFromToken, requiresMfa } from '../utils/roleChecker';

export function useMfaEnforcement() {
  const navigate = useNavigate();
  const [state, setState] = useState({
    isLoading: true,
    requiresSetup: false,
    gracePeriodDays: null as number | null,
    isEnforced: false
  });

  useEffect(() => {
    checkMfaStatus();
  }, []);

  const checkMfaStatus = async () => {
    try {
      const token = authService.getAccessToken();
      if (!token) {
        setState(prev => ({ ...prev, isLoading: false }));
        return;
      }

      const role = getRoleFromToken(token);
      
      if (!requiresMfa(role)) {
        // Cliente → No requiere MFA
        setState(prev => ({ ...prev, isLoading: false, requiresSetup: false }));
        return;
      }

      // Verificar estado MFA
      const mfaStatus = await mfaService.getMFAStatus();

      if (!mfaStatus.isEnabled) {
        // Calcular período de gracia
        const accountCreatedAt = await getAccountCreationDate();
        const daysSinceCreation = calculateDaysSince(accountCreatedAt);
        const gracePeriod = 3; // 3 días
        const remainingDays = Math.max(0, gracePeriod - daysSinceCreation);

        setState({
          isLoading: false,
          requiresSetup: true,
          gracePeriodDays: remainingDays,
          isEnforced: remainingDays === 0
        });

        // Si expiró el período → Forzar setup
        if (remainingDays === 0) {
          navigate('/mfa/setup-required');
        }
      } else {
        setState({
          isLoading: false,
          requiresSetup: false,
          gracePeriodDays: null,
          isEnforced: false
        });
      }
    } catch (error) {
      console.error('Error checking MFA:', error);
      setState(prev => ({ ...prev, isLoading: false }));
    }
  };

  const getAccountCreationDate = async (): Promise<Date> => {
    try {
      const response = await fetch('/api/user/profile', {
        headers: {
          'Authorization': `Bearer ${authService.getAccessToken()}`
        }
      });
      const data = await response.json();
      return new Date(data.createdAt);
    } catch {
      return new Date(); // Fallback: cuenta nueva
    }
  };

  const calculateDaysSince = (date: Date): number => {
    const now = new Date();
    const diffTime = Math.abs(now.getTime() - date.getTime());
    return Math.ceil(diffTime / (1000 * 60 * 60 * 24));
  };

  return {
    ...state,
    checkMfaStatus
  };
}
```

#### 2. Banner de Advertencia Progresiva

**Archivo:** `src/components/layout/MFABanner.tsx`

```typescript
import React, { useState } from 'react';
import { Link } from 'react-router-dom';
import { useMfaEnforcement } from '../../hooks/useMfaEnforcement';

export const MFABanner: React.FC = () => {
  const { requiresSetup, gracePeriodDays } = useMfaEnforcement();
  const [isDismissed, setIsDismissed] = useState(
    localStorage.getItem('mfa-banner-dismissed') === 'true'
  );

  if (!requiresSetup || isDismissed || gracePeriodDays === null) {
    return null;
  }

  const severity = gracePeriodDays >= 2 ? 'info' : gracePeriodDays === 1 ? 'warning' : 'critical';

  const styles = {
    info: 'bg-blue-50 border-blue-500 text-blue-900',
    warning: 'bg-yellow-50 border-yellow-500 text-yellow-900',
    critical: 'bg-red-50 border-red-500 text-red-900 animate-pulse'
  };

  const icons = {
    info: '💡',
    warning: '⚠️',
    critical: '🚨'
  };

  const handleDismiss = () => {
    setIsDismissed(true);
    localStorage.setItem('mfa-banner-dismissed', 'true');
  };

  return (
    <div className={`border-l-4 p-4 mb-4 ${styles[severity]}`} role="alert">
      <div className="flex items-start">
        <div className="text-2xl mr-3">{icons[severity]}</div>
        
        <div className="flex-1">
          <h3 className="font-bold text-lg mb-1">
            {severity === 'critical' 
              ? '🚨 ACCIÓN REQUERIDA: Configura MFA HOY' 
              : 'MFA Requerido'
            }
          </h3>
          
          <p className="mb-2">
            Como Admin/Experto, debes habilitar la Autenticación de Dos Factores (MFA).
          </p>

          <p className="mb-3">
            <strong>
              {gracePeriodDays > 0 
                ? `Tienes ${gracePeriodDays} día${gracePeriodDays !== 1 ? 's' : ''} restante${gracePeriodDays !== 1 ? 's' : ''}`
                : 'El período de gracia ha expirado'
              }
            </strong>
          </p>

          <div className="flex gap-3">
            <Link
              to="/mfa/setup"
              className="px-4 py-2 bg-primary text-white rounded-md hover:bg-primary-dark"
            >
              Configurar MFA ahora →
            </Link>
            
            {severity !== 'critical' && (
              <button
                onClick={handleDismiss}
                className="px-4 py-2 bg-gray-200 text-gray-700 rounded-md hover:bg-gray-300"
              >
                Recordar más tarde
              </button>
            )}
          </div>
        </div>
      </div>
    </div>
  );
};
```

#### 3. Página de Setup con Contexto

**Archivo:** `src/pages/MFASetupPage.tsx` (versión completa)

```typescript
import React, { useState } from 'react';
import { useNavigate, useLocation } from 'react-router-dom';
import { MFASetup } from '../components/auth/MFASetup';
import { getRoleFromToken } from '../utils/roleChecker';
import { authService } from '../services/authService';

export const MFASetupPage: React.FC = () => {
  const navigate = useNavigate();
  const location = useLocation();
  const [isCompleted, setIsCompleted] = useState(false);

  const reason = location.state?.reason;
  const isRequired = reason === 'grace_period_expired' || reason === 'admin_enforced';

  const token = authService.getAccessToken();
  const role = token ? getRoleFromToken(token) : null;
  const roleName = role === 2 ? 'Administrador' : role === 1 ? 'Experto' : 'Usuario';

  const handleComplete = () => {
    setIsCompleted(true);
    localStorage.removeItem('mfa-banner-dismissed'); // Limpiar banner
    setTimeout(() => navigate('/dashboard'), 2000);
  };

  if (isCompleted) {
    return (
      <div className="min-h-screen flex items-center justify-center bg-gray-50">
        <div className="max-w-md w-full bg-white rounded-lg shadow-lg p-8 text-center">
          <div className="text-6xl mb-4">✅</div>
          <h2 className="text-2xl font-bold mb-2">¡MFA Configurado!</h2>
          <p className="text-gray-600 mb-4">
            Tu cuenta ahora está protegida con autenticación de dos factores.
          </p>
          <p className="text-sm text-gray-500">Redirigiendo...</p>
        </div>
      </div>
    );
  }

  return (
    <div className="min-h-screen bg-gray-50 py-8">
      <div className="max-w-4xl mx-auto px-4">
        {/* Header */}
        <div className={`
          rounded-lg p-6 mb-6
          ${isRequired ? 'bg-red-50 border-2 border-red-500' : 'bg-blue-50 border-2 border-blue-500'}
        `}>
          <div className="flex items-start">
            <div className="text-4xl mr-4">{isRequired ? '🔒' : '🔐'}</div>
            <div>
              <h1 className="text-2xl font-bold mb-2">
                {isRequired ? 'Configuración Obligatoria de MFA' : 'Configura MFA'}
              </h1>
              
              <p className="mb-3">
                Como <strong>{roleName}</strong>, necesitas habilitar MFA para:
              </p>

              <ul className="list-disc list-inside space-y-1 mb-4">
                <li>Proteger tu cuenta contra accesos no autorizados</li>
                <li>Cumplir con normativas de seguridad (GDPR, PCI DSS)</li>
                <li>Proteger los datos que manejas</li>
                {role === 1 && <li>Proteger tus pagos y cuenta de Stripe</li>}
                {role === 2 && <li>Proteger el acceso administrativo</li>}
              </ul>

              {isRequired && (
                <div className="bg-white rounded p-3 border border-red-300">
                  <p className="text-sm text-red-800 font-semibold">
                    ⚠️ No podrás acceder sin completar este paso.
                  </p>
                </div>
              )}
            </div>
          </div>
        </div>

        {/* Info */}
        <div className="bg-white rounded-lg shadow-sm p-6 mb-6">
          <h2 className="text-xl font-semibold mb-4">📱 ¿Qué necesitas?</h2>
          
          <div className="grid md:grid-cols-2 gap-4">
            <div className="flex items-start">
              <div className="text-2xl mr-3">1️⃣</div>
              <div>
                <h3 className="font-semibold mb-1">App de autenticación</h3>
                <p className="text-sm text-gray-600">
                  Google Authenticator, Microsoft Authenticator, o similar
                </p>
              </div>
            </div>
            
            <div className="flex items-start">
              <div className="text-2xl mr-3">2️⃣</div>
              <div>
                <h3 className="font-semibold mb-1">Tu teléfono</h3>
                <p className="text-sm text-gray-600">
                  Para escanear el QR o ingresar la clave manualmente
                </p>
              </div>
            </div>
            
            <div className="flex items-start">
              <div className="text-2xl mr-3">3️⃣</div>
              <div>
                <h3 className="font-semibold mb-1">3 minutos</h3>
                <p className="text-sm text-gray-600">
                  El proceso es rápido y sencillo
                </p>
              </div>
            </div>
            
            <div className="flex items-start">
              <div className="text-2xl mr-3">4️⃣</div>
              <div>
                <h3 className="font-semibold mb-1">Lugar seguro</h3>
                <p className="text-sm text-gray-600">
                  Para guardar códigos de recuperación
                </p>
              </div>
            </div>
          </div>
        </div>

        {/* Setup Component */}
        <div className="bg-white rounded-lg shadow-lg p-6">
          <MFASetup onComplete={handleComplete} />
        </div>

        {/* Support */}
        <div className="mt-6 text-center text-sm text-gray-500">
          ¿Necesitas ayuda? {' '}
          <a href="/help/mfa" className="text-primary hover:underline">
            Ver guía completa
          </a>
        </div>
      </div>
    </div>
  );
};
```

#### 4. Agregar Banner al Dashboard

**Archivo:** `src/pages/Dashboard.tsx`

```typescript
import { MFABanner } from '../components/layout/MFABanner';

export const Dashboard: React.FC = () => {
  return (
    <div className="min-h-screen bg-gray-50">
      {/* ⚠️ Banner MFA */}
      <MFABanner />

      {/* Contenido del dashboard */}
      <div className="max-w-7xl mx-auto py-6 px-4">
        <h1 className="text-3xl font-bold mb-6">Dashboard</h1>
        {/* ... */}
      </div>
    </div>
  );
};
```

---

## TESTING

### Unit Tests

```typescript
// __tests__/roleChecker.test.ts
import { getRoleFromToken, requiresMfa, UserRole } from '../utils/roleChecker';

describe('roleChecker', () => {
  it('should detect Admin role', () => {
    const token = 'valid_jwt_token_here';
    const role = getRoleFromToken(token);
    expect(role).toBe(UserRole.Admin);
  });

  it('should require MFA for Admin', () => {
    expect(requiresMfa(UserRole.Admin)).toBe(true);
  });

  it('should not require MFA for Client', () => {
    expect(requiresMfa(UserRole.Client)).toBe(false);
  });
});
```

### Integration Tests (Cypress)

```typescript
// cypress/e2e/mfa-enforcement.cy.ts
describe('MFA Enforcement', () => {
  it('should force Admin to setup MFA', () => {
    cy.loginAsAdmin();
    cy.url().should('include', '/mfa/setup-required');
    cy.contains('Configuración Obligatoria');
  });

  it('should show grace period banner for Expert', () => {
    cy.loginAsNewExpert();
    cy.contains('Tienes 3 días restantes');
    cy.url().should('include', '/dashboard');
  });
});
```

---

## DEPLOYMENT

### Checklist de producción

- [ ] Todas las rutas protegidas con `<ProtectedRoute>`
- [ ] MFA obligatorio para Admin/Expert
- [ ] Banner de advertencia visible
- [ ] Período de gracia de 3 días configurado
- [ ] Tests pasando (unit + integration)
- [ ] Error boundaries implementados
- [ ] Analytics de MFA configurados
- [ ] Documentación actualizada
- [ ] Review de código completado
- [ ] QA aprobado

---

## 🎯 RESUMEN

### ✅ Lo que tendrás al final:

```
┌──────────────────────────────────────┐
│  MFA OBLIGATORIO IMPLEMENTADO ✅     │
├──────────────────────────────────────┤
│  ✅ Admin → MFA inmediato            │
│  ✅ Expert → 3 días de gracia        │
│  ✅ Client → Opcional                │
│  ✅ Advertencias graduales           │
│  ✅ Rutas protegidas                 │
│  ✅ Type-safe                        │
│  ✅ Testeable                        │
│  ✅ Production-ready                 │
└──────────────────────────────────────┘
```

### 📊 Tiempo estimado:

- **MVP básico:** 30-40 minutos
- **Implementación completa:** 4-6 horas
- **Testing:** 1-2 horas
- **Total:** 5-8 horas

### 🚀 Próximos pasos:

1. Implementar MVP (30 min)
2. Testear localmente
3. Si funciona → Implementar versión completa
4. Testing exhaustivo
5. Deploy a staging
6. QA y review
7. Deploy a producción

---

## 📞 SOPORTE

**¿Dudas?** Consulta:
- `FRONTEND_COMPLETE_GUIDE.md` - Guía completa
- `SECURITY_AUDIT_2025.md` - Contexto de seguridad
- `MFA_COMPLETE_IMPLEMENTATION.md` - Detalles MFA

**¡Éxito con la implementación!** 🎉


