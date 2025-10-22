# Validación de Ubicación en Citas

## Resumen

Se ha implementado un sistema de validación que asegura que las citas propuestas por los clientes estén dentro del rango de servicio del experto **tal como estaba definido al momento de la contratación**.

## Funcionamiento

### 1. Validación de Ubicación
- **Cuándo se valida**: Al crear o proponer una cita
- **Qué se valida**: Que la ubicación propuesta esté dentro del rango del experto
- **Rango usado**: El rango definido en el `SearchParameter` original del cliente
- **Coordenadas del experto**: Las coordenadas almacenadas en el `ExpertProfile` al momento de crear el servicio

### 2. Cálculo de Distancia
- Se usa la fórmula de Haversine para calcular la distancia entre:
  - Ubicación del experto (al momento de la contratación)
  - Ubicación propuesta para la cita
- La distancia se compara con el rango máximo permitido

### 3. Comportamiento ante Cambios de Ubicación del Experto

#### ✅ **Se mantiene la ubicación original**
Si un experto cambia su ubicación después de ser contratado:
- Las citas siguen siendo válidas dentro del rango original
- El experto no puede "escapar" de sus compromisos
- Se preserva la integridad del contrato

#### Ejemplo:
1. **Cliente busca servicios** en Madrid con rango de 50km
2. **Experto en Alcalá de Henares** (30km de Madrid) ofrece servicio
3. **Cliente contrata** al experto
4. **Experto se muda** a Barcelona después de ser contratado
5. **Cliente propone cita** en Madrid
6. **✅ VÁLIDA**: La cita es válida porque Madrid está a 30km de Alcalá de Henares (ubicación original)

## Implementación Técnica

### Método de Validación
```csharp
private async Task ValidateAppointmentLocationAsync(SearchHire searchHire, decimal? appointmentLatitude, decimal? appointmentLongitude)
```

### Datos Utilizados
- **Coordenadas del experto**: `SearchService.ExpertProfile.Latitude/Longitude`
- **Rango máximo**: `Search.SearchParameters.FirstOrDefault().LocationRange`
- **Ubicación de la cita**: `appointmentLatitude/appointmentLongitude`

### Mensaje de Error
```
La ubicación propuesta para la cita está fuera del rango del experto. 
Distancia: X.X km, Rango máximo: XX km. 
El experto solo puede realizar citas dentro de su rango de servicio original.
```

## Beneficios

1. **Integridad del Contrato**: Los expertos no pueden cambiar las condiciones después de ser contratados
2. **Protección del Cliente**: Los clientes pueden confiar en que el experto cumplirá dentro del rango acordado
3. **Consistencia**: El sistema mantiene las condiciones originales durante toda la duración del servicio
4. **Prevención de Abuso**: Evita que expertos cambien su ubicación para evitar citas

## Casos de Uso

### Caso 1: Experto se muda después de ser contratado
- **Resultado**: Las citas siguen siendo válidas dentro del rango original
- **Beneficio**: Protege al cliente de cambios inesperados

### Caso 2: Cliente propone cita fuera del rango
- **Resultado**: Se rechaza la propuesta con mensaje explicativo
- **Beneficio**: Evita citas imposibles de cumplir

### Caso 3: Rango no definido
- **Resultado**: Se usa un rango por defecto de 50km
- **Beneficio**: Garantiza que siempre hay una validación

## Configuración

### Rango por Defecto
- Si no se define un rango en `SearchParameter.LocationRange`, se usa **50km**

### Logging
- Se registran todas las validaciones para auditoría
- Incluye coordenadas, distancias y rangos utilizados

## Consideraciones Futuras

1. **Notificaciones**: Podría implementarse una notificación al experto cuando se propone una cita
2. **Flexibilidad**: Podría permitirse modificar el rango con consentimiento mutuo
3. **Historial**: Mantener un historial de cambios de ubicación del experto









