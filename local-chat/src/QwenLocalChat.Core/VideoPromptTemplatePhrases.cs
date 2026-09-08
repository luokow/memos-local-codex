using System.Text.Json.Serialization;

namespace QwenLocalChat.Core;

public sealed record VideoPromptPhraseEditor(
    string Key,
    string Label,
    string AutomationId,
    Func<VideoPromptTemplatePhrases, string> Read,
    Func<VideoPromptTemplatePhrases, string, VideoPromptTemplatePhrases> Write);

/// <summary>
/// User-editable sentences used when inserting official-style video prompt templates.
/// Empty fields fall back to <see cref="OfficialDefaults"/>. Placeholders: {id} {pic} {clauses}.
/// </summary>
public sealed record VideoPromptTemplatePhrases
{
    public static VideoPromptTemplatePhrases OfficialDefaults { get; } = new()
    {
        StyleLine = "The target video uses a cinematic live-action style.",
        Soundscape = "Quiet room tone and soft fabric movement.",
        Music = "N/A",
        SummaryTag = "[reference generation]",
        PersonDefinition = "{id} is the person {clauses}.",
        AppearanceClause = "whose face, body, hair, and clothing come only from {pic}",
        CharacterClause = "who is a distinct person whose face, body, hair, and clothing come only from {pic}",
        AppearanceSummary = "Keep {id}'s appearance locked to {pic}.",
        AppearanceRetention = "face, body, hair, and outfit stay locked to {pic}",
        AppearanceDetail = "{id} looks exactly like {pic}.",
        PoseClause = "{id} performs the pose and action shown in {pic}.",
        PoseSummary = "Transfer only the pose, blocking, relative positions, and camera from {pic}. Do not copy the face, body, or clothes from {pic}. Do not animate {pic} as-is.",
        PoseRetention = "pose, blocking, and camera are attribute_transfer from {pic}",
        PoseDetail = "The camera framing, character placement, and pose follow {pic}.",
        PoseReplaceDetail = "{id} occupies the corresponding role in that layout. Keep {pic}'s camera and relative positions. Do not keep the face or body identity of the person {id} is replacing.",
        PoseContinueDetail = "Then {id} continues that action naturally for the rest of the clip.",
        ClothingClause = "whose clothing and costume come only from {pic}",
        ClothingSummary = "Transfer outfit details from {pic} without copying that face.",
        ClothingRetention = "clothes are attribute_transfer from {pic}",
        ClothingDetail = "The wardrobe follows {pic}. Do not copy the face from {pic}.",
        StandalonePoseDefinition = "{id} is the spatial layout, relative positions, pose, and camera framing from {pic}.",
        StandalonePoseSummary = "Place and move the people using only the positions, action, and camera from {pic}. Do not copy identity from {pic}. Do not animate {pic} as-is.",
        StandalonePoseRetention = "{id} (appears in [Shot 1]): attribute_transfer - blocking, pose, and camera follow {pic}; do not preserve identity.",
        StandalonePoseDetail = "The camera framing, character placement, and pose follow {pic}. Ignore any other identity visible in {pic}.",
        SceneDefinition = "{id} is the environment, lighting, and background from {pic}.",
        SceneSummary = "Build the shot in the place shown by {pic}. Do not copy any person from {pic}.",
        SceneRetention = "{id} (appears in [Shot 1]): fully_preserved - setting and lighting follow {pic}.",
        SceneDetail = "Use the architecture, props, and light of {pic} only as the environment.",
        StandaloneClothingDefinition = "{id} is the clothing and costume only from {pic}.",
        StandaloneClothingRetention = "{id} (appears in [Shot 1]): attribute_transfer - clothes follow {pic}.",
        StyleDefinition = "{id} is the visual style, grade, and rendering from {pic}.",
        StyleSummary = "Borrow look and grade from {pic} as a weak style reference.",
        StyleRetention = "{id} (style and atmosphere): weak_reference - style cues follow {pic}.",
        StyleDetail = "Match the overall look of {pic} without copying its subjects.",
        WeakDefinition = "{id} is a weak visual cue from {pic}.",
        WeakSummary = "Treat {pic} as a weak reference only.",
        WeakRetention = "{id} (style and atmosphere): weak_reference - {pic} is a light cue, not identity.",
        WeakDetail = "Do not lock identity or pose to {pic}; use it only as a light cue.",
        VideoDefinition = "{vid} provides the reference for pose, action, cutting rhythm, and camera movement.",
        VideoOfSubjectDefinition = "{vid} provides the reference for the pose, action, and camera movement of {id}.",
        VideoSummary = "Follow the action, camera path, and pacing of {vid}. Do not treat {vid} as the first frame of a new clip. Do not copy identity from {vid}.",
        VideoRetention = "{vid} (camera and pacing structure): attribute_transfer - pose, action, and camera follow {vid}; do not preserve identity.",
        VideoDetail = "The camera movement, blocking, and action follow {vid}. Continue that motion as a new shot with the same rhythm.",
        VideoOfSubjectDetail = "{id} performs the pose and action shown in {vid}. Keep {vid}'s camera movement and relative positions. Do not keep the face or body identity from {vid}.",
        AudioDefinition = "{aud} provides the reference for voice timbre, pacing, or ambient texture.",
        AudioSummary = "Use {aud} only as a sound reference. Do not copy the raw waveform unless the prompt later asks for reuse.",
        AudioRetention = "{aud}: reference - timbre and rhythm follow {aud}.",
        AudioDetail = "The on-screen action fits the timing suggested by {aud}.",
        ExtraDescriptionPrefix = "镜头",
        ExtraSoundPrefix = "声音",
        ExtraMusicPrefix = "配乐",
        ExtraIdentityPrefix = "身份",
    };

    public static IReadOnlyList<VideoPromptPhraseEditor> Editors { get; } =
    [
        Field("extraDescriptionPrefix", "补充·画面前缀", p => p.ExtraDescriptionPrefix, (p, v) => p with { ExtraDescriptionPrefix = v }),
        Field("extraSoundPrefix", "补充·声音前缀", p => p.ExtraSoundPrefix, (p, v) => p with { ExtraSoundPrefix = v }),
        Field("extraMusicPrefix", "补充·配乐前缀", p => p.ExtraMusicPrefix, (p, v) => p with { ExtraMusicPrefix = v }),
        Field("extraIdentityPrefix", "补充·身份前缀", p => p.ExtraIdentityPrefix, (p, v) => p with { ExtraIdentityPrefix = v }),
        Field("style", "画面风格句", p => p.StyleLine, (p, v) => p with { StyleLine = v }),
        Field("soundscape", "环境声", p => p.Soundscape, (p, v) => p with { Soundscape = v }),
        Field("music", "配乐", p => p.Music, (p, v) => p with { Music = v }),
        Field("summaryTag", "任务标记", p => p.SummaryTag, (p, v) => p with { SummaryTag = v }),
        Field("personDefinition", "人物定义", p => p.PersonDefinition, (p, v) => p with { PersonDefinition = v }),
        Field("appearanceClause", "长相从句", p => p.AppearanceClause, (p, v) => p with { AppearanceClause = v }),
        Field("characterClause", "多人物从句", p => p.CharacterClause, (p, v) => p with { CharacterClause = v }),
        Field("appearanceSummary", "长相摘要", p => p.AppearanceSummary, (p, v) => p with { AppearanceSummary = v }),
        Field("appearanceRetention", "长相保留", p => p.AppearanceRetention, (p, v) => p with { AppearanceRetention = v }),
        Field("appearanceDetail", "长相画面", p => p.AppearanceDetail, (p, v) => p with { AppearanceDetail = v }),
        Field("poseClause", "姿势动作句", p => p.PoseClause, (p, v) => p with { PoseClause = v }),
        Field("poseSummary", "姿势摘要", p => p.PoseSummary, (p, v) => p with { PoseSummary = v }),
        Field("poseRetention", "姿势保留", p => p.PoseRetention, (p, v) => p with { PoseRetention = v }),
        Field("poseDetail", "姿势画面", p => p.PoseDetail, (p, v) => p with { PoseDetail = v }),
        Field("poseReplaceDetail", "姿势替换句", p => p.PoseReplaceDetail, (p, v) => p with { PoseReplaceDetail = v }),
        Field("poseContinueDetail", "姿势续写句", p => p.PoseContinueDetail, (p, v) => p with { PoseContinueDetail = v }),
        Field("clothingClause", "服装从句", p => p.ClothingClause, (p, v) => p with { ClothingClause = v }),
        Field("clothingSummary", "服装摘要", p => p.ClothingSummary, (p, v) => p with { ClothingSummary = v }),
        Field("clothingRetention", "服装保留", p => p.ClothingRetention, (p, v) => p with { ClothingRetention = v }),
        Field("clothingDetail", "服装画面", p => p.ClothingDetail, (p, v) => p with { ClothingDetail = v }),
        Field("standalonePoseDefinition", "单独姿势定义", p => p.StandalonePoseDefinition, (p, v) => p with { StandalonePoseDefinition = v }),
        Field("standalonePoseSummary", "单独姿势摘要", p => p.StandalonePoseSummary, (p, v) => p with { StandalonePoseSummary = v }),
        Field("standalonePoseRetention", "单独姿势保留", p => p.StandalonePoseRetention, (p, v) => p with { StandalonePoseRetention = v }),
        Field("standalonePoseDetail", "单独姿势画面", p => p.StandalonePoseDetail, (p, v) => p with { StandalonePoseDetail = v }),
        Field("sceneDefinition", "场景定义", p => p.SceneDefinition, (p, v) => p with { SceneDefinition = v }),
        Field("sceneSummary", "场景摘要", p => p.SceneSummary, (p, v) => p with { SceneSummary = v }),
        Field("sceneRetention", "场景保留", p => p.SceneRetention, (p, v) => p with { SceneRetention = v }),
        Field("sceneDetail", "场景画面", p => p.SceneDetail, (p, v) => p with { SceneDetail = v }),
        Field("styleDefinition", "风格定义", p => p.StyleDefinition, (p, v) => p with { StyleDefinition = v }),
        Field("styleSummary", "风格摘要", p => p.StyleSummary, (p, v) => p with { StyleSummary = v }),
        Field("styleRetention", "风格保留", p => p.StyleRetention, (p, v) => p with { StyleRetention = v }),
        Field("styleDetail", "风格画面", p => p.StyleDetail, (p, v) => p with { StyleDetail = v }),
        Field("weakDefinition", "弱参考定义", p => p.WeakDefinition, (p, v) => p with { WeakDefinition = v }),
        Field("weakSummary", "弱参考摘要", p => p.WeakSummary, (p, v) => p with { WeakSummary = v }),
        Field("weakRetention", "弱参考保留", p => p.WeakRetention, (p, v) => p with { WeakRetention = v }),
        Field("weakDetail", "弱参考画面", p => p.WeakDetail, (p, v) => p with { WeakDetail = v }),
        Field("videoDefinition", "参考视频定义", p => p.VideoDefinition, (p, v) => p with { VideoDefinition = v }),
        Field("videoOfSubjectDefinition", "主体跟视频定义", p => p.VideoOfSubjectDefinition, (p, v) => p with { VideoOfSubjectDefinition = v }),
        Field("videoSummary", "参考视频摘要", p => p.VideoSummary, (p, v) => p with { VideoSummary = v }),
        Field("videoRetention", "参考视频保留", p => p.VideoRetention, (p, v) => p with { VideoRetention = v }),
        Field("videoDetail", "参考视频画面", p => p.VideoDetail, (p, v) => p with { VideoDetail = v }),
        Field("videoOfSubjectDetail", "主体跟视频画面", p => p.VideoOfSubjectDetail, (p, v) => p with { VideoOfSubjectDetail = v }),
        Field("audioDefinition", "参考音频定义", p => p.AudioDefinition, (p, v) => p with { AudioDefinition = v }),
        Field("audioSummary", "参考音频摘要", p => p.AudioSummary, (p, v) => p with { AudioSummary = v }),
        Field("audioRetention", "参考音频保留", p => p.AudioRetention, (p, v) => p with { AudioRetention = v }),
        Field("audioDetail", "参考音频画面", p => p.AudioDetail, (p, v) => p with { AudioDetail = v }),
    ];

    [JsonPropertyName("styleLine")] public string StyleLine { get; init; } = "";
    [JsonPropertyName("soundscape")] public string Soundscape { get; init; } = "";
    [JsonPropertyName("music")] public string Music { get; init; } = "";
    [JsonPropertyName("summaryTag")] public string SummaryTag { get; init; } = "";
    [JsonPropertyName("personDefinition")] public string PersonDefinition { get; init; } = "";
    [JsonPropertyName("appearanceClause")] public string AppearanceClause { get; init; } = "";
    [JsonPropertyName("characterClause")] public string CharacterClause { get; init; } = "";
    [JsonPropertyName("appearanceSummary")] public string AppearanceSummary { get; init; } = "";
    [JsonPropertyName("appearanceRetention")] public string AppearanceRetention { get; init; } = "";
    [JsonPropertyName("appearanceDetail")] public string AppearanceDetail { get; init; } = "";
    [JsonPropertyName("poseClause")] public string PoseClause { get; init; } = "";
    [JsonPropertyName("poseSummary")] public string PoseSummary { get; init; } = "";
    [JsonPropertyName("poseRetention")] public string PoseRetention { get; init; } = "";
    [JsonPropertyName("poseDetail")] public string PoseDetail { get; init; } = "";
    [JsonPropertyName("poseReplaceDetail")] public string PoseReplaceDetail { get; init; } = "";
    [JsonPropertyName("poseContinueDetail")] public string PoseContinueDetail { get; init; } = "";
    [JsonPropertyName("clothingClause")] public string ClothingClause { get; init; } = "";
    [JsonPropertyName("clothingSummary")] public string ClothingSummary { get; init; } = "";
    [JsonPropertyName("clothingRetention")] public string ClothingRetention { get; init; } = "";
    [JsonPropertyName("clothingDetail")] public string ClothingDetail { get; init; } = "";
    [JsonPropertyName("standalonePoseDefinition")] public string StandalonePoseDefinition { get; init; } = "";
    [JsonPropertyName("standalonePoseSummary")] public string StandalonePoseSummary { get; init; } = "";
    [JsonPropertyName("standalonePoseRetention")] public string StandalonePoseRetention { get; init; } = "";
    [JsonPropertyName("standalonePoseDetail")] public string StandalonePoseDetail { get; init; } = "";
    [JsonPropertyName("sceneDefinition")] public string SceneDefinition { get; init; } = "";
    [JsonPropertyName("sceneSummary")] public string SceneSummary { get; init; } = "";
    [JsonPropertyName("sceneRetention")] public string SceneRetention { get; init; } = "";
    [JsonPropertyName("sceneDetail")] public string SceneDetail { get; init; } = "";
    [JsonPropertyName("standaloneClothingDefinition")] public string StandaloneClothingDefinition { get; init; } = "";
    [JsonPropertyName("standaloneClothingRetention")] public string StandaloneClothingRetention { get; init; } = "";
    [JsonPropertyName("styleDefinition")] public string StyleDefinition { get; init; } = "";
    [JsonPropertyName("styleSummary")] public string StyleSummary { get; init; } = "";
    [JsonPropertyName("styleRetention")] public string StyleRetention { get; init; } = "";
    [JsonPropertyName("styleDetail")] public string StyleDetail { get; init; } = "";
    [JsonPropertyName("weakDefinition")] public string WeakDefinition { get; init; } = "";
    [JsonPropertyName("weakSummary")] public string WeakSummary { get; init; } = "";
    [JsonPropertyName("weakRetention")] public string WeakRetention { get; init; } = "";
    [JsonPropertyName("weakDetail")] public string WeakDetail { get; init; } = "";
    [JsonPropertyName("videoDefinition")] public string VideoDefinition { get; init; } = "";
    [JsonPropertyName("videoOfSubjectDefinition")] public string VideoOfSubjectDefinition { get; init; } = "";
    [JsonPropertyName("videoSummary")] public string VideoSummary { get; init; } = "";
    [JsonPropertyName("videoRetention")] public string VideoRetention { get; init; } = "";
    [JsonPropertyName("videoDetail")] public string VideoDetail { get; init; } = "";
    [JsonPropertyName("videoOfSubjectDetail")] public string VideoOfSubjectDetail { get; init; } = "";
    [JsonPropertyName("audioDefinition")] public string AudioDefinition { get; init; } = "";
    [JsonPropertyName("audioSummary")] public string AudioSummary { get; init; } = "";
    [JsonPropertyName("audioRetention")] public string AudioRetention { get; init; } = "";
    [JsonPropertyName("audioDetail")] public string AudioDetail { get; init; } = "";
    [JsonPropertyName("extraDescriptionPrefix")] public string ExtraDescriptionPrefix { get; init; } = "";
    [JsonPropertyName("extraSoundPrefix")] public string ExtraSoundPrefix { get; init; } = "";
    [JsonPropertyName("extraMusicPrefix")] public string ExtraMusicPrefix { get; init; } = "";
    [JsonPropertyName("extraIdentityPrefix")] public string ExtraIdentityPrefix { get; init; } = "";

    public VideoPromptTemplatePhrases WithDefaults()
    {
        var filled = this;
        foreach (var editor in Editors)
        {
            if (string.IsNullOrWhiteSpace(editor.Read(filled)))
                filled = editor.Write(filled, editor.Read(OfficialDefaults));
        }
        if (string.IsNullOrWhiteSpace(filled.StandaloneClothingDefinition))
            filled = filled with { StandaloneClothingDefinition = OfficialDefaults.StandaloneClothingDefinition };
        if (string.IsNullOrWhiteSpace(filled.StandaloneClothingRetention))
            filled = filled with { StandaloneClothingRetention = OfficialDefaults.StandaloneClothingRetention };
        const string legacyPoseClause =
            "whose pose, spatial layout, relative positions, and camera framing come only from {pic}";
        if (string.Equals(filled.PoseClause.Trim(), legacyPoseClause, StringComparison.Ordinal))
            filled = filled with { PoseClause = OfficialDefaults.PoseClause };
        if (string.Equals(filled.StyleLine.Trim(), VideoPromptComposer.LegacyStyleLine, StringComparison.Ordinal)
            || string.Equals(filled.StyleLine.Trim(), "Cinematic live-action, medium shot.", StringComparison.Ordinal))
            filled = filled with { StyleLine = OfficialDefaults.StyleLine };
        return filled;
    }

    public IReadOnlyList<(string Slot, string Prefix)> ExtraPrefixSlots()
    {
        var copy = WithDefaults();
        return
        [
            ("description", NormalizePrefix(copy.ExtraDescriptionPrefix)),
            ("overall_soundscape", NormalizePrefix(copy.ExtraSoundPrefix)),
            ("non_diegetic_music", NormalizePrefix(copy.ExtraMusicPrefix)),
            ("subject_definitions", NormalizePrefix(copy.ExtraIdentityPrefix)),
        ];
    }

    public static string NormalizePrefix(string? prefix)
    {
        var text = (prefix ?? "").Trim();
        while (text.EndsWith(':') || text.EndsWith('：'))
            text = text[..^1].TrimEnd();
        return text;
    }

    public static string Apply(
        string template,
        string id = "",
        string pic = "",
        string clauses = "",
        string vid = "",
        string aud = "")
        => (template ?? "")
            .Replace("{id}", id, StringComparison.Ordinal)
            .Replace("{pic}", pic, StringComparison.Ordinal)
            .Replace("{clauses}", clauses, StringComparison.Ordinal)
            .Replace("{vid}", vid, StringComparison.Ordinal)
            .Replace("{aud}", aud, StringComparison.Ordinal);

    private static VideoPromptPhraseEditor Field(
        string key,
        string label,
        Func<VideoPromptTemplatePhrases, string> read,
        Func<VideoPromptTemplatePhrases, string, VideoPromptTemplatePhrases> write)
        => new(key, label, "VideoPromptPhrase" + char.ToUpperInvariant(key[0]) + key[1..] + "Setting", read, write);
}
