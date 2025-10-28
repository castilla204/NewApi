// Script de prueba para verificar que los endpoints devuelven SystemStatusDto con colores
// Archivo: test_endpoints_with_colors.js

const axios = require('axios');

const BASE_URL = 'http://localhost:7124';

// Headers para desarrollo
const DEV_HEADERS = {
    'X-Development-Mode': 'true',
    'Content-Type': 'application/json'
};

async function testEndpoints() {
    console.log('🧪 Probando endpoints con SystemStatusDto y colores...\n');

    try {
        // 1. Probar endpoint SearchHire/expert
        console.log('1️⃣ Probando GET /api/SearchHire/expert');
        const expertResponse = await axios.get(`${BASE_URL}/api/SearchHire/expert`, {
            headers: DEV_HEADERS
        });
        
        console.log('✅ SearchHire/expert - Status:', expertResponse.status);
        if (expertResponse.data && expertResponse.data.length > 0) {
            const firstHire = expertResponse.data[0];
            console.log('📊 Primer SearchHire:');
            console.log('   - ID:', firstHire.id);
            console.log('   - Status:', firstHire.status);
            console.log('   - StatusTranslated:', firstHire.statusTranslated);
            
            if (firstHire.statusInfo) {
                console.log('   - StatusInfo:');
                console.log('     * DisplayName:', firstHire.statusInfo.displayName);
                console.log('     * Description:', firstHire.statusInfo.description);
                console.log('     * Color:', firstHire.statusInfo.color);
                console.log('     * IsFinalizationStatus:', firstHire.statusInfo.isFinalizationStatus);
            } else {
                console.log('   ❌ StatusInfo: NO ENCONTRADO');
            }
        }
        console.log('');

        // 2. Probar endpoint Search con paginación
        console.log('2️⃣ Probando GET /api/Search?page=1&pageSize=20&sortBy=createdAt&sortDirection=desc');
        const searchResponse = await axios.get(`${BASE_URL}/api/Search?page=1&pageSize=20&sortBy=createdAt&sortDirection=desc`, {
            headers: DEV_HEADERS
        });
        
        console.log('✅ Search - Status:', searchResponse.status);
        if (searchResponse.data && searchResponse.data.searches && searchResponse.data.searches.length > 0) {
            const firstSearch = searchResponse.data.searches[0];
            console.log('📊 Primera Search:');
            console.log('   - ID:', firstSearch.id);
            console.log('   - Title:', firstSearch.title);
            
            if (firstSearch.searchHire) {
                console.log('   - SearchHire Status:', firstSearch.searchHire.status);
                console.log('   - SearchHire StatusTranslated:', firstSearch.searchHire.statusTranslated);
                
                if (firstSearch.searchHire.statusInfo) {
                    console.log('   - SearchHire StatusInfo:');
                    console.log('     * DisplayName:', firstSearch.searchHire.statusInfo.displayName);
                    console.log('     * Description:', firstSearch.searchHire.statusInfo.description);
                    console.log('     * Color:', firstSearch.searchHire.statusInfo.color);
                    console.log('     * IsFinalizationStatus:', firstSearch.searchHire.statusInfo.isFinalizationStatus);
                } else {
                    console.log('   ❌ SearchHire StatusInfo: NO ENCONTRADO');
                }
            } else {
                console.log('   - SearchHire: NO ENCONTRADO');
            }
        }
        console.log('');

        // 3. Probar endpoint details-complete (ya sabemos que funciona)
        console.log('3️⃣ Probando GET /api/Search/243/details-complete');
        const detailsResponse = await axios.get(`${BASE_URL}/api/Search/243/details-complete`, {
            headers: DEV_HEADERS
        });
        
        console.log('✅ Search/243/details-complete - Status:', detailsResponse.status);
        if (detailsResponse.data && detailsResponse.data.search && detailsResponse.data.search.searchHire) {
            const searchHire = detailsResponse.data.search.searchHire;
            console.log('📊 SearchHire en details-complete:');
            console.log('   - Status:', searchHire.status);
            console.log('   - StatusTranslated:', searchHire.statusTranslated);
            
            if (searchHire.statusInfo) {
                console.log('   - StatusInfo:');
                console.log('     * DisplayName:', searchHire.statusInfo.displayName);
                console.log('     * Description:', searchHire.statusInfo.description);
                console.log('     * Color:', searchHire.statusInfo.color);
                console.log('     * IsFinalizationStatus:', searchHire.statusInfo.isFinalizationStatus);
            } else {
                console.log('   ❌ StatusInfo: NO ENCONTRADO');
            }
        }
        console.log('');

        // 4. Resumen de colores encontrados
        console.log('🎨 RESUMEN DE COLORES ENCONTRADOS:');
        const allColors = new Set();
        
        // Buscar colores en SearchHire/expert
        if (expertResponse.data && expertResponse.data.length > 0) {
            expertResponse.data.forEach(hire => {
                if (hire.statusInfo && hire.statusInfo.color) {
                    allColors.add(`${hire.statusInfo.color} (${hire.statusInfo.displayName})`);
                }
            });
        }
        
        // Buscar colores en Search
        if (searchResponse.data && searchResponse.data.searches && searchResponse.data.searches.length > 0) {
            searchResponse.data.searches.forEach(search => {
                if (search.searchHire && search.searchHire.statusInfo && search.searchHire.statusInfo.color) {
                    allColors.add(`${search.searchHire.statusInfo.color} (${search.searchHire.statusInfo.displayName})`);
                }
            });
        }
        
        if (allColors.size > 0) {
            console.log('   Colores únicos encontrados:');
            Array.from(allColors).forEach(color => console.log(`   - ${color}`));
        } else {
            console.log('   ❌ No se encontraron colores en los endpoints');
        }

        console.log('\n✅ Pruebas completadas exitosamente!');
        
    } catch (error) {
        console.error('❌ Error en las pruebas:', error.message);
        if (error.response) {
            console.error('   Status:', error.response.status);
            console.error('   Data:', error.response.data);
        }
    }
}

// Ejecutar las pruebas
testEndpoints();
