using System.Text.Json;

namespace AscNet.GameServer
{
    /// <summary>
    /// Emits protocol compatibility metadata without writing packet payloads.
    /// The JSONL is an observation input for Scripts/protocol_gap.py, not a
    /// runtime response source.
    /// </summary>
    internal static class ProtocolGapProbe
    {
        private static readonly object WriteLock = new();
        private static readonly string? OutputPath = ResolveOutputPath();
        private static readonly string Region = Environment.GetEnvironmentVariable("ASCNET_REGION")?.Trim().ToLowerInvariant() ?? "global";

        public static string RequestFailureStatus(Exception exception) =>
            exception is MessagePack.MessagePackSerializationException or InvalidDataException
                ? "field_mismatch"
                : "handler_error";

        public static void Record(
            string session,
            string direction,
            string name,
            string packetTypeName,
            string status,
            int? payloadBytes = null,
            int? requestId = null,
            string? error = null,
            string? point = null,
            string? lastSuccessfulRequest = null)
        {
            if (OutputPath is null)
                return;

            var row = new Dictionary<string, object?>
            {
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
                ["region"] = Region,
                ["session"] = Truncate(session),
                ["direction"] = Truncate(direction),
                ["name"] = Truncate(name),
                ["packet_type_name"] = Truncate(packetTypeName),
                ["status"] = Truncate(status),
            };
            if (payloadBytes is not null)
                row["payload_len"] = payloadBytes;
            if (requestId is not null)
                row["request_id"] = requestId;
            if (!string.IsNullOrEmpty(error))
                row["error"] = Truncate(error);
            if (!string.IsNullOrEmpty(point))
                row["point"] = Truncate(point);
            if (!string.IsNullOrEmpty(lastSuccessfulRequest))
                row["last_successful_request"] = Truncate(lastSuccessfulRequest);

            try
            {
                string? directory = Path.GetDirectoryName(OutputPath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                string line = JsonSerializer.Serialize(row) + Environment.NewLine;
                lock (WriteLock)
                    File.AppendAllText(OutputPath, line);
            }
            catch
            {
                // Diagnostics must never affect packet delivery or disconnect handling.
            }
        }

        private static string? ResolveOutputPath()
        {
            string? configured = Environment.GetEnvironmentVariable("ASCNET_PROTOCOL_GAP_LOG");
            if (string.IsNullOrWhiteSpace(configured))
                return null;

            return Path.GetFullPath(configured);
        }

        private static string Truncate(string value) => value.Length <= 256 ? value : value[..256];
    }
}
