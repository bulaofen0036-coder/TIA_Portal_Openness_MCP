using ModelContextProtocol;
using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    // Doctor: one-call environment diagnosis for non-experts / fresh machines.
    // Ported from the pre-split repo where it shipped alongside the lite profile;
    // SKILL.md documents it, so the tool must exist in every released build.
    public static partial class McpServer
    {
        [McpServerTool(Name = "Doctor"), Description("[L0][Diagnostics] One-call environment doctor for non-experts. Checks TIA install, Openness group membership, and connection/project state, and returns a plain-language diagnosis with the exact fix per problem. When fix=true (default) it ENSURES Openness group membership (adds the current user; may prompt a Windows UAC dialog). Read-only apart from that one fix. Call this first when setup is failing or you are unsure the environment is ready.")]
        public static async Task<ResponseDoctor> Doctor(
            [Description("fix: when true (default), ensure Openness group membership (adds user, may prompt UAC). false = read-only diagnosis, no prompt.")] bool fix = true)
        {
            try
            {
                var checks = new List<DoctorCheck>();

                // 1) TIA installation
                int? inUse = Engineering.TiaMajorVersion == 0 ? (int?)null : Engineering.TiaMajorVersion;
                int? detected = Engineering.DetectTiaMajorVersion();
                bool tiaOk = inUse != null || detected != null;
                checks.Add(new DoctorCheck
                {
                    Name = "TIA Portal installation",
                    Ok = tiaOk,
                    Detail = tiaOk ? $"detected V{(inUse ?? detected)}" : "no TIA Portal detected",
                    Fix = tiaOk ? null : "Install TIA Portal V18+ with the Openness option, then set the user environment variable TiaPortalLocation to the install path (e.g. C:\\Program Files\\Siemens\\Automation\\Portal V21)."
                });

                // 2) Openness group membership (+ optional auto-fix)
                bool groupOk;
                if (fix)
                {
                    try { groupOk = await Siemens.Openness.IsUserInGroup(); }
                    catch { groupOk = false; }
                }
                else
                {
                    try { groupOk = Siemens.Openness.IsUserInGroupNoFix(); }
                    catch { groupOk = false; }
                }
                checks.Add(new DoctorCheck
                {
                    Name = "Openness user group",
                    Ok = groupOk,
                    Detail = groupOk ? "current user is in 'Siemens TIA Openness' group" : "current user NOT in 'Siemens TIA Openness' group",
                    Fix = groupOk ? null : "Run Doctor with fix=true (prompts UAC to add you), or manually add your Windows user to the 'Siemens TIA Openness' local group and sign out/in. Admin rights required."
                });

                // 3) Connection + project state
                bool connected = false; string? projectName = null;
                try { var st = Portal.GetState(); connected = st?.IsConnected ?? false; projectName = st?.Project; }
                catch { }
                bool hasProject = !string.IsNullOrWhiteSpace(projectName) && projectName != "-";
                checks.Add(new DoctorCheck
                {
                    Name = "TIA connection / project",
                    Ok = connected,
                    Detail = connected ? (hasProject ? $"connected, project '{projectName}' open" : "connected, no project bound") : "not connected",
                    Fix = connected ? (hasProject ? null : "Call AttachToOpenProject (if a project is open in TIA UI) or OpenProject/CreateProject.") : "Call Connect (first call may pop an Openness authorization dialog in TIA — click Yes)."
                });

                string next;
                if (!tiaOk) next = "(install TIA Portal)";
                else if (!groupOk) next = "EnsureOpennessUserGroup";
                else if (!connected) next = "Connect";
                else if (!hasProject) next = "AttachToOpenProject";
                else next = "GetProjectTree";

                bool ready = tiaOk && groupOk;
                var failed = checks.Where(c => !c.Ok).Select(c => c.Name).ToList();
                string summary = ready && connected && hasProject
                    ? "Environment healthy — project open, ready to work."
                    : ready
                        ? "Environment OK — connect/open a project next."
                        : $"Not ready. Fix: {string.Join("; ", failed)}.";

                return new ResponseDoctor
                {
                    Ready = ready,
                    Checks = checks,
                    RecommendedNextTool = next,
                    Summary = summary,
                    Message = summary,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Doctor unexpected error: {ex.Message}", ex, McpErrorCode.InternalError);
            }
        }
    }
}
