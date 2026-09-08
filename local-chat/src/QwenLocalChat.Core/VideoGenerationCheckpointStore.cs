using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QwenLocalChat.Core;

/// <summary>
/// Persists resumable video-generation progress (params + completed sampler steps + latent file).
/// Latent tensors live under the ComfyUI output tree; this store only keeps small metadata.
/// </summary>
public sealed record VideoGenerationCheckpoint(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("profile_id")] string ProfileId,
    [property: JsonPropertyName("prompt")] string Prompt,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("duration_seconds")] int DurationSeconds,
    [property: JsonPropertyName("steps")] int Steps,
    [property: JsonPropertyName("seed")] long Seed,
    [property: JsonPropertyName("completed_steps")] int CompletedSteps,
    [property: JsonPropertyName("segment_size")] int SegmentSize,
    [property: JsonPropertyName("latent_file_name")] string? LatentFileName,
    [property: JsonPropertyName("latent_subfolder")] string? LatentSubfolder,
    [property: JsonPropertyName("workflow_sha256")] string WorkflowSha256,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("updated_at_utc")] DateTimeOffset UpdatedAtUtc,
    [property: JsonPropertyName("last_error")] string? LastError = null)
{
    public const int CurrentSchemaVersion = 1;
    public bool CanResume =>
        Status is "ready_to_resume" or "running"
        && CompletedSteps > 0
        && CompletedSteps < Steps
        && !string.IsNullOrWhiteSpace(LatentFileName);
}

public sealed class VideoGenerationCheckpointStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly object _gate = new();

    public VideoGenerationCheckpointStore(string path)
        => _path = path;

    public static string DefaultPath(string localChatRoot)
        => Path.Combine(localChatRoot, "data", "video-generation-checkpoint.json");

    public VideoGenerationCheckpoint? Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return null;
            try
            {
                var json = File.ReadAllText(_path);
                var checkpoint = JsonSerializer.Deserialize<VideoGenerationCheckpoint>(json, JsonOptions);
                if (checkpoint is null || checkpoint.SchemaVersion != VideoGenerationCheckpoint.CurrentSchemaVersion)
                    return null;
                return checkpoint;
            }
            catch (JsonException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
        }
    }

    public void Save(VideoGenerationCheckpoint checkpoint)
    {
        lock (_gate)
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(checkpoint, JsonOptions));
            File.Copy(temp, _path, overwrite: true);
            File.Delete(temp);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (File.Exists(_path)) File.Delete(_path);
            var temp = _path + ".tmp";
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public static string ComputeWorkflowSha256(string workflowPath)
    {
        using var stream = File.OpenRead(workflowPath);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }

    public static string BuildLatentPrefix(string profileId, long seed)
        => $"local-ai/checkpoints/{Sanitize(profileId)}_{seed}";

    private static string Sanitize(string value)
    {
        var chars = value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_').ToArray();
        return new string(chars);
    }
}

/// <summary>
/// Builds optional step-segmented graphs using KSamplerAdvanced + SaveLatent/LoadLatent.
/// Mid-node KSampler still cannot resume; segments are the smallest safe recovery unit.
/// Segments are chained in one graph so the model is loaded once per submission.
///
/// MiniMax H3 is excluded: its AV latent is a NestedTensor, and stock SaveLatent calls
/// <c>samples["samples"].contiguous()</c>, which raises AttributeError.
/// </summary>
public static class VideoSegmentedWorkflowBuilder
{
    public const int DefaultSegmentSize = 5;

    /// <summary>
    /// Whether this API graph can use stock SaveLatent/LoadLatent step checkpoints.
    /// Returns false for MiniMax H3 NestedTensor AV latents.
    /// </summary>
    public static bool SupportsStepLatentCheckpoints(System.Text.Json.Nodes.JsonObject graph)
    {
        foreach (var pair in graph)
        {
            if (pair.Value is not System.Text.Json.Nodes.JsonObject node) continue;
            var classType = node["class_type"]?.GetValue<string>();
            if (classType is "MiniMaxH3ReferenceToVideo" or "MiniMaxH3ImageToVideo"
                or "EmptyMiniMaxH3LatentAV")
                return false;
        }
        return true;
    }

    public static int SegmentSizeFor(int totalSteps, int preferred = DefaultSegmentSize)
    {
        if (totalSteps <= 0) throw new ArgumentOutOfRangeException(nameof(totalSteps));
        if (preferred <= 0) preferred = DefaultSegmentSize;
        return Math.Min(preferred, totalSteps);
    }

    public static IReadOnlyList<(int Start, int End)> PlanSegments(int totalSteps, int segmentSize)
    {
        if (totalSteps <= 0) throw new ArgumentOutOfRangeException(nameof(totalSteps));
        segmentSize = SegmentSizeFor(totalSteps, segmentSize);
        var segments = new List<(int, int)>();
        for (var start = 0; start < totalSteps; start += segmentSize)
            segments.Add((start, Math.Min(totalSteps, start + segmentSize)));
        return segments;
    }

    /// <summary>
    /// Rebuilds sampler node 7 into a chain of KSamplerAdvanced segments with SaveLatent
    /// checkpoints. When <paramref name="resumeLatentFileName"/> is set, sampling continues
    /// from that latent at <paramref name="resumeFromStep"/>.
    /// </summary>
    public static System.Text.Json.Nodes.JsonObject ApplySegmentedSampler(
        System.Text.Json.Nodes.JsonObject graph,
        int totalSteps,
        int segmentSize,
        long seed,
        string latentPrefix,
        int resumeFromStep = 0,
        string? resumeLatentFileName = null)
    {
        if (!SupportsStepLatentCheckpoints(graph))
            throw new InvalidOperationException(
                "当前工作流使用 NestedTensor / MiniMax H3 AV latent，不能用标准 SaveLatent 分段检查点。请使用整图 KSampler。");

        if (totalSteps <= 0) throw new ArgumentOutOfRangeException(nameof(totalSteps));
        if (resumeFromStep < 0 || resumeFromStep >= totalSteps)
            throw new ArgumentOutOfRangeException(nameof(resumeFromStep));

        if (!graph.TryGetPropertyValue("7", out var samplerNode) || samplerNode is not System.Text.Json.Nodes.JsonObject sampler)
            throw new InvalidOperationException("工作流缺少采样节点 7。");
        if (sampler["inputs"] is not System.Text.Json.Nodes.JsonObject samplerInputs)
            throw new InvalidOperationException("采样节点 7 缺少 inputs。");

        var positive = samplerInputs["positive"]?.DeepClone()
            ?? throw new InvalidOperationException("采样节点缺少 positive。");
        var negative = samplerInputs["negative"]?.DeepClone()
            ?? throw new InvalidOperationException("采样节点缺少 negative。");
        var model = samplerInputs["model"]?.DeepClone()
            ?? throw new InvalidOperationException("采样节点缺少 model。");
        var originalLatent = samplerInputs["latent_image"]?.DeepClone()
            ?? throw new InvalidOperationException("采样节点缺少 latent_image。");
        var cfg = samplerInputs["cfg"]?.GetValue<double>() ?? 1;
        var samplerName = samplerInputs["sampler_name"]?.GetValue<string>() ?? "euler";
        var scheduler = samplerInputs["scheduler"]?.GetValue<string>() ?? "simple";

        var segments = PlanSegments(totalSteps, segmentSize)
            .Where(segment => segment.End > resumeFromStep)
            .Select(segment => (Start: Math.Max(segment.Start, resumeFromStep), segment.End))
            .ToArray();
        if (segments.Length == 0)
            throw new InvalidOperationException("没有剩余可执行的采样段。");

        System.Text.Json.Nodes.JsonNode latentSource = originalLatent;
        if (resumeFromStep > 0)
        {
            if (string.IsNullOrWhiteSpace(resumeLatentFileName))
                throw new ArgumentException("从检查点恢复时必须提供 latent 文件名。", nameof(resumeLatentFileName));
            graph["20"] = new System.Text.Json.Nodes.JsonObject
            {
                ["class_type"] = "LoadLatent",
                ["inputs"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["latent"] = resumeLatentFileName,
                },
            };
            latentSource = System.Text.Json.Nodes.JsonNode.Parse("""["20", 0]""")!;
        }

        string? lastSamplerId = null;
        for (var index = 0; index < segments.Length; index++)
        {
            var (startStep, endStep) = segments[index];
            var isFinal = index == segments.Length - 1;
            var samplerId = index == 0 ? "7" : $"7s{index}";
            var isFirstOverall = startStep == 0 && resumeFromStep == 0 && index == 0;

            graph[samplerId] = new System.Text.Json.Nodes.JsonObject
            {
                ["class_type"] = "KSamplerAdvanced",
                ["inputs"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["model"] = model.DeepClone(),
                    ["add_noise"] = isFirstOverall ? "enable" : "disable",
                    ["noise_seed"] = seed,
                    ["steps"] = totalSteps,
                    ["cfg"] = cfg,
                    ["sampler_name"] = samplerName,
                    ["scheduler"] = scheduler,
                    ["positive"] = positive.DeepClone(),
                    ["negative"] = negative.DeepClone(),
                    ["latent_image"] = latentSource.DeepClone(),
                    ["start_at_step"] = startStep,
                    ["end_at_step"] = endStep,
                    ["return_with_leftover_noise"] = isFinal ? "disable" : "enable",
                },
            };

            if (!isFinal)
            {
                var saveId = $"21s{index}";
                graph[saveId] = new System.Text.Json.Nodes.JsonObject
                {
                    ["class_type"] = "SaveLatent",
                    ["inputs"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["samples"] = System.Text.Json.Nodes.JsonNode.Parse($"""["{samplerId}", 0]""")!,
                        ["filename_prefix"] = $"{latentPrefix}_step{endStep}",
                    },
                };
                latentSource = System.Text.Json.Nodes.JsonNode.Parse($"""["{samplerId}", 0]""")!;
            }

            lastSamplerId = samplerId;
        }

        // Point decode nodes at the final sampler output.
        if (lastSamplerId is not null && lastSamplerId != "7")
        {
            RewireSamplerReference(graph, "10", lastSamplerId);
            RewireSamplerReference(graph, "11", lastSamplerId);
        }

        return graph;
    }

    private static void RewireSamplerReference(System.Text.Json.Nodes.JsonObject graph, string nodeId, string samplerId)
    {
        if (!graph.TryGetPropertyValue(nodeId, out var node) || node is not System.Text.Json.Nodes.JsonObject obj)
            return;
        if (obj["inputs"] is not System.Text.Json.Nodes.JsonObject inputs) return;
        if (!inputs.TryGetPropertyValue("samples", out var samples) || samples is not System.Text.Json.Nodes.JsonArray link)
            return;
        if (link.Count > 0) link[0] = samplerId;
    }
}
