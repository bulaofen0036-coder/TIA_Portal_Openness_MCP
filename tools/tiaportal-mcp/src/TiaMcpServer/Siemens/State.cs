using System.Collections.Generic;

namespace TiaMcpServer.Siemens
{
    public class State
    {
        public bool? IsConnected { get; set; }
        public string? Project { get; set; }
        public string? Session { get; set; }
        /// <summary>Projects visible in the attached TIA Portal instance (may be empty).</summary>
        public IEnumerable<string>? AttachableProjects { get; set; }
        /// <summary>Concrete next call when no project is bound.</summary>
        public string? NextAction { get; set; }
    }
}
