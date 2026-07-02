using ModelContextProtocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json.Nodes;

namespace TiaMcpServer.ModelContextProtocol
{
    // On-demand authoring cheat sheets (see McpGuides). Purpose: models driving this server
    // from hosts that never load SKILL.md (VS Code / Cursor / third-party agents) kept
    // hand-writing fragile FlgNet XML, mixing up BOM rules, and retry-looping on syntax
    // errors — this tool hands them the verified rules right before they write code.
    public static partial class McpServer
    {
        [McpServerTool(Name = "GetAuthoringGuide"), Description("[L0][Guide] Verified syntax + workflow cheat sheet for authoring TIA content through this server. CALL THIS BEFORE writing any SCL/LAD/DB/HMI content — it prevents the common encoding, syntax and tool-routing mistakes. Topics: workflow, scl, lad, db, hmi, errors. Read-only, does not touch TIA Portal.")]
        public static ResponseMessage GetAuthoringGuide(
            [Description("topic: one of workflow | scl | lad | db | hmi | errors")] string topic)
        {
            var text = McpGuides.Topic(topic);
            if (text == null)
            {
                return new ResponseMessage
                {
                    Message = $"Unknown topic '{topic}'. Available topics: {McpGuides.TopicList}.",
                    Meta = new JsonObject { ["success"] = false, ["topics"] = McpGuides.TopicList },
                };
            }
            return new ResponseMessage
            {
                Message = text,
                Meta = new JsonObject { ["success"] = true, ["topic"] = topic.Trim().ToLowerInvariant() },
            };
        }
    }
}
