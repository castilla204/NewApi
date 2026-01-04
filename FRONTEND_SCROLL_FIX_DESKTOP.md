# 🔧 Solución: Scroll No Funciona en PC

## 🐛 Problema
El scroll vertical no funciona en desktop cuando hay un modal abierto o cuando se bloquea el scroll del body.

## ✅ Solución

### **1. Verificar y Corregir el Bloqueo de Scroll**

El problema típico es que cuando se abre un modal, se aplica `pointer-events: none` y `data-scroll-locked="1"` al body, pero no se restaura correctamente al cerrar el modal.

#### **Solución en el Componente del Modal:**

```typescript
// En tu componente de modal (ej: CategoriesModal.tsx)
import { useEffect } from 'react';

const CategoriesModal = ({ isOpen, onClose }) => {
  useEffect(() => {
    if (isOpen) {
      // Bloquear scroll del body cuando el modal está abierto
      document.body.style.overflow = 'hidden';
      document.body.style.pointerEvents = 'none';
      // Permitir eventos en el modal
      const modal = document.getElementById('categories-modal');
      if (modal) {
        modal.style.pointerEvents = 'auto';
      }
    } else {
      // Restaurar scroll cuando el modal se cierra
      document.body.style.overflow = '';
      document.body.style.pointerEvents = '';
    }

    // Cleanup: siempre restaurar al desmontar
    return () => {
      document.body.style.overflow = '';
      document.body.style.pointerEvents = '';
    };
  }, [isOpen]);

  if (!isOpen) return null;

  return (
    <div 
      id="categories-modal"
      className="modal-overlay"
      onClick={onClose}
    >
      <div 
        className="modal-content"
        onClick={(e) => e.stopPropagation()}
      >
        {/* Contenido del modal */}
      </div>
    </div>
  );
};
```

### **2. CSS para Asegurar Scroll en Desktop**

```css
/* Asegurar que el body siempre pueda hacer scroll en desktop */
@media (min-width: 769px) {
  body {
    overflow-y: auto !important;
    pointer-events: auto !important;
  }

  /* El modal no debe bloquear el scroll del body en desktop */
  .modal-overlay {
    position: fixed;
    top: 0;
    left: 0;
    right: 0;
    bottom: 0;
    background: rgba(0, 0, 0, 0.5);
    z-index: 1000;
    /* En desktop, permitir scroll del modal si es necesario */
    overflow-y: auto;
  }

  .modal-content {
    position: relative;
    margin: 2rem auto;
    max-width: 600px;
    background: white;
    border-radius: 12px;
    padding: 1.5rem;
    pointer-events: auto;
    max-height: calc(100vh - 4rem);
    overflow-y: auto;
  }
}

/* En móvil, mantener el comportamiento actual */
@media (max-width: 768px) {
  body.modal-open {
    overflow: hidden;
    position: fixed;
    width: 100%;
  }

  .modal-overlay {
    position: fixed;
    top: 0;
    left: 0;
    right: 0;
    bottom: 0;
    background: rgba(0, 0, 0, 0.5);
    z-index: 1000;
    overflow-y: auto;
    -webkit-overflow-scrolling: touch;
  }

  .modal-content {
    position: relative;
    margin: 1rem;
    background: white;
    border-radius: 12px;
    padding: 1rem;
    pointer-events: auto;
    max-height: calc(100vh - 2rem);
    overflow-y: auto;
  }
}
```

### **3. Hook Personalizado para Manejar Scroll Lock**

```typescript
// hooks/useScrollLock.ts
import { useEffect } from 'react';

export const useScrollLock = (isLocked: boolean, isMobile: boolean = false) => {
  useEffect(() => {
    // Solo bloquear scroll en móvil
    if (isLocked && isMobile) {
      const originalStyle = window.getComputedStyle(document.body).overflow;
      document.body.style.overflow = 'hidden';
      
      return () => {
        document.body.style.overflow = originalStyle;
      };
    }
  }, [isLocked, isMobile]);
};

// Uso en el componente:
const CategoriesModal = ({ isOpen, onClose }) => {
  const isMobile = window.innerWidth <= 768;
  useScrollLock(isOpen, isMobile);

  // ... resto del componente
};
```

### **4. Detectar si es Desktop y Permitir Scroll**

```typescript
// utils/scrollUtils.ts
export const lockBodyScroll = (lock: boolean, isMobile: boolean = false) => {
  if (lock && isMobile) {
    // Solo bloquear en móvil
    document.body.style.overflow = 'hidden';
    document.body.style.position = 'fixed';
    document.body.style.width = '100%';
  } else {
    // En desktop, siempre permitir scroll
    document.body.style.overflow = '';
    document.body.style.position = '';
    document.body.style.width = '';
  }
};

export const unlockBodyScroll = () => {
  document.body.style.overflow = '';
  document.body.style.position = '';
  document.body.style.width = '';
  document.body.style.pointerEvents = '';
  // Remover atributo data-scroll-locked si existe
  document.body.removeAttribute('data-scroll-locked');
};
```

### **5. Solución Completa con Detección de Dispositivo**

```typescript
// components/CategoriesModal.tsx
import { useEffect, useState } from 'react';

const CategoriesModal = ({ isOpen, onClose }) => {
  const [isMobile, setIsMobile] = useState(false);

  useEffect(() => {
    const checkMobile = () => {
      setIsMobile(window.innerWidth <= 768);
    };
    
    checkMobile();
    window.addEventListener('resize', checkMobile);
    
    return () => window.removeEventListener('resize', checkMobile);
  }, []);

  useEffect(() => {
    if (isOpen) {
      if (isMobile) {
        // Solo bloquear scroll en móvil
        document.body.style.overflow = 'hidden';
        document.body.style.position = 'fixed';
        document.body.style.width = '100%';
      }
      // En desktop, no hacer nada - permitir scroll normal
    } else {
      // Siempre restaurar al cerrar
      document.body.style.overflow = '';
      document.body.style.position = '';
      document.body.style.width = '';
      document.body.style.pointerEvents = '';
      document.body.removeAttribute('data-scroll-locked');
    }

    return () => {
      // Cleanup seguro
      document.body.style.overflow = '';
      document.body.style.position = '';
      document.body.style.width = '';
      document.body.style.pointerEvents = '';
      document.body.removeAttribute('data-scroll-locked');
    };
  }, [isOpen, isMobile]);

  if (!isOpen) return null;

  return (
    <div 
      className={`modal-overlay ${isMobile ? 'mobile' : 'desktop'}`}
      onClick={onClose}
    >
      <div 
        className="modal-content"
        onClick={(e) => e.stopPropagation()}
      >
        {/* Contenido del modal */}
        <button onClick={onClose}>Cerrar</button>
      </div>
    </div>
  );
};
```

## 🎯 Puntos Clave

1. **En Desktop**: NO bloquear el scroll del body cuando hay un modal
2. **En Móvil**: SÍ bloquear el scroll para evitar scroll del fondo
3. **Cleanup**: Siempre restaurar los estilos al cerrar el modal
4. **Detección**: Usar media queries o JavaScript para detectar desktop vs móvil

## 🔍 Verificación

Después de aplicar la solución:

1. Abre el modal en desktop
2. Intenta hacer scroll en la página de fondo - **debe funcionar**
3. El modal debe poder hacer scroll si tiene mucho contenido
4. Al cerrar el modal, el scroll debe seguir funcionando normalmente

## ⚠️ Nota

Si usas alguna librería de modales (como `react-modal`, `@headlessui/react`, etc.), verifica su configuración para asegurarte de que no esté bloqueando el scroll en desktop.

