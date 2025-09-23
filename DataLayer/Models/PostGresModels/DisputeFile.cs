namespace newApi.DataLayer.Models.PostGresModels
{
    /// <summary>
    /// Modelo para archivos adjuntos en disputas
    /// </summary>
    public class DisputeFile
    {
        /// <summary>
        /// Identificador único del archivo
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Clave foránea a la disputa asociada
        /// </summary>
        public int DisputeId { get; set; }

        /// <summary>
        /// Nombre original del archivo
        /// </summary>
        public string FileName { get; set; } = string.Empty;

        /// <summary>
        /// URL del archivo en Google Cloud Storage
        /// </summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>
        /// Tipo de archivo (extensión)
        /// </summary>
        public string FileType { get; set; } = string.Empty;

        /// <summary>
        /// Tamaño del archivo en bytes
        /// </summary>
        public long FileSize { get; set; }

        /// <summary>
        /// Fecha de creación del archivo
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Relación con la disputa
        /// </summary>
        public virtual Dispute Dispute { get; set; }
    }
}
