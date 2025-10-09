const { Client } = require('pg');

// Configuración de conexión a tu base de datos
const client = new Client({
  host: '185.166.39.4',
  port: 30000,
  database: 'atrapo',
  user: 'admin',
  password: 'Pedrohabo1//',
  ssl: false // Cambia a true si necesitas SSL
});

async function cleanHangfire() {
  try {
    console.log('🔌 Conectando a la base de datos...');
    await client.connect();
    console.log('✅ Conectado exitosamente');

    // 1. Ver qué tablas existen en hangfire
    console.log('\n🔍 Verificando tablas en esquema hangfire...');
    const tables = await client.query(`
      SELECT table_name 
      FROM information_schema.tables 
      WHERE table_schema = 'hangfire'
      ORDER BY table_name
    `);
    
    console.log('📋 Tablas encontradas en hangfire:');
    tables.rows.forEach(table => {
      console.log(`  - ${table.table_name}`);
    });

    // 2. Ver estructura de la tabla job
    console.log('\n🔍 Verificando estructura de tabla job...');
    const jobColumns = await client.query(`
      SELECT column_name, data_type 
      FROM information_schema.columns 
      WHERE table_schema = 'hangfire' AND table_name = 'job'
      ORDER BY ordinal_position
    `);
    
    console.log('📋 Columnas de tabla job:');
    jobColumns.rows.forEach(col => {
      console.log(`  - ${col.column_name} (${col.data_type})`);
    });

    // 3. Ver qué jobs problemáticos existen
    console.log('\n🔍 Verificando jobs problemáticos...');
    const problemJobs = await client.query(`
      SELECT id, invocationdata::text as invocationdata_text 
      FROM hangfire.job 
      WHERE invocationdata::text LIKE '%HangfireJobService%'
    `);
    
    console.log(`📊 Jobs problemáticos encontrados: ${problemJobs.rows.length}`);
    if (problemJobs.rows.length > 0) {
      problemJobs.rows.forEach(job => {
        console.log(`  - Job ID: ${job.id}`);
      });
    }

    // 4. Ver estructura de la tabla set
    console.log('\n🔍 Verificando estructura de tabla set...');
    const setColumns = await client.query(`
      SELECT column_name, data_type 
      FROM information_schema.columns 
      WHERE table_schema = 'hangfire' AND table_name = 'set'
      ORDER BY ordinal_position
    `);
    
    console.log('📋 Columnas de tabla set:');
    setColumns.rows.forEach(col => {
      console.log(`  - ${col.column_name} (${col.data_type})`);
    });

    // 5. Ver qué recurring jobs problemáticos existen
    console.log('\n🔍 Verificando recurring jobs problemáticos...');
    const problemRecurring = await client.query(`
      SELECT key, value 
      FROM hangfire.set 
      WHERE value LIKE '%HangfireJobService%'
    `);
    
    console.log(`📊 Recurring jobs problemáticos encontrados: ${problemRecurring.rows.length}`);
    if (problemRecurring.rows.length > 0) {
      problemRecurring.rows.forEach(job => {
        console.log(`  - Key: ${job.key}`);
      });
    }

    // 3. Limpiar jobs problemáticos
    if (problemJobs.rows.length > 0) {
      console.log('\n🧹 Limpiando jobs problemáticos...');
      const deleteJobs = await client.query(`
        DELETE FROM hangfire."Job" 
        WHERE "InvocationData"::text LIKE '%HangfireJobService%'
      `);
      console.log(`✅ Eliminados ${deleteJobs.rowCount} jobs problemáticos`);
    }

    // 4. Limpiar recurring jobs problemáticos
    if (problemRecurring.rows.length > 0) {
      console.log('\n🧹 Limpiando recurring jobs problemáticos...');
      const deleteRecurring = await client.query(`
        DELETE FROM hangfire."Set" 
        WHERE "Value" LIKE '%HangfireJobService%'
      `);
      console.log(`✅ Eliminados ${deleteRecurring.rowCount} recurring jobs problemáticos`);
    }

    // 6. Verificar jobs restantes
    console.log('\n📊 Verificando jobs restantes...');
    const remainingJobs = await client.query('SELECT COUNT(*) as total FROM hangfire.job');
    const remainingRecurring = await client.query(`
      SELECT COUNT(*) as total FROM hangfire.set WHERE key LIKE '%recurring%'
    `);
    
    console.log(`📈 Jobs restantes: ${remainingJobs.rows[0].total}`);
    console.log(`📈 Recurring jobs restantes: ${remainingRecurring.rows[0].total}`);

    // 7. Mostrar jobs actuales
    console.log('\n📋 Jobs actuales en Hangfire:');
    const currentJobs = await client.query(`
      SELECT key, value 
      FROM hangfire.set 
      WHERE key LIKE '%recurring%'
      ORDER BY key
    `);
    
    currentJobs.rows.forEach(job => {
      console.log(`  - ${job.key}`);
    });

    console.log('\n🎉 ¡Limpieza completada exitosamente!');
    console.log('💡 Ahora reinicia tu aplicación y ve a http://localhost:7124/hangfire');

  } catch (error) {
    console.error('❌ Error:', error.message);
  } finally {
    await client.end();
    console.log('🔌 Conexión cerrada');
  }
}

// Ejecutar la limpieza
cleanHangfire();
