// Script para poblar colores en SystemStatuses si no están ya poblados
// Archivo: populate_colors_if_needed.js

const { Client } = require('pg');

const client = new Client({
    host: '185.166.39.4',
    port: 30000,
    database: 'atrapo',
    user: 'admin',
    password: 'Pedrohabo1//'
});

async function populateColorsIfNeeded() {
    try {
        await client.connect();
        console.log('🔌 Conectado a PostgreSQL');

        // Verificar si ya hay colores poblados
        const checkResult = await client.query(`
            SELECT COUNT(*) as count 
            FROM "SystemStatuses" 
            WHERE "Color" IS NOT NULL AND "Color" != ''
        `);
        
        const coloredCount = parseInt(checkResult.rows[0].count);
        console.log(`📊 Estados con color: ${coloredCount}`);

        if (coloredCount > 0) {
            console.log('✅ Los colores ya están poblados. No es necesario poblar de nuevo.');
            return;
        }

        console.log('🎨 Poblando colores en SystemStatuses...');

        // Poblar colores para SearchHireStatus
        const searchHireColors = [
            { statusValue: 'pending', color: '#FFA500', displayName: 'Pendiente', description: 'El servicio está pendiente de procesamiento' },
            { statusValue: 'active', color: '#17A2B8', displayName: 'Activo', description: 'El servicio está activo y en progreso' },
            { statusValue: 'completed', color: '#28A745', displayName: 'Completado', description: 'El servicio ha sido completado exitosamente' },
            { statusValue: 'cancelled', color: '#DC3545', displayName: 'Cancelado', description: 'El servicio ha sido cancelado' },
            { statusValue: 'dispute_resolved_client', color: '#17A2B8', displayName: 'Disputa Resuelta (Cliente)', description: 'La disputa ha sido resuelta a favor del cliente' },
            { statusValue: 'dispute_resolved_expert', color: '#6F42C1', displayName: 'Disputa Resuelta (Experto)', description: 'La disputa ha sido resuelta a favor del experto' },
            { statusValue: 'awaiting_client_decision', color: '#FFC107', displayName: 'Esperando Decisión del Cliente', description: 'Esperando que el cliente tome una decisión' },
            { statusValue: 'awaiting_expert_response', color: '#20C997', displayName: 'Esperando Respuesta del Experto', description: 'Esperando respuesta del experto' }
        ];

        for (const status of searchHireColors) {
            await client.query(`
                UPDATE "SystemStatuses" 
                SET "Color" = $1, "DisplayName" = $2, "Description" = $3
                WHERE "StatusValue" = $4 AND "StatusType" = 'SearchHireStatus'
            `, [status.color, status.displayName, status.description, status.statusValue]);
            
            console.log(`   ✅ ${status.statusValue}: ${status.color} - ${status.displayName}`);
        }

        // Poblar colores para AppointmentStatus
        const appointmentColors = [
            { statusValue: 'awaiting_appointment', color: '#FFC107', displayName: 'Esperando Cita', description: 'Esperando que se programe una cita' },
            { statusValue: 'appointment_proposed', color: '#6F42C1', displayName: 'Cita Propuesta', description: 'Se ha propuesto una fecha para la cita' },
            { statusValue: 'appointment_confirmed', color: '#20C997', displayName: 'Cita Confirmada', description: 'La cita ha sido confirmada por ambas partes' },
            { statusValue: 'appointment_rejected', color: '#FD7E14', displayName: 'Cita Rechazada', description: 'La cita propuesta ha sido rechazada' },
            { statusValue: 'appointment_completed', color: '#28A745', displayName: 'Cita Completada', description: 'La cita ha sido completada exitosamente' },
            { statusValue: 'appointment_cancelled', color: '#DC3545', displayName: 'Cita Cancelada', description: 'La cita ha sido cancelada' },
            { statusValue: 'appointment_report_sent', color: '#6610F2', displayName: 'Informe Enviado', description: 'El experto ha enviado el reporte de la cita' },
            { statusValue: 'expert_report_timeout', color: '#E83E8C', displayName: 'Timeout del Experto', description: 'El experto no envió el reporte a tiempo' }
        ];

        for (const status of appointmentColors) {
            await client.query(`
                UPDATE "SystemStatuses" 
                SET "Color" = $1, "DisplayName" = $2, "Description" = $3
                WHERE "StatusValue" = $4 AND "StatusType" = 'AppointmentStatus'
            `, [status.color, status.displayName, status.description, status.statusValue]);
            
            console.log(`   ✅ ${status.statusValue}: ${status.color} - ${status.displayName}`);
        }

        // Verificar el resultado final
        const finalResult = await client.query(`
            SELECT COUNT(*) as count 
            FROM "SystemStatuses" 
            WHERE "Color" IS NOT NULL AND "Color" != ''
        `);
        
        const finalColoredCount = parseInt(finalResult.rows[0].count);
        console.log(`\n🎉 Población completada! Estados con color: ${finalColoredCount}`);

        // Mostrar resumen de colores
        const summaryResult = await client.query(`
            SELECT "StatusType", "StatusValue", "DisplayName", "Color"
            FROM "SystemStatuses" 
            WHERE "Color" IS NOT NULL AND "Color" != ''
            ORDER BY "StatusType", "SortOrder"
        `);

        console.log('\n📋 RESUMEN DE COLORES POBLADOS:');
        let currentType = '';
        summaryResult.rows.forEach(row => {
            if (row.StatusType !== currentType) {
                currentType = row.StatusType;
                console.log(`\n${currentType}:`);
            }
            console.log(`   ${row.Color} - ${row.DisplayName} (${row.StatusValue})`);
        });

    } catch (error) {
        console.error('❌ Error:', error.message);
    } finally {
        await client.end();
        console.log('\n🔌 Desconectado de PostgreSQL');
    }
}

// Ejecutar el script
populateColorsIfNeeded();
