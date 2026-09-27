using System.Collections.Generic;
using System.Linq;

namespace TiaMcpServer.Siemens
{
    /// <summary>
    /// Features whose availability depends on the connected TIA Portal major version.
    /// Only model differences that actually fork the code today — extend deliberately.
    /// </summary>
    public enum TiaFeature
    {
        /// <summary>Hardware-level HMI connection creation via Siemens.Engineering.HW.CommunicationConnections (V21+ only; absent on V20).</summary>
        HardwareHmiConnection,

        /// <summary>SIMATIC SD / S7DCL document export (ExportAsDocuments), available V20 and newer.</summary>
        DocumentExport,

        /// <summary>PLC connection table (S7 connections etc.) via Siemens.Engineering.HW.CommunicationConnections (V21+ only; absent on V20).</summary>
        PlcConnections
    }

    public class CapabilityInfo
    {
        public string Feature { get; set; } = "";
        public bool Supported { get; set; }
        public int MinVersion { get; set; }
        public string Note { get; set; } = "";
    }

    /// <summary>
    /// Single source of truth for version-gated feature availability. Replaces scattered
    /// silent no-ops with an explicit, queryable answer so the model knows up front what
    /// the connected portal can do instead of discovering it through a failed call.
    /// </summary>
    internal static class Capability
    {
        // Minimum TIA major version that supports each feature.
        private static readonly IReadOnlyDictionary<TiaFeature, int> MinVersion =
            new Dictionary<TiaFeature, int>
            {
                [TiaFeature.HardwareHmiConnection] = 21,
                [TiaFeature.DocumentExport] = 20,
                [TiaFeature.PlcConnections] = 21
            };

        private static readonly IReadOnlyDictionary<TiaFeature, string> Notes =
            new Dictionary<TiaFeature, string>
            {
                [TiaFeature.HardwareHmiConnection] = "Siemens.Engineering.HW.CommunicationConnections is not exposed on TIA V20; hardware HMI connection creation requires V21 or newer.",
                [TiaFeature.DocumentExport] = "ExportAsDocuments (SIMATIC SD / S7DCL) requires TIA Portal V20 or newer.",
                [TiaFeature.PlcConnections] = "Siemens.Engineering.HW.CommunicationConnections is not exposed on TIA V20; creating or listing PLC connections (S7 connections for PUT/GET) requires V21 or newer. On V20 create the connection in the TIA network view."
            };

        /// <summary>True when the connected portal version supports the feature. Unknown version (0) is treated as supported to avoid false negatives.</summary>
        public static bool IsSupported(TiaFeature feature)
        {
            int major = Engineering.TiaMajorVersion;
            if (major <= 0) return true; // version unknown -> don't block; the call itself will surface any real error
            return major >= MinVersion[feature];
        }

        /// <summary>Throws <see cref="PortalException"/> (NotSupportedOnVersion) when the feature is unavailable on the connected version.</summary>
        public static void RequireSupported(TiaFeature feature)
        {
            if (!IsSupported(feature))
                throw new PortalException(PortalErrorCode.NotSupportedOnVersion, Describe(feature));
        }

        /// <summary>Human-readable explanation of why a feature is/isn't available, including the version requirement.</summary>
        public static string Describe(TiaFeature feature)
        {
            return Notes.TryGetValue(feature, out var note) ? note : $"{feature} requires TIA Portal V{MinVersion[feature]} or newer.";
        }

        /// <summary>Snapshot of every feature's availability against the connected version, for Bootstrap to advertise.</summary>
        public static List<CapabilityInfo> Snapshot()
        {
            return MinVersion.Keys
                .Select(f => new CapabilityInfo
                {
                    Feature = f.ToString(),
                    Supported = IsSupported(f),
                    MinVersion = MinVersion[f],
                    Note = Describe(f)
                })
                .ToList();
        }
    }
}
