// Script Node.js para poblar colores en SystemStatuses
// Ejecutar: node populate_colors_direct.js

const { Client } = require('pg');

const client = new Client({
    host: '185.166.39.4',
    port: 30000,
    database: 'atrapo',
    user: 'admin',
    password: '__REDACTED_CREDENTIAL__',
});

async function populateColors() {
    try {
        console.log('🔌 Conectando a la base de datos...');
        await client.connect();
        console.log('✅ Conectado a PostgreSQL');

        // Actualizar colores para estados existentes
        console.log('🎨 Actualizando colores...');
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
        console.log(`✅ Colores actualizados para ${updateResult.rowCount} registros`);

        // Verificar resultados
        console.log('\n📋 Estados con colores:');
        const result = await client.query(`
            SELECT "StatusValue", "DisplayName", "Color" 
            FROM "SystemStatuses" 
            ORDER BY "StatusType", "SortOrder" 
            LIMIT 15;
        `);

        result.rows.forEach(row => {
            console.log(`  ${row.StatusValue} - ${row.DisplayName} - ${row.Color}`);
        });

        console.log('\n✅ Colores poblados exitosamente');
        console.log('\n🎯 PRÓXIMO PASO:');
        console.log('Probar el endpoint: GET /api/Search/243/details-complete');
        
    } catch (error) {
        console.error('❌ Error:', error.message);
        console.log('Asegúrate de que PostgreSQL esté ejecutándose y las credenciales sean correctas');
    } finally {
        await client.end();
    }
}

// Ejecutar la función
populateColors();

