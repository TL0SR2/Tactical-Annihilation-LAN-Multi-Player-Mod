using System;
using System.IO;
using System.Security.Cryptography;
using BepInEx;

namespace AnnW.LanMp.Core
{
    /// <summary>Compute Assembly-CSharp SHA256 hex for Hello handshake (PL4).</summary>
    internal static class GameContentFingerprint
    {
        internal static string ComputeSha256Hex()
        {
            try
            {
                var path = Path.Combine(Paths.GameRootPath, "AnnW_Data", "Managed", "Assembly-CSharp.dll");
                if (!File.Exists(path))
                    return "";

                using (var fs = File.OpenRead(path))
                using (var sha = SHA256.Create())
                {
                    var hash = sha.ComputeHash(fs);
                    return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                }
            }
            catch
            {
                return "";
            }
        }
    }
}
