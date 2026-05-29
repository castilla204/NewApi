using Microsoft.EntityFrameworkCore;
using OtpNet;
using QRCoder;
using System.Security.Cryptography;
using System.Text;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;
using newApi.DataLayer.Models.DTOs;

namespace newApi.Services
{
    /// <summary>
    /// ✅ SEGURIDAD 2025: Servicio de autenticación multifactor (MFA)
    /// Implementa TOTP (Time-based One-Time Password) compatible con Google Authenticator
    /// </summary>
    public class MfaService
    {
        private readonly AppDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly ILogger<MfaService> _logger;

        public MfaService(AppDbContext context, IConfiguration configuration, ILogger<MfaService> logger)
        {
            _context = context;
            _configuration = configuration;
            _logger = logger;
        }

        /// <summary>
        /// Genera un secreto TOTP nuevo para un usuario
        /// </summary>
        public async Task<(string secret, string qrCodeBase64, string manualEntryKey)> GenerateTotpSecretAsync(int userId)
        {
            var user = await _context.Users.FindAsync(userId);
            if (user == null)
                throw new InvalidOperationException("User not found");

            // Generar secreto de 160 bits (20 bytes) - TOTP estándar
            var key = KeyGeneration.GenerateRandomKey(20);
            var secret = Base32Encoding.ToString(key);

            // Generar URI para QR Code
            var appName = _configuration["App:Name"] ?? "YourApp";
            var issuer = Uri.EscapeDataString(appName);
            var label = Uri.EscapeDataString($"{appName}:{user.Email}");
            
            var totpUri = $"otpauth://totp/{label}?secret={secret}&issuer={issuer}&algorithm=SHA1&digits=6&period=30";

            // Generar QR Code
            var qrCodeBase64 = GenerateQrCode(totpUri);

            // Formato manual para entrada (grupos de 4)
            var manualEntryKey = FormatSecretForManualEntry(secret);

            return (secret, qrCodeBase64, manualEntryKey);
        }

        /// <summary>
        /// Genera un código QR en formato Base64
        /// </summary>
        private string GenerateQrCode(string totpUri)
        {
            using var qrGenerator = new QRCodeGenerator();
            using var qrCodeData = qrGenerator.CreateQrCode(totpUri, QRCodeGenerator.ECCLevel.Q);
            using var qrCode = new PngByteQRCode(qrCodeData);
            var qrCodeBytes = qrCode.GetGraphic(20); // 20 pixels por módulo

            return Convert.ToBase64String(qrCodeBytes);
        }

        /// <summary>
        /// Formatea el secreto para entrada manual (grupos de 4 caracteres)
        /// </summary>
        private string FormatSecretForManualEntry(string secret)
        {
            var formatted = new StringBuilder();
            for (int i = 0; i < secret.Length; i++)
            {
                if (i > 0 && i % 4 == 0)
                    formatted.Append(' ');
                formatted.Append(secret[i]);
            }
            return formatted.ToString();
        }

        /// <summary>
        /// Verifica un código TOTP
        /// </summary>
        public bool VerifyTotpCode(string secret, string code)
        {
            try
            {
                var key = Base32Encoding.ToBytes(secret);
                var totp = new Totp(key);

                // Verificar código actual y ventanas de tiempo adyacentes (±1 período = ±30 segundos)
                // Esto compensa pequeñas diferencias de reloj
                var now = DateTime.UtcNow;
                
                return totp.VerifyTotp(code, out _, new VerificationWindow(1, 1));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error verifying TOTP code");
                return false;
            }
        }

        /// <summary>
        /// Genera códigos de recuperación (10 códigos)
        /// </summary>
        public List<string> GenerateRecoveryCodes(int count = 10)
        {
            var codes = new List<string>();
            
            for (int i = 0; i < count; i++)
            {
                // Generar código de 8 caracteres alfanuméricos
                var code = GenerateRandomCode(8);
                codes.Add(FormatRecoveryCode(code));
            }

            return codes;
        }

        /// <summary>
        /// Genera un código aleatorio seguro
        /// </summary>
        private string GenerateRandomCode(int length)
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // Sin caracteres ambiguos (0,O,1,I)
            using var rng = RandomNumberGenerator.Create();
            var bytes = new byte[length];
            rng.GetBytes(bytes);

            var result = new char[length];
            for (int i = 0; i < length; i++)
            {
                result[i] = chars[bytes[i] % chars.Length];
            }

            return new string(result);
        }

        /// <summary>
        /// Formatea un código de recuperación (XXXX-XXXX)
        /// </summary>
        private string FormatRecoveryCode(string code)
        {
            if (code.Length == 8)
                return $"{code.Substring(0, 4)}-{code.Substring(4, 4)}";
            return code;
        }

        /// <summary>
        /// Cifra el secreto TOTP para almacenamiento
        /// </summary>
        public string EncryptSecret(string secret)
        {
            try
            {
                var key = Encoding.UTF8.GetBytes(GetEncryptionKey());
                using var aes = Aes.Create();
                aes.Key = key;
                aes.GenerateIV();

                using var encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
                using var msEncrypt = new MemoryStream();
                
                // Guardar IV al inicio
                msEncrypt.Write(aes.IV, 0, aes.IV.Length);
                
                using (var csEncrypt = new CryptoStream(msEncrypt, encryptor, CryptoStreamMode.Write))
                using (var swEncrypt = new StreamWriter(csEncrypt))
                {
                    swEncrypt.Write(secret);
                }

                return Convert.ToBase64String(msEncrypt.ToArray());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error encrypting MFA secret");
                throw;
            }
        }

        /// <summary>
        /// Descifra el secreto TOTP
        /// </summary>
        public string DecryptSecret(string encryptedSecret)
        {
            try
            {
                var key = Encoding.UTF8.GetBytes(GetEncryptionKey());
                var fullCipher = Convert.FromBase64String(encryptedSecret);

                using var aes = Aes.Create();
                aes.Key = key;

                // Extraer IV (primeros 16 bytes)
                var iv = new byte[16];
                Array.Copy(fullCipher, 0, iv, 0, iv.Length);
                aes.IV = iv;

                using var decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
                using var msDecrypt = new MemoryStream(fullCipher, iv.Length, fullCipher.Length - iv.Length);
                using var csDecrypt = new CryptoStream(msDecrypt, decryptor, CryptoStreamMode.Read);
                using var srDecrypt = new StreamReader(csDecrypt);
                
                return srDecrypt.ReadToEnd();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error decrypting MFA secret");
                throw;
            }
        }

        /// <summary>
        /// Cifra códigos de recuperación (JSON)
        /// </summary>
        public string EncryptRecoveryCodes(List<string> codes)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(codes);
            return EncryptSecret(json);
        }

        /// <summary>
        /// Descifra códigos de recuperación
        /// </summary>
        public List<string> DecryptRecoveryCodes(string encryptedCodes)
        {
            var json = DecryptSecret(encryptedCodes);
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }

        /// <summary>
        /// Obtiene la clave de cifrado desde configuración
        /// </summary>
        private string GetEncryptionKey()
        {
            var key = _configuration["Mfa:EncryptionKey"];
            
            if (string.IsNullOrEmpty(key))
            {
                throw new InvalidOperationException("MFA encryption key not configured. Add 'Mfa:EncryptionKey' to configuration.");
            }

            // Asegurar que la clave tenga 32 bytes (256 bits) para AES-256
            if (key.Length < 32)
            {
                using var sha256 = SHA256.Create();
                var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(key));
                return Convert.ToBase64String(hash).Substring(0, 32);
            }

            return key.Substring(0, 32);
        }

        /// <summary>
        /// ✅ FIX: Guarda el secreto TOTP temporalmente para setup (sin habilitar MFA)
        /// </summary>
        public async Task<(string secret, string qrCodeBase64, string manualEntryKey)> SaveTotpSecretForSetupAsync(int userId)
        {
            var user = await _context.Users.FindAsync(userId);
            if (user == null)
                throw new InvalidOperationException("User not found");

            // Verificar si ya tiene MFA habilitado
            var existingMfa = await _context.UserMfaSettings
                .FirstOrDefaultAsync(m => m.UserId == userId);

            if (existingMfa != null && existingMfa.IsEnabled)
                throw new InvalidOperationException("MFA is already enabled for this user");

            // Generar nuevo secreto TOTP
            var (secret, qrCodeBase64, manualEntryKey) = await GenerateTotpSecretAsync(userId);

            // Cifrar el secreto
            var encryptedSecret = EncryptSecret(secret);

            // Guardar temporalmente (IsEnabled = false)
            if (existingMfa == null)
            {
                existingMfa = new UserMfaSettings
                {
                    UserId = userId,
                    TotpSecret = encryptedSecret,
                    IsEnabled = false, // ✅ Aún NO habilitado
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                _context.UserMfaSettings.Add(existingMfa);
            }
            else
            {
                // Actualizar secreto si ya existe pero no está habilitado
                existingMfa.TotpSecret = encryptedSecret;
                existingMfa.UpdatedAt = DateTime.UtcNow;
            }

            await _context.SaveChangesAsync();

            _logger.LogInformation($"TOTP secret saved for user {userId} (not enabled yet)");

            return (secret, qrCodeBase64, manualEntryKey);
        }

        /// <summary>
        /// ✅ FIX: Habilita MFA para un usuario (valida código del secreto YA GUARDADO)
        /// </summary>
        public async Task<EnableMfaResponseDto> EnableMfaAsync(int userId, string totpCode)
        {
            var user = await _context.Users.FindAsync(userId);
            if (user == null)
                throw new InvalidOperationException("User not found");

            // Buscar configuración MFA existente
            var mfaSettings = await _context.UserMfaSettings
                .FirstOrDefaultAsync(m => m.UserId == userId);

            if (mfaSettings == null)
                throw new InvalidOperationException("MFA setup not initiated. Please call /mfa/setup first.");

            if (mfaSettings.IsEnabled)
                throw new InvalidOperationException("MFA is already enabled for this user");

            // ✅ FIX: Usar el secreto YA GUARDADO, NO generar uno nuevo
            var decryptedSecret = DecryptSecret(mfaSettings.TotpSecret);

            // Verificar que el código proporcionado sea correcto
            if (!VerifyTotpCode(decryptedSecret, totpCode))
            {
                _logger.LogWarning($"Invalid TOTP code for user {userId}");
                throw new InvalidOperationException("Invalid TOTP code. Please try again.");
            }

            // Generar códigos de recuperación
            var recoveryCodes = GenerateRecoveryCodes(10);
            var encryptedRecoveryCodes = EncryptRecoveryCodes(recoveryCodes);

            // Habilitar MFA y guardar recovery codes
            mfaSettings.RecoveryCodesEncrypted = encryptedRecoveryCodes;
            mfaSettings.IsEnabled = true; // ✅ Ahora sí habilitar
            mfaSettings.EnabledAt = DateTime.UtcNow;
            mfaSettings.UpdatedAt = DateTime.UtcNow;
            mfaSettings.RecoveryCodesUsed = 0;
            mfaSettings.FailedAttempts = 0;
            mfaSettings.LockedUntil = null;

            await _context.SaveChangesAsync();

            _logger.LogInformation($"MFA enabled successfully for user {userId}");

            // Generar QR code y manual key para la respuesta
            var (_, qrCodeBase64, manualEntryKey) = await GenerateTotpSecretFromExisting(decryptedSecret, user.Email);

            return new EnableMfaResponseDto
            {
                QrCodeBase64 = qrCodeBase64,
                ManualEntryKey = manualEntryKey,
                RecoveryCodes = recoveryCodes,
                Message = "MFA enabled successfully. Save your recovery codes in a safe place!"
            };
        }

        /// <summary>
        /// Genera QR code a partir de un secreto existente
        /// </summary>
        private async Task<(string secret, string qrCodeBase64, string manualEntryKey)> GenerateTotpSecretFromExisting(string secret, string userEmail)
        {
            // Generar URI para QR Code
            var appName = _configuration["App:Name"] ?? "YourApp";
            var issuer = Uri.EscapeDataString(appName);
            var label = Uri.EscapeDataString($"{appName}:{userEmail}");
            
            var totpUri = $"otpauth://totp/{label}?secret={secret}&issuer={issuer}&algorithm=SHA1&digits=6&period=30";

            // Generar QR Code
            var qrCodeBase64 = GenerateQrCode(totpUri);

            // Formato manual
            var manualEntryKey = FormatSecretForManualEntry(secret);

            return await Task.FromResult((secret, qrCodeBase64, manualEntryKey));
        }

        /// <summary>
        /// Verifica un código MFA (TOTP o código de recuperación)
        /// </summary>
        public async Task<(bool isValid, string? message)> VerifyMfaCodeAsync(int userId, string code, bool isRecoveryCode = false)
        {
            var mfaSettings = await _context.UserMfaSettings
                .FirstOrDefaultAsync(m => m.UserId == userId && m.IsEnabled);

            if (mfaSettings == null)
                return (false, "MFA is not enabled for this user");

            // Verificar si está bloqueado
            if (mfaSettings.IsLocked)
                return (false, $"Account is locked due to too many failed attempts. Try again after {mfaSettings.LockedUntil:yyyy-MM-dd HH:mm:ss} UTC");

            if (isRecoveryCode)
            {
                // Verificar código de recuperación
                var recoveryCodes = DecryptRecoveryCodes(mfaSettings.RecoveryCodesEncrypted ?? "");
                var normalizedCode = code.Replace("-", "").Replace(" ", "").ToUpperInvariant();
                
                if (recoveryCodes.Any(rc => rc.Replace("-", "").Equals(normalizedCode, StringComparison.OrdinalIgnoreCase)))
                {
                    // Remover código usado
                    recoveryCodes.RemoveAll(rc => rc.Replace("-", "").Equals(normalizedCode, StringComparison.OrdinalIgnoreCase));
                    mfaSettings.RecoveryCodesEncrypted = EncryptRecoveryCodes(recoveryCodes);
                    mfaSettings.RecoveryCodesUsed++;
                    mfaSettings.LastVerifiedAt = DateTime.UtcNow;
                    mfaSettings.FailedAttempts = 0;
                    await _context.SaveChangesAsync();

                    var remaining = recoveryCodes.Count;
                    return (true, remaining <= 3 ? $"Recovery code accepted. Warning: Only {remaining} recovery codes remaining!" : null);
                }
            }
            else
            {
                // 🛡️ T2 FIX (parte 1): validar TotpSecret no vacío ANTES de aceptar TOTP.
                // Si por alguna razón TotpSecret se corrompió/borró pero IsEnabled sigue true,
                // VerifyTotpCode con secret vacío puede devolver false constante (rate-limit OK)
                // PERO si VerifyTotpCode tiene algún edge case que devuelva true con secret
                // vacío (ej: librería que trata empty como wildcard), sería bypass. Defensa.
                if (string.IsNullOrWhiteSpace(mfaSettings.TotpSecret))
                {
                    return (false, "MFA TOTP secret is missing. Use a recovery code or contact support.");
                }

                // Verificar código TOTP
                var secret = DecryptSecret(mfaSettings.TotpSecret);

                if (VerifyTotpCode(secret, code))
                {
                    mfaSettings.LastVerifiedAt = DateTime.UtcNow;
                    mfaSettings.FailedAttempts = 0;
                    await _context.SaveChangesAsync();
                    return (true, null);
                }
            }

            // 🛡️ T2 FIX (parte 2): incremento ATÓMICO de FailedAttempts vía SQL directo.
            // El patrón anterior (`mfaSettings.FailedAttempts++` + SaveChangesAsync) tiene
            // race condition: 5 requests en paralelo leen FailedAttempts=0, todos lo
            // incrementan a 1 en memoria, y SaveChanges del último gana → solo cuenta como
            // 1 intento fallido. Permite N intentos fuera del límite de 5.
            // SQL atómico: UPDATE ... SET FailedAttempts = FailedAttempts + 1 RETURNING
            // garantiza incremento per-request. Postgres serializa el UPDATE a nivel de fila.
            var newFailedAttempts = await _context.UserMfaSettings
                .Where(m => m.UserId == userId && m.IsEnabled)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.FailedAttempts, m => m.FailedAttempts + 1)
                    .SetProperty(m => m.UpdatedAt, DateTime.UtcNow));

            // Re-leer para obtener el contador post-update (no podemos usar mfaSettings local,
            // su valor está stale tras el UPDATE atómico).
            var updatedSettings = await _context.UserMfaSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.UserId == userId);

            var currentAttempts = updatedSettings?.FailedAttempts ?? mfaSettings.FailedAttempts + 1;

            // Bloquear después de 5 intentos fallidos (15 minutos)
            if (currentAttempts >= 5)
            {
                // Otro UPDATE atómico para setear LockedUntil. Idempotente: si llegan 2
                // requests al 5to intento, ambos seteán el mismo LockedUntil aproximado.
                await _context.UserMfaSettings
                    .Where(m => m.UserId == userId && m.IsEnabled)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(m => m.LockedUntil, DateTime.UtcNow.AddMinutes(15)));
                return (false, "Too many failed attempts. Account locked for 15 minutes.");
            }

            return (false, $"Invalid code. {5 - currentAttempts} attempts remaining.");
        }

        /// <summary>
        /// Deshabilita MFA para un usuario (solo requiere código TOTP)
        /// ✅ CORRECCIÓN: Eliminado parámetro password - todos los usuarios usan Google OAuth
        /// </summary>
        public async Task<bool> DisableMfaAsync(int userId, string totpCode)
        {
            var user = await _context.Users.FindAsync(userId);
            if (user == null)
                return false;

            var mfaSettings = await _context.UserMfaSettings
                .FirstOrDefaultAsync(m => m.UserId == userId && m.IsEnabled);

            if (mfaSettings == null)
                return false;

            // Verificar código TOTP
            var secret = DecryptSecret(mfaSettings.TotpSecret);
            if (!VerifyTotpCode(secret, totpCode))
                return false;

            // Deshabilitar MFA (no eliminar, mantener historial)
            mfaSettings.IsEnabled = false;
            mfaSettings.UpdatedAt = DateTime.UtcNow;
            
            await _context.SaveChangesAsync();
            return true;
        }

        /// <summary>
        /// Obtiene el estado de MFA de un usuario
        /// </summary>
        public async Task<MfaStatusDto> GetMfaStatusAsync(int userId)
        {
            var mfaSettings = await _context.UserMfaSettings
                .FirstOrDefaultAsync(m => m.UserId == userId);

            if (mfaSettings == null || !mfaSettings.IsEnabled)
            {
                return new MfaStatusDto
                {
                    IsEnabled = false,
                    IsRequired = false
                };
            }

            var recoveryCodes = DecryptRecoveryCodes(mfaSettings.RecoveryCodesEncrypted ?? "");

            return new MfaStatusDto
            {
                IsEnabled = true,
                IsRequired = true, // Puede personalizarse por rol
                EnabledAt = mfaSettings.EnabledAt,
                LastVerifiedAt = mfaSettings.LastVerifiedAt,
                RemainingRecoveryCodes = recoveryCodes.Count,
                IsLocked = mfaSettings.IsLocked,
                LockedUntil = mfaSettings.LockedUntil
            };
        }
    }
}

