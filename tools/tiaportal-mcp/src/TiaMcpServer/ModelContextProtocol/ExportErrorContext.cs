using System;
using System.Text.Json.Nodes;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>
    /// Machine-readable context for a refused single-object export (issue #63).
    ///
    /// The MCP SDK in use only carries a text message on tool errors, so the context is appended as
    /// one trailing line: <c>ERROR_CONTEXT: {json}</c>. Clients parse that line instead of
    /// pattern-matching the localized native message.
    ///
    /// Rules the fields follow:
    /// - <c>scope</c> is "object" for a per-object refusal and "session" when the TIA Portal process
    ///   is gone (every handle is dead) or no project is open. "object" only says where the failure happened; it does not
    ///   authorize carrying on with other requests.
    /// - <c>reason</c> names what the native message says; <c>rootCauseKnown</c> stays false unless
    ///   the reason itself is the root cause. "not permitted" alone does not tell permission from
    ///   capability, so it is reported as notPermitted with rootCauseKnown=false.
    /// - <c>retryable</c> means "the same call, unchanged, may succeed". It is never true here:
    ///   nothing is retried, compiled or escalated automatically.
    /// Zero dependencies so the offline suite can feed it real exception chains.
    /// </summary>
    internal static class ExportErrorContext
    {
        internal const string Prefix = "ERROR_CONTEXT: ";

        internal static JsonObject Build(string phase, string objectKind, string? softwarePath, string? objectPath, Exception? ex)
        {
            var native = NativeMessage(ex);
            var processLost = PortalFailureClassifier.IsPortalProcessLost(ex);
            var noProject = Has(native, "no project is open");
            string reason;
            bool rootCauseKnown;
            if (processLost) { reason = "portalProcessLost"; rootCauseKnown = true; }
            else if (noProject) { reason = "noProjectOpen"; rootCauseKnown = true; }
            else if (Has(native, "inconsistent") || Has(native, "not consistent")) { reason = "inconsistent"; rootCauseKnown = true; }
            else if (Has(native, "know-how") || Has(native, "knowhow")) { reason = "knowHowProtected"; rootCauseKnown = true; }
            else if (Has(native, "not permitted") || Has(native, "not allowed")) { reason = "notPermitted"; rootCauseKnown = false; }
            else { reason = "unknown"; rootCauseKnown = false; }

            return new JsonObject
            {
                ["code"] = "ExportFailed",
                ["phase"] = phase,
                ["objectKind"] = objectKind,
                ["softwarePath"] = softwarePath,
                ["objectPath"] = objectPath,
                ["scope"] = processLost || noProject ? "session" : "object",
                ["reason"] = reason,
                ["rootCauseKnown"] = rootCauseKnown,
                ["retryable"] = false,
                ["nativeMessage"] = native
            };
        }

        /// <summary>One line, ready to append to an error message.</summary>
        internal static string Line(string phase, string objectKind, string? softwarePath, string? objectPath, Exception? ex)
            => Prefix + Build(phase, objectKind, softwarePath, objectPath, ex).ToJsonString();

        /// <summary>The innermost non-wrapper message: what Openness itself said.</summary>
        private static string? NativeMessage(Exception? ex)
        {
            string? last = null;
            for (var e = ex; e != null; e = e.InnerException)
            {
                var m = e.Message?.Trim();
                if (!string.IsNullOrEmpty(m) && e is not System.Reflection.TargetInvocationException) last = m;
            }
            return last;
        }

        private static bool Has(string? haystack, string needle)
            => (haystack ?? string.Empty).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
