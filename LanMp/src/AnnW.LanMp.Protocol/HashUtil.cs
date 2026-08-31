using System;
using System.Security.Cryptography;
using System.Text;

namespace AnnW.LanMp.Protocol
{
    public static class HashUtil
    {
        public static string StableHash16(string text)
        {
            if (text == null)
                return "";
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant().Substring(0, 16);
            }
        }
    }
}
