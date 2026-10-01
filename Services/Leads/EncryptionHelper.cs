using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AgenticSystem.Services;

public static class EncryptionHelper
{
    public static string Encrypt(string plainText, string secretKey, string salt)
    {
        if (string.IsNullOrEmpty(plainText)) return string.Empty;

        try
        {
            byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
            byte[] saltBytes = Encoding.UTF8.GetBytes(salt);

            // Derive 256-bit key and 128-bit IV using PBKDF2 static method
            byte[] keyAndIv = Rfc2898DeriveBytes.Pbkdf2(
                secretKey,
                saltBytes,
                10000,
                HashAlgorithmName.SHA256,
                48 // 32 bytes for Key + 16 bytes for IV
            );

            byte[] key = new byte[32];
            byte[] iv = new byte[16];
            Buffer.BlockCopy(keyAndIv, 0, key, 0, 32);
            Buffer.BlockCopy(keyAndIv, 32, iv, 0, 16);

            using (var aes = Aes.Create())
            {
                aes.Key = key;
                aes.IV = iv;

                using (var ms = new MemoryStream())
                {
                    using (var cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
                    {
                        cs.Write(plainBytes, 0, plainBytes.Length);
                        cs.FlushFinalBlock();
                    }
                    return Convert.ToBase64String(ms.ToArray());
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Encryption failed: {ex.Message}");
            return string.Empty;
        }
    }

    public static string Decrypt(string cipherText, string secretKey, string salt)
    {
        if (string.IsNullOrEmpty(cipherText)) return string.Empty;

        try
        {
            byte[] cipherBytes = Convert.FromBase64String(cipherText);
            byte[] saltBytes = Encoding.UTF8.GetBytes(salt);

            // Derive 256-bit key and 128-bit IV using PBKDF2 static method
            byte[] keyAndIv = Rfc2898DeriveBytes.Pbkdf2(
                secretKey,
                saltBytes,
                10000,
                HashAlgorithmName.SHA256,
                48 // 32 bytes for Key + 16 bytes for IV
            );

            byte[] key = new byte[32];
            byte[] iv = new byte[16];
            Buffer.BlockCopy(keyAndIv, 0, key, 0, 32);
            Buffer.BlockCopy(keyAndIv, 32, iv, 0, 16);

            using (var aes = Aes.Create())
            {
                aes.Key = key;
                aes.IV = iv;

                using (var ms = new MemoryStream())
                {
                    using (var cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Write))
                    {
                        cs.Write(cipherBytes, 0, cipherBytes.Length);
                        cs.FlushFinalBlock();
                    }
                    return Encoding.UTF8.GetString(ms.ToArray());
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Decryption failed: {ex.Message}");
            return "[Decryption Failed]";
        }
    }
}
