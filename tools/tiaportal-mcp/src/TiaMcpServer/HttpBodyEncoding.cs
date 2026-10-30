using System;
using System.Text;

namespace TiaMcpServer
{
    /// <summary>
    /// 请求体解码。JSON-RPC / MCP 规定消息体是 UTF-8，但
    /// <see cref="System.Net.HttpListenerRequest.ContentEncoding"/> 在 Content-Type 不带 charset 时
    /// 返回 <see cref="Encoding.Default"/>（中文 Windows 上是 GBK/CP936）而不是 null，
    /// 所以 `req.ContentEncoding ?? Encoding.UTF8` 的兜底永远不会触发：
    /// UTF-8 字节被按 GBK 解码，U+00A0 变成「聽」，中文路径与注释全部损坏。
    /// 缺省必须是 UTF-8；只有客户端显式写了 charset 才按它解。
    /// </summary>
    internal static class HttpBodyEncoding
    {
        internal static Encoding Resolve(string? contentType)
        {
            string? ct = contentType;
            if (ct != null && ct.Length > 0)
            {
                int at = ct.IndexOf("charset", StringComparison.OrdinalIgnoreCase);
                if (at >= 0)
                {
                    int eq = ct.IndexOf('=', at);
                    if (eq > 0)
                    {
                        string name = ct.Substring(eq + 1).Trim();
                        int semi = name.IndexOf(';');
                        if (semi >= 0) name = name.Substring(0, semi).Trim();
                        name = name.Trim('"', '\'');
                        if (name.Length > 0)
                        {
                            try { return Encoding.GetEncoding(name); }
                            catch (ArgumentException) { }
                            catch (NotSupportedException) { }
                        }
                    }
                }
            }
            return Encoding.UTF8;
        }
    }
}
