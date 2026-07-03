using Siemens.Collaboration.Net;
using System.Threading.Tasks;

namespace TiaMcpServer.Siemens
{
    public static class Openness
    {
        public static int TiaMajorVersion { get; private set; }

        public static void Initialize(int? tiaMajorVersion = 21)
        {
            // with nuget packages:
            // 2.1 nuget package: Siemens.Collaboration.Net.TiaPortal.Openness.Resolver
            //     & User Environment Variable: TiaPortalLocation=C:\Program Files\Siemens\Automation\Portal V20
            // 2.2 nuget package: Siemens.Collaboration.Net.TiaPortal.Packages.Openness
            // 2.3 Api.Global.Openness().Initialize(tiaMajorVersion: 20); // fixed version 20

            TiaMajorVersion = tiaMajorVersion ?? 21; // Default to TIA Portal V21 if not specified

            // Initialize the Openness API with the specified TIA Portal major version
            Api.Global.Openness().Initialize(tiaMajorVersion: tiaMajorVersion);
        }

        // Pure check — does NOT add the user or prompt UAC. Use for read-only diagnosis.
        public static bool IsUserInGroupNoFix()
        {
            return Api.Global.Openness().IsUserInGroup();
        }

        public static async Task<bool> IsUserInGroup()
        {
            if (Api.Global.Openness().IsUserInGroup())
            {
                // user is in group
                return true;
            }
            else
            {
                return await Api.Global.Openness().AddUserToGroupAsync();
            }
        }
    }
}
