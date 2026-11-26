using System;
using System.IO;
using System.Security.Cryptography;

namespace BDTool.UnityCore
{
    /// <summary>
    /// 以文件原始字节计算 SHA-256。不要改用 AssetDatabase 文本或序列化对象摘要，
    /// 否则不能与外部文件指纹做同一口径的 CAS 比较。
    /// </summary>
    internal static class AssetFileFingerprint
    {
        public static string ComputeSha256(string fullPath)
        {
            using (SHA256 sha256 = SHA256.Create())
            using (FileStream stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.SequentialScan))
            {
                byte[] hash = sha256.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
            }
        }
    }
}
