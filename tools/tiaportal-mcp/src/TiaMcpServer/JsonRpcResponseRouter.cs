using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace TiaMcpServer
{
    /// <summary>
    /// Matches JSON-RPC responses coming out of the MCP host to the HTTP requests waiting for them.
    ///
    /// One reader drains the host's output stream for the whole process lifetime, and each HTTP
    /// request waits on its own id. The design this replaced started a fresh reader per request
    /// and left it running when the request timed out: from then on two readers shared one stream,
    /// the orphan swallowed the next response, and every later request timed out too.
    ///
    /// No MCP SDK types, so the offline suite can drive it.
    /// </summary>
    internal sealed class JsonRpcResponseRouter
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pending =
            new ConcurrentDictionary<string, TaskCompletionSource<string>>(StringComparer.Ordinal);

        private volatile Exception? _closed;

        public int PendingCount => _pending.Count;

        /// <summary>Canonical key for a JSON-RPC id, so 7 and "7" stay distinct and match themselves.</summary>
        public static string IdKey(JsonNode? id) => id == null ? "null" : id.ToJsonString();

        /// <summary>Registers interest in the response to <paramref name="idKey"/>. Call BEFORE the
        /// request is written, or a fast response could arrive with nobody waiting for it.
        /// Returns null when a request with the same id is already in flight.</summary>
        public Task<string>? Expect(string idKey)
        {
            var closed = _closed;
            if (closed != null)
                return Task.FromException<string>(new IOException("The MCP host is no longer running.", closed));

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _pending.TryAdd(idKey, tcs) ? tcs.Task : null;
        }

        /// <summary>Stops waiting for <paramref name="idKey"/>; a response arriving later is dropped.</summary>
        public void Abandon(string idKey)
        {
            if (_pending.TryRemove(idKey, out var tcs))
                tcs.TrySetCanceled();
        }

        /// <summary>Feeds one line of host output. True when it completed a waiting request.
        /// Notifications and server-to-client requests (they carry a method) are not responses.</summary>
        public bool Route(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;
            JsonNode? node;
            try { node = JsonNode.Parse(line!); }
            catch (JsonException) { return false; }

            if (node is not JsonObject obj) return false;
            if (obj.ContainsKey("method")) return false;
            if (!obj.TryGetPropertyValue("id", out var id)) return false;

            return _pending.TryRemove(IdKey(id), out var tcs) && tcs.TrySetResult(line!);
        }

        /// <summary>Drains <paramref name="reader"/> until it ends, then fails everything still waiting.
        /// Blocking by design: the host's output is a blocking stream, so run this on its own thread.</summary>
        public void Pump(TextReader reader)
        {
            Exception reason = new EndOfStreamException("The MCP host closed its output stream.");
            try
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                    Route(line);
            }
            catch (Exception ex)
            {
                reason = ex;
            }
            Close(reason);
        }

        /// <summary>Fails every waiting request and refuses new ones.</summary>
        public void Close(Exception reason)
        {
            _closed = reason ?? new EndOfStreamException();
            foreach (var key in _pending.Keys)
                if (_pending.TryRemove(key, out var tcs))
                    tcs.TrySetException(new IOException("The MCP host is no longer running.", _closed));
        }
    }
}
