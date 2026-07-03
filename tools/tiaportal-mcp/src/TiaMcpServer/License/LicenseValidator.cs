using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace TiaMcpServer.License
{
    /// <summary>
    /// 授权校验主入口。先查本地缓存，过期/不存在时调在线 API。
    /// 
    /// 返回 (Ok, Message)：Ok 为 true 表示授权通过，
    /// Message 包含状态描述供调用方日志记录。
    /// </summary>
    public static class LicenseValidator
    {
        private static readonly HttpClient HttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        /// <summary>
        /// 校验 license key。流程：
        ///   1. key 为空 → 拒绝
        ///   2. 本地缓存有效 → 通过
        ///   3. 在线 POST /api/validate → 成功则缓存 token → 通过
        ///   4. 以上全失败 → 拒绝
        /// </summary>
        public static async Task<(bool Ok, string Message)> Validate(
            string? licenseKey,
            string serverUrl)
        {
            // ── 1. 无 Key ────────────────────────────────────────
            if (string.IsNullOrWhiteSpace(licenseKey))
            {
                return (false, "LICENSE: no --license-key provided. Obtain a key from your vendor.");
            }

            // ── 2. 本地缓存 ──────────────────────────────────────
            var cached = LicenseCache.TryLoad();
            if (cached != null)
            {
                return (true, "LICENSE: cache valid, offline mode.");
            }

            // ── 3. 在线校验 ──────────────────────────────────────
            try
            {
                var machineId = MachineId.Get();
                var json = $"{{\"key\":\"{EscapeJson(licenseKey)}\",\"machineId\":\"{machineId}\"}}";
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var url = serverUrl.TrimEnd('/') + "/api/validate";
                using var response = await HttpClient.PostAsync(url, content);
                var body = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var token = ExtractJsonString(body, "token");
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        LicenseCache.Save(token);
                        return (true, "LICENSE: online validation OK, token cached for 30 days.");
                    }
                    return (false, $"LICENSE: server returned success but no token in response: {Truncate(body)}");
                }

                // 服务器拒绝
                var reason = GetFriendlyError(response.StatusCode, body);
                return (false, $"LICENSE: server rejected ({reason})");
            }
            catch (TaskCanceledException)
            {
                return (false, "LICENSE: server timeout (10s). Check network or try later.");
            }
            catch (HttpRequestException ex)
            {
                return (false, $"LICENSE: cannot reach license server: {ex.Message}");
            }
            catch (Exception ex)
            {
                return (false, $"LICENSE: unexpected error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ── 友好错误信息 ─────────────────────────────────────────

        private static string GetFriendlyError(System.Net.HttpStatusCode code, string body)
        {
            switch ((int)code)
            {
                case 401:
                    return "invalid license key";
                case 403:
                    {
                        var msg = ExtractJsonString(body, "error");
                        return msg ?? "license expired or revoked";
                    }
                case 409:
                    return "key already bound to max machines";
                case 429:
                    return "too many requests, please wait";
                default:
                    return $"HTTP {(int)code} {Truncate(body)}";
            }
        }

        // ── 工具函数 ─────────────────────────────────────────────

        /// <summary>从 JSON 中提取 string 字段值（零依赖简单解析）。</summary>
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

        /// <summary>转义 JSON 字符串中的特殊字符。</summary>
        private static string EscapeJson(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        /// <summary>截断过长的响应体，避免日志里打印太大。</summary>
        private static string Truncate(string s, int max = 200)
        {
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }
    }
}
