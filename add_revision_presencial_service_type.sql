-- Script para añadir el tipo de servicio "Revisión presencial" a la base de datos
-- Este script verifica si el tipo de servicio ya existe antes de insertarlo

DO $EF$
DECLARE
    max_position INTEGER;
    next_id INTEGER;
BEGIN
    -- Verificar si ya existe un tipo de servicio con el nombre "Revisión presencial"
    IF NOT EXISTS (
        SELECT 1 
        FROM "ServiceTypes" 
        WHERE "Name" = 'Revisión presencial'
    ) THEN
        -- Obtener la siguiente posición disponible para la categoría "Revisión"
        SELECT COALESCE(MAX("Position"), 0) + 1 INTO max_position
        FROM "ServiceTypes"
        WHERE "ServiceTypeCategoryId" = 2; -- Categoría "Revisión"
        
        -- Obtener el siguiente ID disponible
        SELECT COALESCE(MAX("Id"), 0) + 1 INTO next_id
        FROM "ServiceTypes";
        
        -- Insertar el nuevo tipo de servicio
        INSERT INTO "ServiceTypes" (
            "Id",
            "Name", 
            "Description", 
            "ServiceTypeCategoryId",
            "Position",
            "IsActive", 
            "RequiresAppointment",
            "CreatedAt", 
            "UpdatedAt"
        )
        VALUES (
            next_id,
            'Revisión presencial',
            'Servicio de revisión presencial de productos o servicios',
            2, -- ServiceTypeCategoryId = 2 corresponde a "Revisión" (Solo revisión presencial)
            max_position,
            TRUE,
            TRUE, -- Requiere cita presencial
            CURRENT_TIMESTAMP,
            CURRENT_TIMESTAMP
        );
        
        RAISE NOTICE 'Tipo de servicio "Revisión presencial" añadido exitosamente con ID: %', next_id;
    ELSE
        RAISE NOTICE 'El tipo de servicio "Revisión presencial" ya existe en la base de datos';
    END IF;
END $EF$;

