using System.Text.Json;

namespace SIEntryDesk.Core.Access;

/// <summary>
/// Liest Nachrichten aus dem WebSocket /api/v1/developer/devices/notifications.
/// Alles, was nicht dem erwarteten Aufbau entspricht, wird verworfen (Rückgabe null).
/// </summary>
public static class AccessEventParser
{
    public static AccessEvent? Parse(string text)
    {
        var trimmed = text.Trim();
        if (trimmed == "Hello")
            return new AccessHeartbeat();

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(trimmed);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.String)
                return root.GetString() == "Hello" ? new AccessHeartbeat() : null;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var name = GetString(root, "event");
            var data = GetObject(root, "data");
            return name switch
            {
                "access.remote_view" => ParseRingStarted(data),
                "access.remote_view.change" => ParseRingEnded(data),
                "access.data.device.remote_unlock" => ParseDoorUnlocked(root, data),
                "access.logs.add" => (AccessEvent?)ParseLog(data) ?? new AccessOtherEvent(name),
                null or "" => null,
                _ => new AccessOtherEvent(UntrustedText.Clean(name, 80)),
            };
        }
    }

    private static AccessRingStarted? ParseRingStarted(JsonElement data)
    {
        var requestId = GetString(data, "request_id");
        var doorId = GetString(data, "door_id");
        if (!UntrustedText.IsSafeId(requestId) || !UntrustedText.IsSafeId(doorId))
            return null;

        var hubId = GetString(data, "connected_uah_id");
        var deviceId = GetString(data, "device_id");
        var created = GetLong(data, "create_time");
        return new AccessRingStarted(
            RequestId: requestId!,
            DoorId: doorId!,
            DoorName: UntrustedText.Clean(GetString(data, "door_name")),
            HubId: UntrustedText.IsSafeId(hubId) ? hubId! : string.Empty,
            DeviceId: UntrustedText.IsSafeId(deviceId) ? deviceId! : string.Empty,
            DeviceType: UntrustedText.Clean(GetString(data, "device_type"), 60),
            IsCamera: GetBool(data, "is_camera"),
            UnlockingNotAllowed: GetBool(data, "unlocking_not_allowed"),
            CreatedAt: created is > 0 and < 32503680000 ? DateTimeOffset.FromUnixTimeSeconds(created.Value) : null);
    }

    private static AccessRingEnded ParseRingEnded(JsonElement data)
    {
        var requestId = GetString(data, "remote_call_request_id");
        return new AccessRingEnded(UntrustedText.IsSafeId(requestId) ? requestId : null, GetInt(data, "reason_code") ?? 0);
    }

    private static AccessDoorUnlocked? ParseDoorUnlocked(JsonElement root, JsonElement data)
    {
        var doorId = GetString(data, "unique_id");
        if (!UntrustedText.IsSafeId(doorId))
            return null;
        var hubId = GetString(root, "event_object_id");
        return new AccessDoorUnlocked(doorId!, UntrustedText.IsSafeId(hubId) ? hubId! : string.Empty,
            UntrustedText.Clean(GetString(data, "name")));
    }

    /// <summary>Nur erfolgreiche Türöffnungen, mit dem Namen der Person und dem Hub als Verweis auf die Tür.</summary>
    private static AccessUnlockLogged? ParseLog(JsonElement data)
    {
        var source = GetObject(data, "_source");
        var ev = GetObject(source, "event");
        if (GetString(ev, "type") != "access.door.unlock" || GetString(ev, "result") != "ACCESS")
            return null;

        string? hubId = null;
        if (source.ValueKind == JsonValueKind.Object &&
            source.TryGetProperty("target", out var targets) && targets.ValueKind == JsonValueKind.Array)
        {
            foreach (var target in targets.EnumerateArray())
            {
                if (GetString(target, "type") == "door")
                {
                    hubId = GetString(target, "id");
                    break;
                }
            }
        }
        if (!UntrustedText.IsSafeId(hubId))
            return null;

        var actor = UntrustedText.Clean(GetString(GetObject(source, "actor"), "display_name"), 60);
        var provider = UntrustedText.Clean(GetString(GetObject(source, "authentication"), "credential_provider"), 30);
        return new AccessUnlockLogged(hubId!, actor, provider.Length > 0 ? provider : null);
    }

    private static JsonElement GetObject(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object
            ? v
            : default;

    private static string? GetString(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int? GetInt(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
            ? i
            : null;

    private static long? GetLong(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l)
            ? l
            : null;

    private static bool GetBool(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
