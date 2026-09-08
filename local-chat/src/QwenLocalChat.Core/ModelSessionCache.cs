using System.Text.Json;

namespace QwenLocalChat.Core;

/// <summary>
/// llama-server keeps the previous prompt in the assigned slot (and may pick a
/// slot by prompt similarity). A new Local AI thread must not inherit that KV.
/// </summary>
public static class ModelSessionCache
{
    public static bool ShouldReset(string? lastSessionId, string? sessionId, int runningGenerations)
    {
        if (runningGenerations > 1) return false;
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        return !string.Equals(lastSessionId, sessionId, StringComparison.Ordinal);
    }

    public static IReadOnlyList<int> IdleSlotIds(JsonElement root)
    {
        JsonElement array;
        if (root.ValueKind == JsonValueKind.Array)
            array = root;
        else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("slots", out var slots)
                 && slots.ValueKind == JsonValueKind.Array)
            array = slots;
        else
            return [];

        var ids = new List<int>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (IsBusy(item)) continue;
            if (TryReadSlotId(item, out var id))
                ids.Add(id);
        }
        return ids;
    }

    private static bool IsBusy(JsonElement item)
    {
        if (item.TryGetProperty("is_processing", out var processing)
            && processing.ValueKind is JsonValueKind.True)
            return true;
        if (item.TryGetProperty("processing", out var processingAlt)
            && processingAlt.ValueKind is JsonValueKind.True)
            return true;
        if (item.TryGetProperty("id_task", out var task)
            && task.TryGetInt32(out var taskId)
            && taskId >= 0)
            return true;
        return false;
    }

    private static bool TryReadSlotId(JsonElement item, out int id)
    {
        if (item.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out id))
            return true;
        if (item.TryGetProperty("id_slot", out var slotElement) && slotElement.TryGetInt32(out id))
            return true;
        id = -1;
        return false;
    }
}
