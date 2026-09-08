using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace QwenLocalChat.Core;

/// <summary>
/// Generation knobs sent to llama.cpp OpenAI-compatible chat completions.
/// Anti-repetition defaults target long free-form Chinese text (paragraph loops),
/// where server defaults (repeat_penalty=1.0, dry off, last_n=64) are too weak.
/// </summary>
public sealed record ChatGenerationOptions(
    string ModelAlias,
    double Temperature,
    int MaxOutputTokens,
    bool Stream,
    TimeSpan RequestTimeout,
    double FrequencyPenalty = LocalChatSettings.DefaultFrequencyPenalty,
    double PresencePenalty = LocalChatSettings.DefaultPresencePenalty,
    double RepeatPenalty = LocalChatSettings.DefaultRepeatPenalty,
    int RepeatLastN = LocalChatSettings.DefaultRepeatLastN,
    double DryMultiplier = LocalChatSettings.DefaultDryMultiplier,
    /// <summary>
    /// When set, overrides per-request thinking via chat_template_kwargs.enable_thinking.
    /// null keeps the server / --reasoning default. Continue-generation passes false so
    /// the budget is not spent entirely on reasoning_content with an empty body.
    /// </summary>
    bool? EnableThinking = null,
    /// <summary>
    /// Optional thinking token cap (llama.cpp). null = server default; 0 = no thinking budget field.
    /// </summary>
    int? ReasoningBudget = null,
    /// <summary>
    /// When true, trim trailing loops/slogan tails and may abort stream early on loops.
    /// Default false — keep model text intact unless user opts in.
    /// </summary>
    bool ClientRepetitionGuard = false,
    /// <summary>llama.cpp slot pin. null lets the server pick.</summary>
    int? SlotId = null,
    /// <summary>
    /// llama.cpp prompt-cache reuse. false after switching Local AI threads so
    /// the previous conversation's KV is not treated as a prefix of the new one.
    /// </summary>
    bool? CachePrompt = null)
{
    public static ChatGenerationOptions FromSettings(
        LocalChatSettings settings,
        string modelAlias,
        int maxOutputTokens,
        bool? enableThinking = null,
        bool reasoningEnabled = false)
    {
        // Silently extend timeout if the user set max_tokens high but left a short timeout —
        // avoids mid-stream aborts without pushing noisy UI.
        var timeoutSec = Math.Max(
            settings.RequestTimeoutSeconds,
            ContextBudget.SuggestMinTimeoutSeconds(maxOutputTokens));

        int? reasoningBudget = null;
        if (enableThinking is false)
            reasoningBudget = null;
        else if (reasoningEnabled || enableThinking is true)
            reasoningBudget = ContextBudget.SuggestReasoningBudget(maxOutputTokens);

        return new(
            ModelAlias: modelAlias,
            Temperature: settings.Temperature,
            MaxOutputTokens: maxOutputTokens,
            Stream: settings.StreamResponses,
            RequestTimeout: TimeSpan.FromSeconds(timeoutSec),
            FrequencyPenalty: settings.FrequencyPenalty,
            PresencePenalty: settings.PresencePenalty,
            RepeatPenalty: settings.RepeatPenalty,
            RepeatLastN: settings.RepeatLastN,
            DryMultiplier: settings.DryMultiplier,
            EnableThinking: enableThinking,
            ReasoningBudget: reasoningBudget,
            ClientRepetitionGuard: settings.ClientRepetitionGuard);
    }

    public static ChatGenerationOptions FromSettings(
        LocalChatSettings settings,
        ModelProfileCatalog catalog,
        int maxOutputTokens,
        bool? enableThinking = null)
    {
        var profile = settings.ResolveTextProfile(catalog);
        return FromSettings(settings, profile.Service.ModelAlias, maxOutputTokens, enableThinking, profile.Service.ReasoningEnabled);
    }

    /// <summary>JSON body shared by streaming and non-streaming completions.</summary>
    public object ToRequestBody(IReadOnlyList<ChatMessage> messages)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = ModelAlias,
            ["messages"] = messages.Select(message => new { role = message.Role, content = message.Content }).ToArray(),
            ["temperature"] = Temperature,
            ["max_tokens"] = MaxOutputTokens,
            ["stream"] = Stream,
            ["frequency_penalty"] = FrequencyPenalty,
            ["presence_penalty"] = PresencePenalty,
            // llama.cpp extensions: short-window token repeat + long-range DRY n-gram penalty.
            ["repeat_penalty"] = RepeatPenalty,
            ["repeat_last_n"] = RepeatLastN,
            ["dry_multiplier"] = DryMultiplier,
            ["dry_base"] = LocalChatSettings.DefaultDryBase,
            ["dry_allowed_length"] = LocalChatSettings.DefaultDryAllowedLength,
            // -1 = whole generation (needed for paragraph loops beyond short windows).
            ["dry_penalty_last_n"] = LocalChatSettings.DefaultDryPenaltyLastN,
        };
        if (Stream)
            payload["stream_options"] = new { include_usage = true };
        // Qwen3 / llama.cpp: thinking tokens arrive as reasoning_content, not content.
        // Forcing enable_thinking=false on continue avoids emptying the whole max_tokens budget.
        if (EnableThinking is { } enableThinking)
            payload["chat_template_kwargs"] = new { enable_thinking = enableThinking };
        // Cap thinking so long free-form answers still get body tokens (server may ignore unknown fields).
        if (ReasoningBudget is > 0)
            payload["reasoning_budget"] = ReasoningBudget.Value;
        if (SlotId is int slotId)
            payload["id_slot"] = slotId;
        if (CachePrompt is bool cachePrompt)
            payload["cache_prompt"] = cachePrompt;
        return payload;
    }
}

public sealed record ChatCompletionResult(
    string Content,
    string? FinishReason,
    int? PromptTokens,
    int? CompletionTokens,
    int ReasoningChars = 0);

public sealed class QwenChatClient(Uri endpoint, HttpClient? httpClient = null) : ILocalChatClient
{
    private readonly HttpClient _http = httpClient ?? new HttpClient(new SocketsHttpHandler { UseProxy = false });

    public async Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default)
        => (await CompleteWithDetailsAsync(
            messages,
            new ChatGenerationOptions(LocalChatSettings.DefaultModelAlias, 0.7, 4_096, false, TimeSpan.FromMinutes(15)),
            cancellationToken)).Content;

    public async Task<ChatCompletionResult> CompleteWithDetailsAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatGenerationOptions options,
        CancellationToken cancellationToken = default)
    {
        if (options.Stream) throw new NotSupportedException("流式响应必须通过 CompleteStreamingAsync 调用");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.RequestTimeout);
        using var response = await _http.PostAsJsonAsync(endpoint, options.ToRequestBody(messages), timeout.Token);
        var raw = await response.Content.ReadAsStringAsync(timeout.Token);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"文本模型返回 HTTP {(int)response.StatusCode}: {raw}");

        using var document = JsonDocument.Parse(raw);
        var choice = document.RootElement.GetProperty("choices")[0];
        var message = choice.GetProperty("message");
        var content = message.TryGetProperty("content", out var contentElement)
            && contentElement.ValueKind == JsonValueKind.String
            ? contentElement.GetString()?.Trim()
            : null;
        var reasoningChars = 0;
        if (message.TryGetProperty("reasoning_content", out var reasoningElement)
            && reasoningElement.ValueKind == JsonValueKind.String)
            reasoningChars = reasoningElement.GetString()?.Length ?? 0;

        if (string.IsNullOrWhiteSpace(content))
            throw EmptyAnswerException(reasoningChars, choice);

        if (options.ClientRepetitionGuard)
        {
            content = GenerationRepetitionGuard.TrimTrailingLoops(content).Text;
            if (string.IsNullOrWhiteSpace(content))
                throw EmptyAnswerException(reasoningChars, choice);
        }

        var finishReason = choice.TryGetProperty("finish_reason", out var finishElement)
            ? finishElement.GetString()
            : null;
        int? promptTokens = null;
        int? completionTokens = null;
        if (document.RootElement.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("prompt_tokens", out var prompt)) promptTokens = prompt.GetInt32();
            if (usage.TryGetProperty("completion_tokens", out var completion)) completionTokens = completion.GetInt32();
        }
        return new ChatCompletionResult(content, finishReason, promptTokens, completionTokens, reasoningChars);
    }

    public async Task<ChatCompletionResult> CompleteStreamingAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatGenerationOptions options,
        Action<string>? onDelta = null,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.RequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(options.ToRequestBody(messages)),
        };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(timeout.Token);
            throw new HttpRequestException($"文本模型返回 HTTP {(int)response.StatusCode}: {error}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var content = new StringBuilder();
        var reasoningChars = 0;
        string? finishReason = null;
        int? promptTokens = null;
        int? completionTokens = null;
        var stoppedForLoop = false;
        try
        {
            while (await reader.ReadLineAsync(timeout.Token) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                var data = line[5..].TrimStart();
                if (data.Length == 0 || data == "[DONE]") continue;
                using var document = JsonDocument.Parse(data);
                if (document.RootElement.TryGetProperty("choices", out var choices)
                    && choices.GetArrayLength() > 0)
                {
                    var choice = choices[0];
                    if (choice.TryGetProperty("delta", out var delta))
                    {
                        // Visible answer only — reasoning_content is internal thinking.
                        if (delta.TryGetProperty("content", out var deltaContent)
                            && deltaContent.ValueKind == JsonValueKind.String)
                        {
                            var visible = deltaContent.GetString();
                            if (!string.IsNullOrEmpty(visible))
                            {
                                content.Append(visible);
                                onDelta?.Invoke(visible);
                                // Opt-in only: false positives used to cancel normal long Chinese mid-story.
                                if (options.ClientRepetitionGuard
                                    && GenerationRepetitionGuard.ShouldStopStreaming(content.ToString()))
                                {
                                    stoppedForLoop = true;
                                    finishReason = "length";
                                    break;
                                }
                            }
                        }
                        if (delta.TryGetProperty("reasoning_content", out var deltaReasoning)
                            && deltaReasoning.ValueKind == JsonValueKind.String)
                        {
                            reasoningChars += deltaReasoning.GetString()?.Length ?? 0;
                        }
                    }
                    if (choice.TryGetProperty("finish_reason", out var finish)
                        && finish.ValueKind == JsonValueKind.String)
                        finishReason = finish.GetString();
                }
                if (document.RootElement.TryGetProperty("usage", out var usage)
                    && usage.ValueKind == JsonValueKind.Object)
                {
                    if (usage.TryGetProperty("prompt_tokens", out var prompt)) promptTokens = prompt.GetInt32();
                    if (usage.TryGetProperty("completion_tokens", out var completion)) completionTokens = completion.GetInt32();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // User-initiated stop: keep any tokens already streamed.
            var partial = content.ToString();
            if (options.ClientRepetitionGuard)
                partial = GenerationRepetitionGuard.TrimTrailingLoops(partial).Text;
            else
                partial = partial.TrimEnd();
            if (partial.Length > 0)
                return new ChatCompletionResult(partial, "cancelled", promptTokens, completionTokens, reasoningChars);
            throw;
        }

        var visibleContent = content.ToString();
        if (options.ClientRepetitionGuard)
            visibleContent = GenerationRepetitionGuard.TrimTrailingLoops(visibleContent).Text;
        else
            visibleContent = visibleContent.TrimEnd();
        if (visibleContent.Length == 0)
            throw EmptyAnswerException(reasoningChars, finishReason);
        if (stoppedForLoop)
            finishReason = "length";
        return new ChatCompletionResult(visibleContent, finishReason, promptTokens, completionTokens, reasoningChars);
    }

    private static InvalidOperationException EmptyAnswerException(int reasoningChars, JsonElement choice)
    {
        var finish = choice.TryGetProperty("finish_reason", out var finishElement)
            ? finishElement.GetString()
            : null;
        return EmptyAnswerException(reasoningChars, finish);
    }

    private static InvalidOperationException EmptyAnswerException(int reasoningChars, string? finishReason)
    {
        if (reasoningChars > 0)
        {
            var lengthHint = string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase)
                ? "本轮输出额度已在思考阶段用尽，"
                : "模型只产生了思考内容、没有正文，";
            return new InvalidOperationException(
                lengthHint
                + "因此回答为空。可关闭「思考模式」后点「继续」，或提高最大输出 Token 后再试。");
        }

        return new InvalidOperationException("文本模型返回了空回答");
    }

    public void Dispose() => _http.Dispose();
}
