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

            JsonElement root;
            try { root = JsonDocument.Parse(json).RootElement; }
            catch { error = "Configuración del informe con formato inválido."; return false; }

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
                foreach (var c in cp.EnumerateArray())
                {
                    var sec = c.TryGetProperty("section", out var se) ? se.GetString() : null;
                    if (sec == null || !ValidSections.Contains(sec))
                    {
                        error = "Pregunta propia con sección inválida."; return false;
                    }
                }
            }

            return true;
        }
    }
}
