using System.Text.Json.Serialization;

namespace QwenLocalChat.Core;

/// <summary>
/// Which settings a text model can actually use. Missing cards keep the full form.
/// Ternary Bonsai packs are text-only and stay inside an 8K window.
/// </summary>
public sealed record TextModelSettingSupport
{
    public const int DefaultContextMinimum = 2_048;
    public const int DefaultContextMaximum = 32_768;

    [JsonPropertyName("reasoning")]
    public bool Reasoning { get; init; } = true;

    [JsonPropertyName("image_attachments")]
    public bool ImageAttachments { get; init; } = true;

    [JsonPropertyName("audio_attachments")]
    public bool AudioAttachments { get; init; } = true;

    [JsonPropertyName("video_attachments")]
    public bool VideoAttachments { get; init; } = true;

    [JsonPropertyName("context_minimum")]
    public int ContextMinimum { get; init; } = DefaultContextMinimum;

    [JsonPropertyName("context_maximum")]
    public int ContextMaximum { get; init; } = DefaultContextMaximum;

    public static TextModelSettingSupport Unrestricted { get; } = new();

    public static TextModelSettingSupport ForProfile(TextModelProfile profile)
    {
        var path = profile.Service.ModelPath ?? "";
        var alias = profile.Service.ModelAlias ?? "";
        var id = profile.Id ?? "";
        var bonsai = Contains(path, "Ternary-Bonsai")
            || Contains(alias, "bonsai")
            || Contains(id, "bonsai")
            || Contains(alias, "qwen3.8")
            || Contains(path, "Qwen3.8");
        if (!bonsai) return Unrestricted;
        return new()
        {
            Reasoning = true,
            ImageAttachments = false,
            AudioAttachments = false,
            VideoAttachments = false,
            ContextMinimum = DefaultContextMinimum,
            ContextMaximum = 8_192,
        };
    }

    public TextModelSettingSupport Normalized()
    {
        var minimum = Math.Clamp(ContextMinimum, DefaultContextMinimum, DefaultContextMaximum);
        var maximum = Math.Clamp(ContextMaximum, DefaultContextMinimum, DefaultContextMaximum);
        if (minimum > maximum) (minimum, maximum) = (maximum, minimum);
        return this with
        {
            ContextMinimum = minimum,
            ContextMaximum = maximum,
        };
    }

    private static bool Contains(string value, string fragment)
        => value.Contains(fragment, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Generation and attachment settings saved for one text-model profile.
/// Startup parameters stay on the profile service.
/// </summary>
public sealed record TextModelSettingsCard
{
    [JsonPropertyName("max_output_tokens")]
    public int MaxOutputTokens { get; init; } = 4_096;

    [JsonPropertyName("temperature")]
    public double Temperature { get; init; } = 0.7;

    [JsonPropertyName("frequency_penalty")]
    public double FrequencyPenalty { get; init; } = LocalChatSettings.DefaultFrequencyPenalty;

    [JsonPropertyName("presence_penalty")]
    public double PresencePenalty { get; init; } = LocalChatSettings.DefaultPresencePenalty;

    [JsonPropertyName("repeat_penalty")]
    public double RepeatPenalty { get; init; } = LocalChatSettings.DefaultRepeatPenalty;

    [JsonPropertyName("repeat_last_n")]
    public int RepeatLastN { get; init; } = LocalChatSettings.DefaultRepeatLastN;

    [JsonPropertyName("dry_multiplier")]
    public double DryMultiplier { get; init; } = LocalChatSettings.DefaultDryMultiplier;

    [JsonPropertyName("stream_responses")]
    public bool StreamResponses { get; init; } = true;

    [JsonPropertyName("auto_tighten_output_tokens")]
    public bool AutoTightenOutputTokens { get; init; }

    [JsonPropertyName("segmented_long_form")]
    public bool SegmentedLongForm { get; init; }

    [JsonPropertyName("client_repetition_guard")]
    public bool ClientRepetitionGuard { get; init; }

    [JsonPropertyName("request_timeout_seconds")]
    public int RequestTimeoutSeconds { get; init; } = 900;

    [JsonPropertyName("max_history_rounds")]
    public int MaxHistoryRounds { get; init; } = 40;

    [JsonPropertyName("chat_attachments")]
    public ChatAttachmentPolicy ChatAttachments { get; init; } = ChatAttachmentPolicy.SafeDefaults;

    [JsonPropertyName("support")]
    public TextModelSettingSupport Support { get; init; } = TextModelSettingSupport.Unrestricted;

    public static TextModelSettingsCard FromSettings(LocalChatSettings settings, TextModelProfile profile)
        => new()
        {
            MaxOutputTokens = settings.MaxOutputTokens,
            Temperature = settings.Temperature,
            FrequencyPenalty = settings.FrequencyPenalty,
            PresencePenalty = settings.PresencePenalty,
            RepeatPenalty = settings.RepeatPenalty,
            RepeatLastN = settings.RepeatLastN,
            DryMultiplier = settings.DryMultiplier,
            StreamResponses = settings.StreamResponses,
            AutoTightenOutputTokens = settings.AutoTightenOutputTokens,
            SegmentedLongForm = settings.SegmentedLongForm,
            ClientRepetitionGuard = settings.ClientRepetitionGuard,
            RequestTimeoutSeconds = settings.RequestTimeoutSeconds,
            MaxHistoryRounds = settings.MaxHistoryRounds,
            ChatAttachments = settings.ChatAttachments ?? ChatAttachmentPolicy.SafeDefaults,
            Support = TextModelSettingSupport.ForProfile(profile),
        };

    public TextModelSettingsCard Normalized()
        => this with
        {
            Temperature = SettingsNumberInput.Temperature(Temperature),
            FrequencyPenalty = SettingsNumberInput.OpenAiPenalty(FrequencyPenalty),
            PresencePenalty = SettingsNumberInput.OpenAiPenalty(PresencePenalty),
            RepeatPenalty = SettingsNumberInput.RepeatPenalty(RepeatPenalty),
            DryMultiplier = SettingsNumberInput.DryMultiplier(DryMultiplier),
            ChatAttachments = (ChatAttachments ?? ChatAttachmentPolicy.SafeDefaults).Normalized(),
            Support = (Support ?? TextModelSettingSupport.Unrestricted).Normalized(),
        };

    public ChatAttachmentPolicy EffectiveAttachments()
    {
        var attachments = (ChatAttachments ?? ChatAttachmentPolicy.SafeDefaults).Normalized();
        var support = (Support ?? TextModelSettingSupport.Unrestricted).Normalized();
        return attachments with
        {
            AllowImage = support.ImageAttachments && attachments.AllowImage,
            AllowAudio = support.AudioAttachments && attachments.AllowAudio,
            AllowVideo = support.VideoAttachments && attachments.AllowVideo,
        };
    }

    public LocalChatSettings ApplyTo(LocalChatSettings settings)
    {
        var card = Normalized();
        return settings with
        {
            MaxOutputTokens = card.MaxOutputTokens,
            Temperature = card.Temperature,
            FrequencyPenalty = card.FrequencyPenalty,
            PresencePenalty = card.PresencePenalty,
            RepeatPenalty = card.RepeatPenalty,
            RepeatLastN = card.RepeatLastN,
            DryMultiplier = card.DryMultiplier,
            StreamResponses = card.StreamResponses,
            AutoTightenOutputTokens = card.AutoTightenOutputTokens,
            SegmentedLongForm = card.SegmentedLongForm,
            ClientRepetitionGuard = card.ClientRepetitionGuard,
            RequestTimeoutSeconds = card.RequestTimeoutSeconds,
            MaxHistoryRounds = card.MaxHistoryRounds,
            ChatAttachments = card.EffectiveAttachments(),
        };
    }
}

public static class ModelSettingsCards
{
    public static Dictionary<string, TextModelSettingsCard> EnsureText(
        LocalChatSettings settings,
        ModelProfileCatalog catalog)
    {
        var cards = new Dictionary<string, TextModelSettingsCard>(StringComparer.Ordinal);
        foreach (var profile in catalog.Profiles)
        {
            if (settings.TextModelCards is not null
                && settings.TextModelCards.TryGetValue(profile.Id, out var saved))
                cards[profile.Id] = saved.Normalized();
            else
                cards[profile.Id] = TextModelSettingsCard.FromSettings(settings, profile).Normalized();
        }
        return cards;
    }

    public static Dictionary<string, VideoGenerationSettings> EnsureVideo(
        LocalChatSettings settings,
        VideoModelProfileCatalog catalog)
    {
        var fallback = (settings.VideoGeneration ?? VideoGenerationSettings.SafeDefaults).Normalized();
        var cards = new Dictionary<string, VideoGenerationSettings>(StringComparer.Ordinal);
        foreach (var profile in catalog.Profiles)
        {
            if (settings.VideoModelCards is not null
                && settings.VideoModelCards.TryGetValue(profile.Id, out var saved))
                cards[profile.Id] = saved.Normalized();
            else
                cards[profile.Id] = fallback;
        }
        return cards;
    }

    public static Dictionary<string, TextModelSettingsCard>? NormalizeText(
        IReadOnlyDictionary<string, TextModelSettingsCard>? cards)
    {
        if (cards is null || cards.Count == 0) return null;
        return cards.ToDictionary(pair => pair.Key, pair => pair.Value.Normalized(), StringComparer.Ordinal);
    }

    public static Dictionary<string, VideoGenerationSettings>? NormalizeVideo(
        IReadOnlyDictionary<string, VideoGenerationSettings>? cards)
    {
        if (cards is null || cards.Count == 0) return null;
        return cards.ToDictionary(pair => pair.Key, pair => pair.Value.Normalized(), StringComparer.Ordinal);
    }

    public static bool SameText(
        IReadOnlyDictionary<string, TextModelSettingsCard>? left,
        IReadOnlyDictionary<string, TextModelSettingsCard>? right)
        => Same(NormalizeText(left), NormalizeText(right), static (card, other) => card == other);

    public static bool SameVideo(
        IReadOnlyDictionary<string, VideoGenerationSettings>? left,
        IReadOnlyDictionary<string, VideoGenerationSettings>? right)
        => Same(NormalizeVideo(left), NormalizeVideo(right), static (card, other) => card == other);

    public static bool UnselectedChanged(LocalChatSettings left, LocalChatSettings right)
    {
        if (!string.Equals(left.SelectedTextProfileId, right.SelectedTextProfileId, StringComparison.Ordinal)
            && !SameText(left.TextModelCards, right.TextModelCards))
            return true;
        if (!string.Equals(left.SelectedVideoProfileId, right.SelectedVideoProfileId, StringComparison.Ordinal)
            && !SameVideo(left.VideoModelCards, right.VideoModelCards))
            return true;
        if (string.Equals(left.SelectedTextProfileId, right.SelectedTextProfileId, StringComparison.Ordinal)
            && !SameText(left.TextModelCards, right.TextModelCards)
            && Card<TextModelSettingsCard>(left.TextModelCards, left.SelectedTextProfileId)
                == Card<TextModelSettingsCard>(right.TextModelCards, right.SelectedTextProfileId))
            return true;
        if (string.Equals(left.SelectedVideoProfileId, right.SelectedVideoProfileId, StringComparison.Ordinal)
            && !SameVideo(left.VideoModelCards, right.VideoModelCards)
            && Card<VideoGenerationSettings>(left.VideoModelCards, left.SelectedVideoProfileId)
                == Card<VideoGenerationSettings>(right.VideoModelCards, right.SelectedVideoProfileId))
            return true;
        return false;
    }

    public static LocalChatSettings ApplySelected(LocalChatSettings settings)
    {
        var next = settings;
        if (!string.IsNullOrWhiteSpace(settings.SelectedTextProfileId)
            && settings.TextModelCards is not null
            && settings.TextModelCards.TryGetValue(settings.SelectedTextProfileId, out var textCard))
            next = textCard.ApplyTo(next);
        if (!string.IsNullOrWhiteSpace(settings.SelectedVideoProfileId)
            && settings.VideoModelCards is not null
            && settings.VideoModelCards.TryGetValue(settings.SelectedVideoProfileId, out var videoCard))
            next = next with { VideoGeneration = videoCard.Normalized() };
        return next;
    }

    private static T? Card<T>(IReadOnlyDictionary<string, T>? cards, string? id)
        where T : class
    {
        if (cards is null || string.IsNullOrWhiteSpace(id) || !cards.TryGetValue(id, out var card))
            return null;
        return card;
    }

    private static bool Same<T>(
        IReadOnlyDictionary<string, T>? left,
        IReadOnlyDictionary<string, T>? right,
        Func<T, T, bool> equal)
    {
        var leftEmpty = left is null || left.Count == 0;
        var rightEmpty = right is null || right.Count == 0;
        if (leftEmpty || rightEmpty) return leftEmpty && rightEmpty;
        if (left!.Count != right!.Count) return false;
        foreach (var pair in left)
        {
            if (!right.TryGetValue(pair.Key, out var other) || !equal(pair.Value, other))
                return false;
        }
        return true;
    }
}
