using System.Text.Json;

namespace newApi.Services
{
    /// <summary>
    /// Validación de servidor (estructural) del JSON de personalización del informe.
    /// Las reglas específicas por categoría (qué puntos son obligatorios, qué
    /// secciones existen) las aplica el cliente con el catálogo de cada categoría
    /// (inspectionTemplateConfig.ts / inspectionCatalog.ts). Aquí solo validamos que
    /// el JSON tenga forma válida y que las preguntas propias estén acotadas.
    /// </summary>
    public static class InspectionTemplateConfigValidator
    {
        public static bool IsValid(string? json, out string error)
        {
            error = "";
            if (string.IsNullOrWhiteSpace(json)) return true;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(json); }
            catch { error = "Configuración del informe con formato inválido."; return false; }

            using (doc)
            {
                var root = doc.RootElement;

                if (root.ValueKind != JsonValueKind.Object)
                {
                    error = "Configuración del informe inválida."; return false;
                }

                if (root.TryGetProperty("customPoints", out var cp) && cp.ValueKind == JsonValueKind.Array)
                {
                    int customCount = 0;
                    foreach (var c in cp.EnumerateArray())
                    {
                        customCount++;
                        if (customCount > 50)
                        {
                            error = "No se pueden añadir más de 50 preguntas propias."; return false;
                        }

                        var sec = c.TryGetProperty("section", out var se) ? se.GetString() : null;
                        if (string.IsNullOrWhiteSpace(sec))
                        {
                            error = "Pregunta propia con sección inválida."; return false;
                        }

                        var lbl = c.TryGetProperty("label", out var lblProp) ? lblProp.GetString() : null;
                        if (lbl != null && lbl.Length > 200)
                        {
                            error = "Una pregunta propia no puede superar los 200 caracteres."; return false;
                        }
                    }
                }

                return true;
            }
        }
    }
}
