// Script Node.js para buscar y eliminar Console.* de archivos .cs
// Uso: node Scripts/remove-console-logs.js [--dry-run] [--auto]

const fs = require('fs');
const path = require('path');

const args = process.argv.slice(2);
const isDryRun = args.includes('--dry-run');
const isAuto = args.includes('--auto');

console.log('🔍 Buscando todos los Console.* en archivos .cs...\n');

// Función recursiva para buscar archivos .cs
function findCsFiles(dir, fileList = []) {
    const files = fs.readdirSync(dir);
    
    files.forEach(file => {
        const filePath = path.join(dir, file);
        const stat = fs.statSync(filePath);
        
        // Ignorar bin, obj, node_modules, .git
        if (file === 'bin' || file === 'obj' || file === 'node_modules' || file === '.git') {
            return;
        }
        
        if (stat.isDirectory()) {
            findCsFiles(filePath, fileList);
        } else if (file.endsWith('.cs')) {
            fileList.push(filePath);
        }
    });
    
    return fileList;
}

// Buscar archivos
const csFiles = findCsFiles('.');

const consolePatterns = [
    /Console\.WriteLine\s*\([^)]*\);\s*/g,
    /Console\.Write\s*\([^)]*\);\s*/g,
    /Console\.Error\s*\([^)]*\);\s*/g,
    /Console\.Out\s*\([^)]*\);\s*/g,
];

let totalFound = 0;
const filesWithConsole = [];

csFiles.forEach(filePath => {
    try {
        const content = fs.readFileSync(filePath, 'utf8');
        let hasConsole = false;
        let matches = [];
        
        consolePatterns.forEach(pattern => {
            const fileMatches = content.match(pattern);
            if (fileMatches) {
                hasConsole = true;
                matches = matches.concat(fileMatches);
            }
        });
        
        if (hasConsole) {
            totalFound += matches.length;
            filesWithConsole.push({ path: filePath, matches: matches.length, content });
            
            console.log(`📄 ${filePath}`);
            console.log(`   ⚠️  Encontrados ${matches.length} Console.*`);
            
            // Mostrar líneas
            const lines = content.split('\n');
            lines.forEach((line, index) => {
                if (line.match(/Console\.(WriteLine|Write|Error|Out)/)) {
                    console.log(`   Línea ${index + 1}: ${line.trim()}`);
                }
            });
            console.log('');
        }
    } catch (error) {
        console.error(`❌ Error leyendo ${filePath}: ${error.message}`);
    }
});

console.log(`\n📊 Resumen:`);
console.log(`   Archivos con Console.*: ${filesWithConsole.length}`);
console.log(`   Total Console.* encontrados: ${totalFound}`);

if (!isDryRun && isAuto && filesWithConsole.length > 0) {
    console.log('\n🗑️  Eliminando Console.* automáticamente...\n');
    
    filesWithConsole.forEach(({ path: filePath, content }) => {
        let newContent = content;
        
        consolePatterns.forEach(pattern => {
            newContent = newContent.replace(pattern, '');
        });
        
        // Limpiar líneas vacías múltiples (opcional)
        newContent = newContent.replace(/\n\s*\n\s*\n/g, '\n\n');
        
        try {
            fs.writeFileSync(filePath, newContent, 'utf8');
            console.log(`✅ Modificado: ${filePath}`);
        } catch (error) {
            console.error(`❌ Error escribiendo ${filePath}: ${error.message}`);
        }
    });
    
    console.log('\n✅ Proceso completado!');
} else if (isDryRun) {
    console.log('\n💡 Modo dry-run: No se modificaron archivos');
    console.log('   Ejecuta con --auto para aplicar cambios');
}


