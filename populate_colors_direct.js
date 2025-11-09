// Script Node.js para poblar colores en SystemStatuses
// Ejecutar: node populate_colors_direct.js

const { Client } = require('pg');

const client = new Client({
    host: '185.166.39.4',
    port: 30000,
    database: 'atrapo',
    user: 'admin',
    password: 'Pedrohabo1//',
});

async function populateColors() {
    try {
        await client.connect();

        // Actualizar colores para estados existentes
        const updateResult = await client.query(`
            UPDATE "SystemStatuses" 
            SET "Color" = CASE 
                WHEN "StatusValue" = 'pending' THEN '#FFA500'  -- Naranja para pendiente
                WHEN "StatusValue" = 'completed' THEN '#28A745'  -- Verde para completado
                WHEN "StatusValue" = 'cancelled' THEN '#DC3545'  -- Rojo para cancelado
                WHEN "StatusValue" = 'dispute_resolved_client' THEN '#17A2B8'  -- Azul para disputa resuelta
                WHEN "StatusValue" = 'appointment_proposed' THEN '#6F42C1'  -- Púrpura para propuesta
                WHEN "StatusValue" = 'appointment_confirmed' THEN '#20C997'  -- Verde azulado para confirmado
                WHEN "StatusValue" = 'appointment_rejected' THEN '#FD7E14'  -- Naranja oscuro para rechazado
                WHEN "StatusValue" = 'appointment_completed' THEN '#28A745'  -- Verde para completado
                WHEN "StatusValue" = 'appointment_cancelled' THEN '#DC3545'  -- Rojo para cancelado
                WHEN "StatusValue" = 'appointment_report_sent' THEN '#6610F2'  -- Púrpura para reporte enviado
                WHEN "StatusValue" = 'awaiting_appointment' THEN '#FFC107'  -- Amarillo para esperando cita
                WHEN "StatusValue" = 'expert_report_timeout' THEN '#E83E8C'  -- Rosa para timeout
                ELSE '#6C757D'  -- Gris por defecto
            END
            WHERE "Color" IS NULL;
        `);

        // Verificar resultados
        const result = await client.query(`
            SELECT "StatusValue", "DisplayName", "Color" 
            FROM "SystemStatuses" 
            ORDER BY "StatusType", "SortOrder" 
            LIMIT 15;
        `);

        result.rows.forEach(row => {
        });

        
    } catch (error) {
        console.error('❌ Error:', error.message);
    } finally {
        await client.end();
    }
}

// Ejecutar la función
populateColors();

