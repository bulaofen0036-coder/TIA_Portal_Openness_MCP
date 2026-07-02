using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TiaMcpServer.License
{
    /// <summary>
    /// 本地 JWT 缓存。存储 RSA 签名的授权 token 到
    /// %ProgramData%\TiaMcpServer\.license_cache，
    /// 读取时验签 + 比对 machineId + 检查过期。
    /// 
    /// 纯 JWT 明文存储 — RSA 签名保证防篡改，
    /// machineId 保证防跨机器复制。
    /// </summary>
    public static class LicenseCache
    {
        // ── RSA 公钥参数（从 license_public.pem 提取，硬编码） ──────

        private static readonly byte[] RsaModulus = HexToBytes(
            "C76855795914A2CA6BC0D3729A91B067651C17D74955D27EBC2DE1AF90E78B106" +
            "CAE278AB35B3D9745A42C4F565CC8C92F8D1105983FD35CDE0242D66AA1A34E18" +
            "0B2568A8AC4314527C3822FA4C16FEA27987D5978F22A73631C362FB8DF06B02B" +
            "630354170902E45F2C55CA36C4D92FA6FD798C41135F86E514D3FB9DFEE3659B1" +
            "6FCD592F802F070C112D76631AE97C86B01EF10783A44FB22C1FDD2AE3E6779E3" +
            "4599980645CAA2774B7FB55F9D96681BE848C986603CA79E6F0564E93E9EB11AB" +
            "81E8E7C736773D8586292D00F13CE28CE68DE49F3C63EBBDE926861C3B470476D" +
            "D8917983DDCD1A92EC35D5ACFC5B6A048C4F3A537DAC94AEEF3BA6BFF");

        private static readonly byte[] RsaExponent = { 0x01, 0x00, 0x01 };

        // ── 缓存路径 ──────────────────────────────────────────────

        private static readonly string CacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "TiaMcpServer");

        private static readonly string CacheFile = Path.Combine(CacheDir, ".license_cache");

        // ── 公共 API ──────────────────────────────────────────────

        /// <summary>
        /// 读取本地缓存的 token，通过 RSA 验签、machineId 比对、过期检查。
        /// 任一步失败返回 null（静默，不抛异常）。
        /// </summary>
        public static string? TryLoad()
        {
            try
            {
                if (!File.Exists(CacheFile))
                    return null;

                var token = File.ReadAllText(CacheFile, Encoding.UTF8).Trim();
                if (string.IsNullOrWhiteSpace(token) || !token.Contains("."))
                    return null;

                var parts = token.Split('.');
                if (parts.Length != 3)
                    return null;

                // ── 验证 RSA 签名 ─────────────────────────────────
                var signingInput = Encoding.UTF8.GetBytes(parts[0] + "." + parts[1]);
                var signature = Base64UrlDecode(parts[2]);

                using var rsa = new RSACryptoServiceProvider(2048);
                rsa.ImportParameters(new RSAParameters
                {
                    Modulus = RsaModulus,
                    Exponent = RsaExponent
                });

                if (!rsa.VerifyData(signingInput, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                    return null;

                // ── 解码 payload ──────────────────────────────────
                var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));

                // ── 比对 machineId ────────────────────────────────
                var mid = ExtractJsonString(payloadJson, "mid");
                if (mid != MachineId.Get())
                    return null;

                // ── 检查过期 ──────────────────────────────────────
                var exp = ExtractJsonLong(payloadJson, "exp");
                if (exp <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                    return null;

                return token;
            }
            catch
            {
                // 任何异常都视为缓存无效，走在线验证
                return null;
            }
        }

        /// <summary>
        /// 将有效的 token 写入本地缓存。
        /// 自动创建 %ProgramData%\TiaMcpServer 目录。
        /// </summary>
        public static void Save(string token)
        {
            Directory.CreateDirectory(CacheDir);
            File.WriteAllText(CacheFile, token, Encoding.UTF8);
        }

        // ── JSON 解析（零依赖，简单字符串提取） ────────────────────

        /// <summary>从 JSON 字符串中提取 string 字段值。</summary>
        private static string? ExtractJsonString(string json, string key)
        {
            var search = $"\"{key}\":\"";
            var start = json.IndexOf(search, StringComparison.Ordinal);
            if (start < 0) return null;
            start += search.Length;
            var end = json.IndexOf('"', start);
            if (end < 0) return null;
            return json.Substring(start, end - start);
        }

        /// <summary>从 JSON 字符串中提取数字字段值。</summary>
        private static long ExtractJsonLong(string json, string key)
        {
            var search = $"\"{key}\":";
            var start = json.IndexOf(search, StringComparison.Ordinal);
            if (start < 0) return 0;
            start += search.Length;
            var end = start;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-'))
                end++;
            if (end == start) return 0;
            return long.Parse(json.Substring(start, end - start));
        }

        // ── 工具函数 ─────────────────────────────────────────────

        private static byte[] Base64UrlDecode(string s)
        {
            s = s.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
            }
            return Convert.FromBase64String(s);
        }

        private static byte[] HexToBytes(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
            return bytes;
        }
    }
}
