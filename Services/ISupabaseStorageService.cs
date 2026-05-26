using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace newApi.Services
{
    /// <summary>
    /// Almacenamiento de multimedia en Supabase Storage (sustituye a Google Cloud Storage).
    /// Sube como servidor con la service_role key (bypassa RLS).
    /// - Bucket público (ImagesBucket): fotos de servicio/perfil/reseñas → URL pública permanente.
    /// - Bucket privado (FilesBucket): disputas/entregables/adjuntos de chat → URLs firmadas temporales.
    /// </summary>
    public interface ISupabaseStorageService
    {
        /// <summary>Nombre del bucket público para imágenes.</summary>
        string ImagesBucket { get; }

        /// <summary>Nombre del bucket privado para ficheros sensibles.</summary>
        string FilesBucket { get; }

        /// <summary>True si hay URL y service_role configuradas.</summary>
        bool IsConfigured { get; }

        /// <summary>Sube un fichero. Devuelve el objectPath guardado (ruta dentro del bucket).</summary>
        Task<string> UploadAsync(string bucket, string objectPath, Stream content, string contentType, CancellationToken ct = default);

        /// <summary>URL pública permanente (solo válida si el bucket es público).</summary>
        string GetPublicUrl(string bucket, string objectPath);

        /// <summary>URL firmada temporal (para buckets privados). null si falla.</summary>
        Task<string?> CreateSignedUrlAsync(string bucket, string objectPath, TimeSpan expiry, CancellationToken ct = default);

        /// <summary>Borra un objeto. true si se borró correctamente.</summary>
        Task<bool> DeleteAsync(string bucket, string objectPath, CancellationToken ct = default);
    }
}
