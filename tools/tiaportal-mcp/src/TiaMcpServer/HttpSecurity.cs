using System;
using System.Collections.Generic;
using System.Net;

namespace TiaMcpServer
{
    /// <summary>
    /// The HTTP transport's security decisions, kept free of HttpListener so the offline suite can
    /// pin them. The endpoint can download to a PLC, so "who may call it" is not a detail.
    /// </summary>
    internal static class HttpSecurity
    {
        public const string ApiKeyEnvironmentVariable = "TIA_MCP_HTTP_API_KEY";

        /// <summary>True when an HttpListener prefix only accepts connections from this machine.
        /// "+", "*", 0.0.0.0, a LAN address or a host name all reach beyond it.</summary>
        public static bool IsLoopbackPrefix(string? prefix)
        {
            if (string.IsNullOrWhiteSpace(prefix)) return false;
            string p = prefix!.Trim();
            int scheme = p.IndexOf("://", StringComparison.Ordinal);
            if (scheme < 0) return false;
            string rest = p.Substring(scheme + 3);

            string host;
            if (rest.StartsWith("[", StringComparison.Ordinal))
            {
                int close = rest.IndexOf(']');
                if (close < 0) return false;
                host = rest.Substring(1, close - 1);
            }
            else
            {
                int end = rest.IndexOfAny(new[] { ':', '/' });
                host = end < 0 ? rest : rest.Substring(0, end);
            }
            return IsLoopbackHost(host);
        }

        /// <summary>Browsers attach Origin to cross-site requests; a page on any other site must not
        /// be able to drive this server (a text/plain POST needs no CORS preflight). Clients that are
        /// not browsers send no Origin and are allowed.</summary>
        public static bool IsAllowedOrigin(string? origin)
        {
            if (string.IsNullOrEmpty(origin)) return true;
            // Sandboxed frames and file:// pages send the literal "null".
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
            return IsLoopbackHost(uri.Host.Trim('[', ']'));
        }

        public static bool IsLoopbackHost(string? host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
            return IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
        }

        /// <summary>True when the TCP peer is this machine (IPv4/IPv6 loopback, incl. IPv4-mapped).</summary>
        public static bool IsLoopbackRemote(IPAddress? address)
        {
            if (address == null) return false;
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            return IPAddress.IsLoopback(address);
        }

        /// <summary>Compares secrets without an early exit, so response timing does not leak
        /// how many leading characters were right.</summary>
        public static bool ConstantTimeEquals(string? a, string? b)
        {
            if (a == null || b == null) return false;
            int diff = a.Length ^ b.Length;
            int n = Math.Max(a.Length, b.Length);
            for (int i = 0; i < n; i++)
            {
                char ca = i < a.Length ? a[i] : '\0';
                char cb = i < b.Length ? b[i] : '\0';
                diff |= ca ^ cb;
            }
            return diff == 0;
        }

        /// <summary>The API key from the command line, else from the environment. The environment
        /// variable exists because a command-line key is visible in the process list and in logs.</summary>
        public static string? ResolveApiKey(string? commandLineKey, Func<string, string?>? getEnv = null)
        {
            if (!string.IsNullOrEmpty(commandLineKey)) return commandLineKey;
            var v = (getEnv ?? Environment.GetEnvironmentVariable)(ApiKeyEnvironmentVariable);
            return string.IsNullOrEmpty(v) ? null : v;
        }

        /// <summary>The argument list with every secret value replaced, for the startup log.</summary>
        public static string RedactArgs(IEnumerable<string>? args)
        {
            if (args == null) return "";
            var outList = new List<string>();
            bool hideNext = false;
            foreach (var a in args)
            {
                if (hideNext) { outList.Add("***"); hideNext = false; continue; }
                if (string.Equals(a, "--http-api-key", StringComparison.OrdinalIgnoreCase))
                {
                    outList.Add(a);
                    hideNext = true;
                    continue;
                }
                if (a != null && a.StartsWith("--http-api-key=", StringComparison.OrdinalIgnoreCase))
                {
                    outList.Add("--http-api-key=***");
                    continue;
                }
                outList.Add(a ?? "");
            }
            return string.Join(" ", outList);
        }
    }
}
