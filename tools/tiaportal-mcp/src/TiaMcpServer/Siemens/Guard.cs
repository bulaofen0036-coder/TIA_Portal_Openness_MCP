using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcpServer.Siemens
{
    /// <summary>
    /// Replaces scattered null-check + return-false patterns with single-line assertions
    /// that throw <see cref="PortalException"/> on failure, letting the MCP layer handle
    /// error mapping uniformly.
    /// </summary>
    internal static class Guard
    {
        /// <summary>Throws <see cref="PortalException"/> (NotFound) if <paramref name="value"/> is null.</summary>
        public static T RequireNotNull<T>(T? value, string kind, string path)
            where T : class
        {
            if (value == null)
                throw new PortalException(PortalErrorCode.NotFound, $"{kind} not found: '{path}'");
            return value;
        }

        /// <summary>Throws <see cref="PortalException"/> (InvalidParams) if <paramref name="value"/> is null or whitespace.</summary>
        public static string RequireNonEmpty(string? value, string paramName)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new PortalException(PortalErrorCode.InvalidParams, $"Parameter '{paramName}' must not be empty.");
            return value!;
        }

        /// <summary>Throws <see cref="PortalException"/> (InvalidState) if <paramref name="condition"/> is false.</summary>
        public static void Require(bool condition, string message)
        {
            if (!condition)
                throw new PortalException(PortalErrorCode.InvalidState, message);
        }

        /// <summary>
        /// Returns <paramref name="candidates"/> formatted as a "Did you mean …?" hint string,
        /// or an empty string when the list is empty. Intended for <see cref="PortalException"/> messages.
        /// </summary>
        public static string DidYouMean(IEnumerable<string>? candidates)
        {
            if (candidates == null) return string.Empty;
            var list = new List<string>(candidates);
            if (list.Count == 0) return string.Empty;
            return " Did you mean: " + string.Join(", ", list) + "?";
        }

        /// <summary>
        /// Tolerant resolution of a (possibly sloppy) softwarePath <paramref name="token"/> against the
        /// available PLC software names. Returns the single resolved name, or null when there is no
        /// match or the match is ambiguous. Pure/deterministic (no Openness) — unit-testable offline.
        /// Order: case-insensitive exact (after trim) → a single-PLC project accepts any token →
        /// unique case-insensitive substring (either direction, e.g. "plc" → "PLC_1").
        /// </summary>
        public static string? MatchPlcName(IReadOnlyList<string>? available, string? token)
        {
            if (available == null || available.Count == 0) return null;
            var t = (token ?? string.Empty).Trim();

            // 1) case-insensitive exact (covers wrong case + surrounding whitespace)
            var exact = available.Where(n => string.Equals(n?.Trim(), t, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1) return exact[0];

            // 2) a single-PLC project resolves any token to its sole PLC
            if (exact.Count == 0 && available.Count == 1) return available[0];

            // 3) unique case-insensitive substring match in either direction
            if (exact.Count == 0 && t.Length > 0)
            {
                var sub = available.Where(n => !string.IsNullOrEmpty(n) && (
                    n!.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    t.IndexOf(n!, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
                if (sub.Count == 1) return sub[0];
            }
            return null;
        }
    }
}
