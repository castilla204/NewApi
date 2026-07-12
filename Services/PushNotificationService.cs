using FirebaseAdmin.Messaging;

namespace newApi.Services
{
    /// <summary>
    /// Clasifica qué errores de FCM implican que el token es DEFINITIVAMENTE inválido
    /// y debe borrarse (best practice de Google: borrar en Unregistered / InvalidArgument,
    /// ya que controlamos el payload). Errores transitorios (Unavailable, Internal,
    /// QuotaExceeded) NO borran: el token puede volver a funcionar.
    /// </summary>
    public static class DeadTokenClassifier
    {
        public static bool ShouldDelete(MessagingErrorCode? code) =>
            code == MessagingErrorCode.Unregistered || code == MessagingErrorCode.InvalidArgument;
    }
}
