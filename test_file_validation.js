const { Client } = require('pg');

const client = new Client({
  host: '185.166.39.4',
  port: 30000,
  database: 'atrapo',
  user: 'admin',
  password: '__REDACTED_CREDENTIAL__'
});

async function testFileValidation() {
  try {
    await client.connect();
    
    // 1. Buscar un SearchHire con entregables requeridos
    const searchHiresResult = await client.query(`
      SELECT 
        sh."Id" as searchhire_id,
        sh."Status" as searchhire_status,
        ss."Id" as service_id,
        ss."Price",
        COUNT(ssdt."Id") as required_deliverables_count,
        STRING_AGG(dt."Name", ', ') as required_types
      FROM "SearchHires" sh
      JOIN "SearchServices" ss ON sh."SearchServiceId" = ss."Id"
      LEFT JOIN "SearchServiceDeliverableTypes" ssdt ON ss."Id" = ssdt."SearchServiceId" AND ssdt."IsSelected" = true
      LEFT JOIN "DeliverableTypes" dt ON ssdt."DeliverableTypeId" = dt."Id"
      WHERE sh."Status" = 'awaiting_client_decision'
      GROUP BY sh."Id", sh."Status", ss."Id", ss."Price"
      HAVING COUNT(ssdt."Id") > 0
      LIMIT 3
    `);
    
    searchHiresResult.rows.forEach(row => {
    });
    
    // 2. Verificar entregables subidos para cada SearchHire
    if (searchHiresResult.rows.length > 0) {
      
      for (const searchHire of searchHiresResult.rows) {
        const deliverablesResult = await client.query(`
          SELECT 
            shd."Id" as deliverable_id,
            shd."Type" as file_type,
            shd."Url",
            shd."CreatedAt"
          FROM "SearchHireDeliverables" shd
          WHERE shd."SearchHireId" = $1
          ORDER BY shd."CreatedAt" DESC
        `, [searchHire.searchhire_id]);
        
        if (deliverablesResult.rows.length > 0) {
          deliverablesResult.rows.forEach(deliverable => {
          });
        } else {
        }
      }
    }
    
    // 3. Buscar citas en awaiting_report para probar validación
    const appointmentsResult = await client.query(`
      SELECT 
        a."Id" as appointment_id,
        a."SearchHireId",
        a."ProposedDate",
        a."ProposedTime",
        s."StatusValue" as current_status,
        sh."Status" as searchhire_status
      FROM "Appointments" a
      JOIN "SystemStatuses" s ON a."StatusId" = s."Id"
      JOIN "SearchHires" sh ON a."SearchHireId" = sh."Id"
      WHERE s."StatusValue" = 'appointment_awaiting_report'
      LIMIT 3
    `);
    
    appointmentsResult.rows.forEach(row => {
    });
    
    // 4. Estadísticas generales
    const statsResult = await client.query(`
      SELECT 
        'SearchHires con entregables requeridos' as metric,
        COUNT(DISTINCT sh."Id") as count
      FROM "SearchHires" sh
      JOIN "SearchServices" ss ON sh."SearchServiceId" = ss."Id"
      JOIN "SearchServiceDeliverableTypes" ssdt ON ss."Id" = ssdt."SearchServiceId" AND ssdt."IsSelected" = true
      WHERE sh."Status" = 'awaiting_client_decision'
      
      UNION ALL
      
      SELECT 
        'Entregables subidos total' as metric,
        COUNT(*) as count
      FROM "SearchHireDeliverables"
      
      UNION ALL
      
      SELECT 
        'Citas en awaiting_report' as metric,
        COUNT(*) as count
      FROM "Appointments" a
      JOIN "SystemStatuses" s ON a."StatusId" = s."Id"
      WHERE s."StatusValue" = 'appointment_awaiting_report'
    `);
    
    statsResult.rows.forEach(row => {
    });
    
    // 5. Ejemplo de validación manual
    if (searchHiresResult.rows.length > 0) {
      const testSearchHire = searchHiresResult.rows[0];
      
      // Verificar tipos requeridos
      const requiredTypesResult = await client.query(`
        SELECT dt."Name" as type_name
        FROM "SearchServiceDeliverableTypes" ssdt
        JOIN "DeliverableTypes" dt ON ssdt."DeliverableTypeId" = dt."Id"
        WHERE ssdt."SearchServiceId" = (
          SELECT "SearchServiceId" FROM "SearchHires" WHERE "Id" = $1
        ) AND ssdt."IsSelected" = true
      `, [testSearchHire.searchhire_id]);
      
      // Verificar archivos subidos
      const uploadedFilesResult = await client.query(`
        SELECT "Type" as file_type
        FROM "SearchHireDeliverables"
        WHERE "SearchHireId" = $1
      `, [testSearchHire.searchhire_id]);
      
      const requiredTypes = requiredTypesResult.rows.map(r => r.type_name.toLowerCase());
      const uploadedTypes = uploadedFilesResult.rows.map(r => r.file_type);
      
      
      const missingTypes = requiredTypes.filter(type => !uploadedTypes.includes(type));
      if (missingTypes.length > 0) {
      } else {
      }
    }
    
  } catch (err) {
    console.error('❌ Error:', err.message);
  } finally {
    await client.end();
  }
}

testFileValidation();
















