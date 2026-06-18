using System.Text.Json;

namespace newApi.Services
{
    /// <summary>
    /// Validación de servidor del JSON de personalización del informe.
    /// Refleja las reglas del cliente (inspectionTemplateConfig.ts): no se pueden
    /// desactivar puntos obligatorios (1, 2, 4) ni la sección A.
    /// </summary>
    public static class InspectionTemplateConfigValidator
    {
        private static readonly HashSet<int> RequiredPoints = new() { 1, 2, 4 };
        private static readonly HashSet<string> SectionsWithRequired = new() { "A" };
        private static readonly HashSet<string> ValidSections = new()
            { "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K" };

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

                if (root.TryGetProperty("disabledSections", out var ds) && ds.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in ds.EnumerateArray())
                    {
                        var id = s.GetString();
                        if (id != null && SectionsWithRequired.Contains(id))
                        {
                            error = $"La sección {id} no se puede desactivar (contiene puntos obligatorios).";
                            return false;
                        }
                    }
                }

                if (root.TryGetProperty("disabledPoints", out var dp) && dp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in dp.EnumerateArray())
                    {
                        if (p.TryGetInt32(out var n) && RequiredPoints.Contains(n))
                        {
                            error = $"El punto {n} es obligatorio y no se puede quitar.";
                            return false;
                        }
                    }
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
                        if (sec == null || !ValidSections.Contains(sec))
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
