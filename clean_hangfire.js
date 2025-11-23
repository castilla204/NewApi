const { Client } = require('pg');

// Configuración de conexión a tu base de datos
const client = new Client({
  host: '185.166.39.4',
  port: 30000,
  database: 'atrapo',
  user: 'admin',
  password: process.env.POSTGRES_PASSWORD || process.env.PGPASSWORD || (() => { console.error('ERROR: POSTGRES_PASSWORD or PGPASSWORD not set'); process.exit(1); })(),
  ssl: false // Cambia a true si necesitas SSL
});

async function cleanHangfire() {
  try {
    await client.connect();

    // 1. Ver qué tablas existen en hangfire
    const tables = await client.query(`
      SELECT table_name 
      FROM information_schema.tables 
      WHERE table_schema = 'hangfire'
      ORDER BY table_name
    `);
    
    tables.rows.forEach(table => {
    });

    // 2. Ver estructura de la tabla job
    const jobColumns = await client.query(`
      SELECT column_name, data_type 
      FROM information_schema.columns 
      WHERE table_schema = 'hangfire' AND table_name = 'job'
      ORDER BY ordinal_position
    `);
    
    jobColumns.rows.forEach(col => {
    });

    // 3. Ver qué jobs problemáticos existen
    const problemJobs = await client.query(`
      SELECT id, invocationdata::text as invocationdata_text 
      FROM hangfire.job 
      WHERE invocationdata::text LIKE '%HangfireJobService%'
    `);
    
    if (problemJobs.rows.length > 0) {
      problemJobs.rows.forEach(job => {
      });
    }

    // 4. Ver estructura de la tabla set
    const setColumns = await client.query(`
      SELECT column_name, data_type 
      FROM information_schema.columns 
      WHERE table_schema = 'hangfire' AND table_name = 'set'
      ORDER BY ordinal_position
    `);
    
    setColumns.rows.forEach(col => {
    });

    // 5. Ver qué recurring jobs problemáticos existen
    const problemRecurring = await client.query(`
      SELECT key, value 
      FROM hangfire.set 
      WHERE value LIKE '%HangfireJobService%'
    `);
    
    if (problemRecurring.rows.length > 0) {
      problemRecurring.rows.forEach(job => {
      });
    }

    // 3. Limpiar jobs problemáticos
    if (problemJobs.rows.length > 0) {
      const deleteJobs = await client.query(`
        DELETE FROM hangfire."Job" 
        WHERE "InvocationData"::text LIKE '%HangfireJobService%'
      `);
    }

    // 4. Limpiar recurring jobs problemáticos
    if (problemRecurring.rows.length > 0) {
      const deleteRecurring = await client.query(`
        DELETE FROM hangfire."Set" 
        WHERE "Value" LIKE '%HangfireJobService%'
      `);
    }

    // 6. Verificar jobs restantes
    const remainingJobs = await client.query('SELECT COUNT(*) as total FROM hangfire.job');
    const remainingRecurring = await client.query(`
      SELECT COUNT(*) as total FROM hangfire.set WHERE key LIKE '%recurring%'
    `);
    

    // 7. Mostrar jobs actuales
    const currentJobs = await client.query(`
      SELECT key, value 
      FROM hangfire.set 
      WHERE key LIKE '%recurring%'
      ORDER BY key
    `);
    
    currentJobs.rows.forEach(job => {
    });


  } catch (error) {
    console.error('❌ Error:', error.message);
  } finally {
    await client.end();
  }
}

// Ejecutar la limpieza
cleanHangfire();
