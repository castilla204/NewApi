using System;
using System.Collections.Generic;
using System.Linq;

namespace newApi.Services
{
    /// <summary>
    /// 🛡️ SEC-MAJ-7 FIX: registry de emails de administradores leído desde configuración.
    ///
    /// Antes el email <c>dcastillaa@gmail.com</c> estaba hardcodeado en 4 sitios de
    /// <see cref="UserService"/> (líneas 95/139/361/370) — auto-promoción a Admin en
    /// cualquier login Google que matchease ese literal + protección contra
    /// BlockUser/DeleteUser. Mover a configuración permite:
    ///   - rotar admin sin redeploy
    ///   - tener varios admins en CSV
    ///   - eliminar PII personal del código fuente versionado
    ///
    /// La fuente es el setting "AdminEmails" (separados por coma) o la variable de
    /// entorno ADMIN_EMAILS. Si ninguno está definido, el set queda vacío y NADIE será
    /// promovido a Admin en logins Google nuevos — prereq operativo: setear la env var
    /// en Render ANTES de desplegar este código.
    /// </summary>
    public interface IAdminEmailRegistry
    {
        bool IsAdmin(string? email);
    }

    public sealed class AdminEmailRegistry : IAdminEmailRegistry
    {
        private readonly IReadOnlySet<string> _emails;

        public AdminEmailRegistry(IReadOnlySet<string> emails)
        {
            _emails = emails ?? throw new ArgumentNullException(nameof(emails));
        }

        public bool IsAdmin(string? email)
        {
            if (string.IsNullOrWhiteSpace(email)) return false;
            return _emails.Contains(email.Trim().ToLowerInvariant());
        }

        /// <summary>
        /// Helper de bootstrap: parsea un CSV (con espacios opcionales) a un set lowercase.
        /// </summary>
        public static IReadOnlySet<string> ParseCsv(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return new HashSet<string>(
                csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                   .Select(e => e.ToLowerInvariant()),
                StringComparer.OrdinalIgnoreCase);
        }
    }
}
