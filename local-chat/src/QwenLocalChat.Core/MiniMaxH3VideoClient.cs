using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QwenLocalChat.Core;

public sealed record VideoGenerationRequest(
    string Prompt,
    VideoGenerationSettings? Settings = null,
    VideoMediaInputs? Media = null)
{
    public VideoGenerationSettings EffectiveSettings => Settings ?? VideoGenerationSettings.SafeDefaults;
    public VideoMediaInputs EffectiveMedia => Media ?? VideoMediaInputs.TextOnly;
}

public sealed record VideoJob(
    string PromptId,
    string Status,
    double? Progress = null,
    string? Error = null,
    string? OutputPath = null,
    int OutputsCount = 0,
    string? RawStatus = null)
{
    public bool IsTerminal => Status is "completed" or "failed" or "cancelled";
}

public static class ComfyUiJobResponseParser
{
    public static VideoJob Parse(string promptId, string json, Func<JsonElement, string?>? resolveOutput = null)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var status = root.TryGetProperty("status", out var statusElement)
            ? statusElement.GetString() ?? "unknown"
            : "unknown";
        double? progress = root.TryGetProperty("progress", out var progressElement) && progressElement.TryGetDouble(out var progressValue)
            ? progressValue
            : null;
        var outputsCount = 0;
        if (root.TryGetProperty("outputs_count", out var countElement) && countElement.TryGetInt32(out var count))
            outputsCount = count;
        var error = ExtractError(root);
        var output = resolveOutput?.Invoke(root);
        // Normalize legacy / alternate status strings.
        status = status.ToLowerInvariant() switch
        {
            "error" => "failed",
            "success" => "completed",
            "running" => "in_progress",
            "queued" => "pending",
            _ => status,
        };
        return new VideoJob(promptId, status, progress, error, output, outputsCount, status);
    }

    public static string? ExtractError(JsonElement root)
    {
        if (root.TryGetProperty("execution_error", out var executionError)
            && executionError.ValueKind is JsonValueKind.Object or JsonValueKind.String)
        {
            if (executionError.ValueKind == JsonValueKind.String)
                return executionError.GetString();
            if (executionError.TryGetProperty("exception_message", out var message)
                && message.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(message.GetString()))
            {
                var type = executionError.TryGetProperty("exception_type", out var typeElement)
                    ? typeElement.GetString()
                    : null;
                var text = message.GetString()!;
                return string.IsNullOrWhiteSpace(type) ? text : $"{type}: {text}";
            }
            return executionError.ToString();
        }

        if (root.TryGetProperty("error", out var error) && error.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            if (error.ValueKind == JsonValueKind.String) return error.GetString();
            if (error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("exception_message", out var nested)
                && nested.ValueKind == JsonValueKind.String)
                return nested.GetString();
            return error.ToString();
        }

        return null;
    }

    public static bool IsQueuePayload(string queueJson)
    {
        try
        {
            using var document = JsonDocument.Parse(queueJson);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && (root.TryGetProperty("queue_running", out _) || root.TryGetProperty("queue_pending", out _));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool IsHistoryPayload(string historyJson)
    {
        try
        {
            using var document = JsonDocument.Parse(historyJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            // Job payloads look like { "status": "in_progress", ... } and must not be treated as history.
            if (root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String
                && !root.TryGetProperty("prompt", out _)
                && root.EnumerateObject().All(p => p.Name is "status" or "progress" or "error" or "execution_error"
                    or "preview_output" or "outputs" or "outputs_count" or "id" or "priority"
                    or "create_time" or "workflow_id" or "execution_start_time" or "execution_end_time"
                    or "execution_status" or "workflow"))
                return false;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool HistoryHasPrompt(string historyJson, string promptId)
    {
        if (!IsHistoryPayload(historyJson)) return false;
        using var document = JsonDocument.Parse(historyJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (root.TryGetProperty(promptId, out _)) return true;
        // Some deployments wrap as { "History": { id: ... } }
        if (root.TryGetProperty("History", out var history) && history.ValueKind == JsonValueKind.Object
            && history.TryGetProperty(promptId, out _))
            return true;
        return false;
    }

    public static VideoJob? TryParseHistory(string historyJson, string promptId, Func<JsonElement, string?>? resolveOutput = null)
    {
        using var document = JsonDocument.Parse(historyJson);
        var root = document.RootElement;
        JsonElement item;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(promptId, out item))
        { }
        else if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("History", out var history)
            && history.ValueKind == JsonValueKind.Object
            && history.TryGetProperty(promptId, out item))
        { }
        else
            return null;

        // Reuse jobs-API normalization shape if already normalized.
        if (item.TryGetProperty("status", out var statusElement) && statusElement.ValueKind == JsonValueKind.String)
            return Parse(promptId, item.GetRawText(), resolveOutput);

        var statusInfo = item.TryGetProperty("status", out var statusObject) && statusObject.ValueKind == JsonValueKind.Object
            ? statusObject
            : default;
        var statusStr = statusInfo.ValueKind == JsonValueKind.Object
            && statusInfo.TryGetProperty("status_str", out var statusStrElement)
                ? statusStrElement.GetString()
                : null;
        string? error = null;
        var cancelled = false;
        if (statusInfo.ValueKind == JsonValueKind.Object
            && statusInfo.TryGetProperty("messages", out var messages)
            && messages.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in messages.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() < 2) continue;
                var name = entry[0].GetString();
                var data = entry[1];
                if (name == "execution_error")
                    error = ExtractError(JsonDocument.Parse($"{{\"execution_error\":{data.GetRawText()}}}").RootElement);
                else if (name == "execution_interrupted")
                    cancelled = true;
            }
        }

        var status = statusStr switch
        {
            "success" => "completed",
            "error" when cancelled => "cancelled",
            "error" => "failed",
            _ => cancelled ? "cancelled" : "completed",
        };
        return new VideoJob(promptId, status, null, error, resolveOutput?.Invoke(item), 0, statusStr);
    }

    public static bool QueueContains(string queueJson, string promptId)
    {
        using var document = JsonDocument.Parse(queueJson);
        var root = document.RootElement;
        foreach (var key in new[] { "queue_running", "queue_pending" })
        {
            if (!root.TryGetProperty(key, out var list) || list.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var item in list.EnumerateArray())
            {
                // Queue items: [priority, prompt_id, prompt, extra, outputs]
                if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() > 1)
                {
                    var id = item[1].ValueKind == JsonValueKind.String ? item[1].GetString() : item[1].ToString();
                    if (string.Equals(id, promptId, StringComparison.Ordinal))
                        return true;
                }
            }
        }
        return false;
    }
}

public sealed class ComfyUiWorkflowVideoClient : IDisposable
{
    private readonly string _workflowPath;
    private readonly VideoModelProfile _profile;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private string? _lastFingerprint;
    private DateTimeOffset _lastFingerprintChangeUtc = DateTimeOffset.UtcNow;

    /// <summary>Injected for tests; production uses <see cref="VideoJobLiveness.DefaultStuckThreshold"/>.</summary>
    public TimeSpan StuckThreshold { get; set; } = VideoJobLiveness.DefaultStuckThreshold;

    public ComfyUiWorkflowVideoClient(string projectRoot, VideoModelProfile profile, HttpClient? httpClient = null)
    {
        _profile = profile;
        if (profile.Adapter != "comfyui-workflow")
            throw new InvalidOperationException("不支持的视频档案适配器。");
        profile.Service.Validate(projectRoot);
        _workflowPath = new VideoModelProfileCatalogStore(projectRoot, Path.Combine(projectRoot, "runtime", "video-model-profiles.json"))
            .ResolveWorkflowPath(profile.WorkflowPath);
        if (!File.Exists(_workflowPath))
            throw new FileNotFoundException("视频工作流不存在。", _workflowPath);
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient(new SocketsHttpHandler { UseProxy = false });
    }

    public string WorkflowPath => _workflowPath;

    public object BuildGraph(VideoGenerationRequest request)
        => BuildGraphObject(request);

    public JsonObject BuildGraphObject(VideoGenerationRequest request, Action<JsonObject>? mutate = null)
        => BuildGraphObjectCore(request, uploadedImages: null, mutate);

    /// <summary>
    /// Builds the graph and uploads local media for the selected conditioning mode.
    /// Safe to call while ComfyUI is already running — only POSTs images + prompt.
    /// </summary>
    public async Task<JsonObject> BuildGraphObjectAsync(
        VideoGenerationRequest request,
        Action<JsonObject>? mutate = null,
        CancellationToken cancellationToken = default)
    {
        var media = request.EffectiveMedia;
        var mediaErrors = media.Validate(_profile.EffectiveMedia);
        if (mediaErrors.Count > 0)
            throw new ArgumentException(string.Join("；", mediaErrors), nameof(request));

        var uploads = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        async Task EnsureUpload(string key, string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            uploads[key] = await UploadImageAsync(path, cancellationToken).ConfigureAwait(false);
        }

        switch (media.Mode)
        {
            case VideoConditioningMode.FirstFrame:
                await EnsureUpload("first_frame", media.FirstFramePath).ConfigureAwait(false);
                break;
            case VideoConditioningMode.FirstLastFrame:
                await EnsureUpload("first_frame", media.FirstFramePath).ConfigureAwait(false);
                await EnsureUpload("last_frame", media.LastFramePath).ConfigureAwait(false);
                break;
            case VideoConditioningMode.LastFrame:
                await EnsureUpload("last_frame", media.LastFramePath).ConfigureAwait(false);
                break;
            case VideoConditioningMode.SingleReferenceImage:
                var refs = media.ResolvedReferenceImages();
                for (var i = 0; i < refs.Count; i++)
                    await EnsureUpload($"ref_image_{i}", refs[i]).ConfigureAwait(false);
                break;
            case VideoConditioningMode.ReferenceVideo:
                var videos = media.ResolvedReferenceVideos();
                for (var i = 0; i < videos.Count; i++)
                    await EnsureUpload($"ref_video_{i}", videos[i]).ConfigureAwait(false);
                break;
            case VideoConditioningMode.ReferenceAudio:
                var audios = media.ResolvedReferenceAudios();
                for (var i = 0; i < audios.Count; i++)
                    await EnsureUpload($"ref_audio_{i}", audios[i]).ConfigureAwait(false);
                break;
        }

        return BuildGraphObjectCore(request, uploads, mutate);
    }

    private JsonObject BuildGraphObjectCore(
        VideoGenerationRequest request,
        IReadOnlyDictionary<string, string>? uploadedImages,
        Action<JsonObject>? mutate)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("视频提示词不能为空。", nameof(request));
        var settings = request.EffectiveSettings;
        var errors = _profile.Capabilities.Validate(settings);
        if (errors.Count > 0)
            throw new ArgumentException(string.Join("；", errors), nameof(request));
        var media = request.EffectiveMedia;
        var mediaErrors = media.Validate(_profile.EffectiveMedia);
        if (mediaErrors.Count > 0)
            throw new ArgumentException(string.Join("；", mediaErrors), nameof(request));
        if (media.Mode is not VideoConditioningMode.Text && (uploadedImages is null || uploadedImages.Count == 0))
            throw new ArgumentException("图生/参考模式需要先上传媒体文件。", nameof(request));

        var root = JsonNode.Parse(File.ReadAllText(_workflowPath))?.AsObject()
            ?? throw new InvalidOperationException("视频工作流必须是 JSON 对象。");
        var graph = root["prompt"]?.AsObject() ?? root;
        var mapping = _profile.InputMapping;
        var seed = settings.RandomSeed
            ? Random.Shared.NextInt64(0, (long)int.MaxValue + 1)
            : settings.Seed;
        Set(graph, mapping.Prompt, VideoPromptComposer.Compose(
            extras: "",
            template: request.Prompt,
            durationSeconds: settings.DurationSeconds));
        Set(graph, mapping.Width, settings.Width);
        Set(graph, mapping.Height, settings.Height);
        Set(graph, mapping.Frames, _profile.Capabilities.AlignFrames(settings.DurationSeconds));
        Set(graph, mapping.Steps, settings.Steps);
        Set(graph, mapping.Seed, seed);
        Set(graph, mapping.Fps, _profile.Capabilities.FramesPerSecond);
        Set(graph, mapping.OutputPrefix, $"local-ai/{_profile.Id}_{settings.Width}x{settings.Height}_{seed}");
        Set(graph, mapping.Format, settings.OutputFormat);
        Set(graph, mapping.Codec, settings.VideoCodec);
        ApplyWorkflowParameters(graph, mapping, _profile.EffectiveWorkflow);
        ApplyMediaConditioning(graph, media, uploadedImages);
        mutate?.Invoke(graph);
        // Stash resolved seed for callers that need checkpoint metadata.
        graph["__resolved_seed"] = seed;
        return graph;
    }

    /// <summary>
    /// Rewires conditioning node 5 between MiniMaxH3ImageToVideo (keyframes) and
    /// MiniMaxH3ReferenceToVideo (text / single ref image). Injects LoadImage nodes.
    /// </summary>
    public static void ApplyMediaConditioning(
        JsonObject graph,
        VideoMediaInputs media,
        IReadOnlyDictionary<string, string>? uploadedImages)
    {
        if (!graph.TryGetPropertyValue("5", out var node5) || node5 is not JsonObject conditioning)
            throw new InvalidOperationException("工作流缺少条件节点 5。");
        if (conditioning["inputs"] is not JsonObject inputs)
            throw new InvalidOperationException("条件节点 5 缺少 inputs。");

        // Preserve shared links / sizes already written by bindings.
        var clip = inputs["clip"]?.DeepClone() ?? JsonNode.Parse("""["2", 0]""")!;
        var vae = inputs["vae"]?.DeepClone() ?? JsonNode.Parse("""["3", 0]""")!;
        var audioVae = inputs["audio_vae"]?.DeepClone() ?? JsonNode.Parse("""["4", 0]""")!;
        var prompt = inputs["prompt"]?.DeepClone() ?? JsonValue.Create("");
        var width = inputs["width"]?.DeepClone() ?? JsonValue.Create(864);
        var height = inputs["height"]?.DeepClone() ?? JsonValue.Create(480);
        var length = inputs["length"]?.DeepClone() ?? JsonValue.Create(243);

        // Drop previously injected loaders so rebuilds stay idempotent.
        foreach (var key in graph.Select(pair => pair.Key).Where(IsInjectedMediaNode).ToArray())
            graph.Remove(key);

        switch (media.Mode)
        {
            case VideoConditioningMode.FirstFrame:
            case VideoConditioningMode.FirstLastFrame:
            case VideoConditioningMode.LastFrame:
            {
                var newInputs = new JsonObject
                {
                    ["clip"] = clip.DeepClone(),
                    ["vae"] = vae.DeepClone(),
                    ["prompt"] = prompt.DeepClone(),
                    ["width"] = width.DeepClone(),
                    ["height"] = height.DeepClone(),
                    ["length"] = length.DeepClone(),
                };
                if (media.Mode is VideoConditioningMode.FirstFrame or VideoConditioningMode.FirstLastFrame)
                {
                    var firstName = RequireUpload(uploadedImages, "first_frame");
                    var firstLoader = AddLoadImage(graph, "load_img_first", firstName);
                    newInputs["first_frame"] = JsonNode.Parse($"""["{firstLoader}", 0]""")!;
                }
                if (media.Mode is VideoConditioningMode.LastFrame or VideoConditioningMode.FirstLastFrame)
                {
                    var lastName = RequireUpload(uploadedImages, "last_frame");
                    var lastLoader = AddLoadImage(graph, "load_img_last", lastName);
                    newInputs["last_frame"] = JsonNode.Parse($"""["{lastLoader}", 0]""")!;
                }

                graph["5"] = new JsonObject
                {
                    ["class_type"] = "MiniMaxH3ImageToVideo",
                    ["inputs"] = newInputs,
                };
                break;
            }
            case VideoConditioningMode.SingleReferenceImage:
            case VideoConditioningMode.ReferenceVideo:
            case VideoConditioningMode.ReferenceAudio:
            {
                var newInputs = NewReferenceInputs(clip, vae, audioVae, prompt, width, height, length, media.RefImageSize);
                AttachReferenceImages(graph, newInputs, media, uploadedImages);
                AttachReferenceVideos(graph, newInputs, media, uploadedImages);
                AttachReferenceAudios(graph, newInputs, media, uploadedImages);
                graph["5"] = new JsonObject
                {
                    ["class_type"] = "MiniMaxH3ReferenceToVideo",
                    ["inputs"] = newInputs,
                };
                break;
            }
            default:
            {
                // Text-only: leave non-H3 workflows untouched; for H3 templates restore ReferenceToVideo.
                var classType = conditioning["class_type"]?.GetValue<string>();
                if (classType is not ("MiniMaxH3ReferenceToVideo" or "MiniMaxH3ImageToVideo"))
                    return;

                conditioning["class_type"] = "MiniMaxH3ReferenceToVideo";
                // Strip media wires only. Do NOT use StartsWith("ref_image") — that also
                // matches ref_image_size and would drop a required combo input.
                foreach (var key in inputs.Select(p => p.Key).Where(IsTransientMediaInputKey).ToArray())
                    inputs.Remove(key);
                if (!inputs.ContainsKey("audio_vae"))
                    inputs["audio_vae"] = audioVae.DeepClone();
                if (!inputs.ContainsKey("ref_image_size"))
                    inputs["ref_image_size"] = "match";
                break;
            }
        }
    }

    /// <summary>
    /// Keys that must not be left on a text-only ReferenceToVideo node.
    /// Autogrow slots are dotted (ref_images.ref_image_0); plain ImageToVideo uses first/last_frame.
    /// </summary>
    internal static bool IsTransientMediaInputKey(string key)
    {
        if (key is "first_frame" or "last_frame") return true;
        // Exact dotted Autogrow paths (0-based indices). Never treat ref_image_size as transient.
        return key.StartsWith("ref_images.", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("ref_videos.", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("ref_video_audios.", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("ref_audios.", StringComparison.OrdinalIgnoreCase)
            // Legacy mistaken flat keys from older client builds.
            || (key.StartsWith("ref_image_", StringComparison.OrdinalIgnoreCase) && key is not "ref_image_size")
            || key.StartsWith("ref_video_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("ref_audio_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInjectedMediaNode(string key)
        => key.StartsWith("load_img_", StringComparison.Ordinal)
            || key.StartsWith("load_vid_", StringComparison.Ordinal)
            || key.StartsWith("load_aud_", StringComparison.Ordinal)
            || key.StartsWith("split_vid_", StringComparison.Ordinal);

    private static JsonObject NewReferenceInputs(
        JsonNode clip, JsonNode vae, JsonNode audioVae, JsonNode? prompt,
        JsonNode width, JsonNode height, JsonNode length, string? refImageSize)
        => new()
        {
            ["clip"] = clip.DeepClone(),
            ["vae"] = vae.DeepClone(),
            ["audio_vae"] = audioVae.DeepClone(),
            ["prompt"] = prompt?.DeepClone() ?? JsonValue.Create(""),
            ["width"] = width.DeepClone(),
            ["height"] = height.DeepClone(),
            ["length"] = length.DeepClone(),
            ["ref_image_size"] = string.IsNullOrWhiteSpace(refImageSize) ? "match" : refImageSize.Trim().ToLowerInvariant(),
        };

    private static void AttachReferenceImages(
        JsonObject graph,
        JsonObject inputs,
        VideoMediaInputs media,
        IReadOnlyDictionary<string, string>? uploadedImages)
    {
        var keys = CollectUploadKeys(media.ResolvedReferenceImages().Count, "ref_image_", uploadedImages);
        for (var i = 0; i < keys.Count; i++)
        {
            var refName = RequireUpload(uploadedImages, keys[i]);
            var refLoader = AddLoadImage(graph, $"load_img_ref_{i}", refName);
            inputs[$"ref_images.ref_image_{i}"] = JsonNode.Parse($"""["{refLoader}", 0]""")!;
        }
    }

    private static void AttachReferenceVideos(
        JsonObject graph,
        JsonObject inputs,
        VideoMediaInputs media,
        IReadOnlyDictionary<string, string>? uploadedImages)
    {
        var keys = CollectUploadKeys(media.ResolvedReferenceVideos().Count, "ref_video_", uploadedImages);
        for (var i = 0; i < keys.Count; i++)
        {
            var fileName = RequireUpload(uploadedImages, keys[i]);
            var loader = AddLoadVideo(graph, $"load_vid_{i}", fileName);
            var splitter = AddGetVideoComponents(graph, $"split_vid_{i}", loader);
            inputs[$"ref_videos.ref_video_{i}"] = JsonNode.Parse($"""["{splitter}", 0]""")!;
            inputs[$"ref_video_audios.ref_video_audio_{i}"] = JsonNode.Parse($"""["{splitter}", 1]""")!;
        }
    }

    private static void AttachReferenceAudios(
        JsonObject graph,
        JsonObject inputs,
        VideoMediaInputs media,
        IReadOnlyDictionary<string, string>? uploadedImages)
    {
        var keys = CollectUploadKeys(media.ResolvedReferenceAudios().Count, "ref_audio_", uploadedImages);
        for (var i = 0; i < keys.Count; i++)
        {
            var fileName = RequireUpload(uploadedImages, keys[i]);
            var loader = AddLoadAudio(graph, $"load_aud_{i}", fileName);
            inputs[$"ref_audios.ref_audio_{i}"] = JsonNode.Parse($"""["{loader}", 0]""")!;
        }
    }

    private static List<string> CollectUploadKeys(
        int knownCount,
        string prefix,
        IReadOnlyDictionary<string, string>? uploadedImages)
    {
        var uploadKeys = new List<string>();
        if (knownCount > 0)
        {
            for (var i = 0; i < knownCount; i++)
                uploadKeys.Add($"{prefix}{i}");
            return uploadKeys;
        }

        if (uploadedImages is null) return uploadKeys;
        uploadKeys.AddRange(uploadedImages.Keys
            .Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase));
        return uploadKeys;
    }

    private static string AddLoadImage(JsonObject graph, string nodeId, string uploadedFileName)
    {
        graph[nodeId] = new JsonObject
        {
            ["class_type"] = "LoadImage",
            ["inputs"] = new JsonObject
            {
                ["image"] = uploadedFileName,
            },
        };
        return nodeId;
    }

    private static string AddLoadVideo(JsonObject graph, string nodeId, string uploadedFileName)
    {
        graph[nodeId] = new JsonObject
        {
            ["class_type"] = "LoadVideo",
            ["inputs"] = new JsonObject
            {
                ["file"] = uploadedFileName,
            },
        };
        return nodeId;
    }

    private static string AddGetVideoComponents(JsonObject graph, string nodeId, string videoNodeId)
    {
        graph[nodeId] = new JsonObject
        {
            ["class_type"] = "GetVideoComponents",
            ["inputs"] = new JsonObject
            {
                ["video"] = JsonNode.Parse($"""["{videoNodeId}", 0]""")!,
            },
        };
        return nodeId;
    }

    private static string AddLoadAudio(JsonObject graph, string nodeId, string uploadedFileName)
    {
        graph[nodeId] = new JsonObject
        {
            ["class_type"] = "LoadAudio",
            ["inputs"] = new JsonObject
            {
                ["audio"] = uploadedFileName,
            },
        };
        return nodeId;
    }

    private static string RequireUpload(IReadOnlyDictionary<string, string>? uploads, string key)
    {
        if (uploads is null || !uploads.TryGetValue(key, out var name) || string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException($"缺少已上传的媒体：{key}");
        return name;
    }

    public async Task<string> UploadImageAsync(string localPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
            throw new FileNotFoundException("要上传的图片不存在。", localPath);
        await using var stream = File.OpenRead(localPath);
        using var content = new MultipartFormDataContent();
        var fileContent = new StreamContent(stream);
        var ext = Path.GetExtension(localPath).ToLowerInvariant();
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            ".mkv" => "video/x-matroska",
            ".avi" => "video/x-msvideo",
            ".wav" => "audio/wav",
            ".mp3" => "audio/mpeg",
            ".flac" => "audio/flac",
            ".ogg" => "audio/ogg",
            ".m4a" => "audio/mp4",
            ".aac" => "audio/aac",
            _ => "application/octet-stream",
        });
        content.Add(fileContent, "image", Path.GetFileName(localPath));
        using var response = await _http.PostAsync(new Uri(_profile.Service.BaseUri, "upload/image"), content, cancellationToken)
            .ConfigureAwait(false);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"上传图片失败：HTTP {(int)response.StatusCode}: {raw}");
        using var document = JsonDocument.Parse(raw);
        var name = document.RootElement.TryGetProperty("name", out var nameElement)
            ? nameElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("ComfyUI 未返回上传后的图片文件名。");
        // Prefer folder-relative name when subfolder is present.
        if (document.RootElement.TryGetProperty("subfolder", out var sub) && !string.IsNullOrWhiteSpace(sub.GetString()))
            return $"{sub.GetString()}/{name}".Replace('\\', '/');
        return name;
    }

    public long TakeResolvedSeed(JsonObject graph)
    {
        if (graph.TryGetPropertyValue("__resolved_seed", out var seedNode) && seedNode is not null)
        {
            var seed = seedNode.GetValue<long>();
            graph.Remove("__resolved_seed");
            return seed;
        }
        return 0;
    }

    public async Task<VideoJob> SubmitAsync(VideoGenerationRequest request, CancellationToken cancellationToken = default)
    {
        var graph = request.EffectiveMedia.Mode is VideoConditioningMode.Text
            ? BuildGraphObject(request)
            : await BuildGraphObjectAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);
        return await SubmitGraphAsync(graph, cancellationToken).ConfigureAwait(false);
    }

    public async Task<VideoJob> SubmitGraphAsync(JsonObject graph, CancellationToken cancellationToken = default)
    {
        graph.Remove("__resolved_seed");
        ResetLiveness();
        using var response = await _http.PostAsJsonAsync(
            new Uri(_profile.Service.BaseUri, "prompt"),
            new { prompt = graph },
            cancellationToken).ConfigureAwait(false);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"ComfyUI 返回 HTTP {(int)response.StatusCode}: {raw}");
        using var document = JsonDocument.Parse(raw);
        var id = document.RootElement.GetProperty("prompt_id").GetString();
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException("ComfyUI 未返回 prompt_id。");
        return new VideoJob(id, "queued");
    }

    public async Task<VideoJob> GetJobAsync(string promptId, CancellationToken cancellationToken = default)
    {
        var serviceReachable = true;
        string? jobJson = null;
        string? jobError = null;
        try
        {
            using var response = await _http.GetAsync(
                new Uri(_profile.Service.BaseUri, $"api/jobs/{Uri.EscapeDataString(promptId)}"),
                cancellationToken).ConfigureAwait(false);
            jobJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Fall through to history/queue triangulation.
                jobJson = null;
            }
            else if (!response.IsSuccessStatusCode)
            {
                jobError = $"ComfyUI 返回 HTTP {(int)response.StatusCode}: {jobJson}";
                serviceReachable = response.StatusCode != System.Net.HttpStatusCode.BadGateway
                    && response.StatusCode != System.Net.HttpStatusCode.ServiceUnavailable;
            }
        }
        catch (HttpRequestException ex)
        {
            serviceReachable = false;
            jobError = $"无法连接视频服务：{ex.Message}";
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            serviceReachable = false;
            jobError = "视频服务请求超时。";
        }

        VideoJob? job = jobJson is null
            ? null
            : ComfyUiJobResponseParser.Parse(promptId, jobJson, ResolveOutput);

        var historyJson = await TryGetStringAsync($"history/{Uri.EscapeDataString(promptId)}", cancellationToken).ConfigureAwait(false)
            ?? await TryGetStringAsync("history", cancellationToken).ConfigureAwait(false);
        var queueJson = await TryGetStringAsync("queue", cancellationToken).ConfigureAwait(false);

        var historyPayload = historyJson is not null && ComfyUiJobResponseParser.IsHistoryPayload(historyJson);
        var queuePayload = queueJson is not null && ComfyUiJobResponseParser.IsQueuePayload(queueJson);
        var historyPresent = historyPayload && ComfyUiJobResponseParser.HistoryHasPrompt(historyJson!, promptId);
        var presentInQueue = queuePayload && ComfyUiJobResponseParser.QueueContains(queueJson!, promptId);

        if (historyPresent && historyJson is not null)
        {
            var fromHistory = ComfyUiJobResponseParser.TryParseHistory(historyJson, promptId, ResolveOutput);
            if (fromHistory is not null && fromHistory.IsTerminal)
                job = MergeResolvedOutput(fromHistory, job);
            else if (job is null && fromHistory is not null)
                job = fromHistory;
        }

        job ??= new VideoJob(
            promptId,
            presentInQueue ? "in_progress" : "unknown",
            Error: jobError);

        if (!string.IsNullOrWhiteSpace(jobError) && string.IsNullOrWhiteSpace(job.Error))
            job = job with { Error = jobError };

        // Prefer explicit failed status when execution_error is present.
        if (!job.IsTerminal
            && !string.IsNullOrWhiteSpace(job.Error)
            && VideoJobLiveness.LooksLikeFatalBackendError(job.Error))
        {
            job = job with { Status = "failed" };
        }

        // When queue/history probes are unavailable, fall back to the jobs API status alone
        // so unit fixtures that only stub /api/jobs do not look like orphaned tasks.
        var observedInQueue = presentInQueue
            || (!queuePayload && job.Status is "in_progress" or "pending" or "queued");
        var observedHistory = historyPresent;
        var canJudgeOrphan = queuePayload && historyPayload;

        var snapshot = new VideoJobSnapshot(
            job.Status,
            job.Progress,
            job.OutputsCount,
            job.Error,
            observedInQueue,
            observedHistory,
            serviceReachable,
            QueueAndHistoryObserved: canJudgeOrphan);

        var fingerprint = snapshot.Fingerprint;
        var now = DateTimeOffset.UtcNow;
        if (!string.Equals(fingerprint, _lastFingerprint, StringComparison.Ordinal))
        {
            _lastFingerprint = fingerprint;
            _lastFingerprintChangeUtc = now;
        }

        var decision = VideoJobLiveness.Evaluate(
            snapshot,
            now - _lastFingerprintChangeUtc,
            StuckThreshold);

        return decision.Action switch
        {
            VideoJobLivenessAction.MarkFailed => new VideoJob(
                promptId,
                decision.Status,
                job.Progress,
                decision.Error ?? job.Error,
                job.OutputPath,
                job.OutputsCount),
            VideoJobLivenessAction.UseSnapshot => job,
            _ => job,
        };
    }

    public async Task CancelAsync(string promptId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync(
            new Uri(_profile.Service.BaseUri, $"api/jobs/{Uri.EscapeDataString(promptId)}/cancel"),
            null,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"取消视频任务失败：HTTP {(int)response.StatusCode}");
    }

    public void ResetLiveness()
    {
        _lastFingerprint = null;
        _lastFingerprintChangeUtc = DateTimeOffset.UtcNow;
    }

    private async Task<string?> TryGetStringAsync(string relative, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(new Uri(_profile.Service.BaseUri, relative), cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static void ApplyWorkflowParameters(
        JsonObject graph,
        VideoNodeInputMapping mapping,
        VideoWorkflowParameters workflow)
    {
        if (workflow.Cfg is { } cfg) Set(graph, mapping.Cfg, cfg);
        if (!string.IsNullOrWhiteSpace(workflow.SamplerName)) Set(graph, mapping.SamplerName, workflow.SamplerName.Trim());
        if (!string.IsNullOrWhiteSpace(workflow.Scheduler)) Set(graph, mapping.Scheduler, workflow.Scheduler.Trim());
        if (workflow.Denoise is { } denoise) Set(graph, mapping.Denoise, denoise);
        if (workflow.ShiftVideo is { } shiftVideo) Set(graph, mapping.ShiftVideo, shiftVideo);
        if (workflow.ShiftAudio is { } shiftAudio) Set(graph, mapping.ShiftAudio, shiftAudio);
        if (workflow.NegativePrompt is not null) Set(graph, mapping.NegativePrompt, workflow.NegativePrompt);
    }

    private static void Set(JsonObject graph, VideoNodeInputBinding? binding, object value)
    {
        if (binding is null) return;
        if (!graph.TryGetPropertyValue(binding.NodeId, out var node)
            || node is not JsonObject nodeObject
            || nodeObject["inputs"] is not JsonObject inputs)
            throw new InvalidOperationException("视频工作流映射节点不存在或缺少 inputs。");
        inputs[binding.InputName] = JsonValue.Create(value);
    }

    private static VideoJob MergeResolvedOutput(VideoJob fromHistory, VideoJob? fromJobs)
    {
        if (fromJobs is null) return fromHistory;
        if (string.IsNullOrWhiteSpace(fromHistory.OutputPath) && !string.IsNullOrWhiteSpace(fromJobs.OutputPath))
            fromHistory = fromHistory with { OutputPath = fromJobs.OutputPath };
        if (fromHistory.OutputsCount == 0 && fromJobs.OutputsCount > 0)
            fromHistory = fromHistory with { OutputsCount = fromJobs.OutputsCount };
        return fromHistory;
    }

    private string? ResolveOutput(JsonElement root)
    {
        if (TryResolveMediaPath(root.TryGetProperty("preview_output", out var preview) ? preview : default) is { } previewPath)
            return previewPath;

        if (!root.TryGetProperty("outputs", out var outputs) || outputs.ValueKind != JsonValueKind.Object)
            return null;

        string? fallback = null;
        foreach (var node in outputs.EnumerateObject())
        {
            if (node.Value.ValueKind != JsonValueKind.Object) continue;
            foreach (var media in node.Value.EnumerateObject())
            {
                if (media.NameEquals("animated") || media.Value.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var item in media.Value.EnumerateArray())
                {
                    if (!LooksLikeVideoOutput(item, media.Name)) continue;
                    if (TryResolveMediaPath(item) is not { } path) continue;
                    if (HasOutputType(item)) return path;
                    fallback ??= path;
                }
            }
        }

        return fallback;
    }

    private string? TryResolveMediaPath(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty("filename", out var fileNameElement))
            return null;
        var name = fileNameElement.GetString();
        if (string.IsNullOrWhiteSpace(name)) return null;
        var sub = item.TryGetProperty("subfolder", out var subfolder) ? subfolder.GetString() : null;
        var path = Path.GetFullPath(Path.Combine(_profile.Service.OutputDirectory, sub ?? string.Empty, name));
        if (!VideoServiceConfiguration.IsContained(_profile.Service.OutputDirectory, path))
            throw new InvalidOperationException("视频输出路径越出 ComfyUI output 目录。");
        return path;
    }

    private static bool LooksLikeVideoOutput(JsonElement item, string mediaType)
    {
        if (item.ValueKind != JsonValueKind.Object) return false;
        if (mediaType.Equals("video", StringComparison.OrdinalIgnoreCase)) return true;
        if (item.TryGetProperty("mediaType", out var mediaTypeElement)
            && string.Equals(mediaTypeElement.GetString(), "video", StringComparison.OrdinalIgnoreCase))
            return true;
        if (item.TryGetProperty("format", out var formatElement))
        {
            var format = formatElement.GetString();
            if (!string.IsNullOrWhiteSpace(format) && format.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        if (!item.TryGetProperty("filename", out var nameElement)) return false;
        var name = nameElement.GetString();
        if (string.IsNullOrWhiteSpace(name)) return false;
        var extension = Path.GetExtension(name).ToLowerInvariant();
        return extension is ".mp4" or ".webm" or ".mov" or ".mkv" or ".avi";
    }

    private static bool HasOutputType(JsonElement item)
        => item.TryGetProperty("type", out var typeElement)
           && string.Equals(typeElement.GetString(), "output", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}

/// <summary>Compatibility name; all model data is resolved from the runtime catalog.</summary>
public sealed class MiniMaxH3VideoClient : IDisposable
{
    private readonly ComfyUiWorkflowVideoClient _inner;

    public MiniMaxH3VideoClient(VideoServiceConfiguration configuration, HttpClient? httpClient = null)
    {
        var root = AppPaths.Discover().ProjectRoot;
        var catalog = new VideoModelProfileCatalogStore(root, Path.Combine(root, "runtime", "video-model-profiles.json")).Load();
        _inner = new ComfyUiWorkflowVideoClient(root, catalog.Resolve(null) with { Service = configuration }, httpClient);
    }

    public object BuildGraph(VideoGenerationRequest request) => _inner.BuildGraph(request);
    public Task<VideoJob> SubmitAsync(VideoGenerationRequest request, CancellationToken cancellationToken = default)
        => _inner.SubmitAsync(request, cancellationToken);
    public Task<VideoJob> GetJobAsync(string promptId, CancellationToken cancellationToken = default)
        => _inner.GetJobAsync(promptId, cancellationToken);
    public Task CancelAsync(string promptId, CancellationToken cancellationToken = default)
        => _inner.CancelAsync(promptId, cancellationToken);
    public void Dispose() => _inner.Dispose();
}
