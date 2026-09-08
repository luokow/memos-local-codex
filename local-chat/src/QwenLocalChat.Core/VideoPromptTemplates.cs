namespace QwenLocalChat.Core;

public enum VideoPromptTemplateKind
{
    CustomPictureDuties,
    AppearanceAndPose,
    AppearanceAndScene,
    AppearancePoseScene,
    AppearancePoseSceneClothes,
    AppearancePoseAndWeakRest,
    Characters,
    FirstFrameLock,
    FirstLastFrame,
    LastFrameLock,
    AppearanceAndVideo,
    Videos,
    Audios,
    TextToVideo,
}

public enum VideoPromptPictureDuty
{
    Appearance,
    Pose,
    Scene,
    Character,
    Clothing,
    Style,
    WeakReference,
    Unused,
}

public sealed record VideoPromptMediaChoice(string Kind, int Index, string FileName)
{
    public string Display
    {
        get
        {
            var label = Kind switch
            {
                "Picture" => $"图{Index}",
                "Video" => $"视频{Index}",
                "Audio" => $"音频{Index}",
                _ => $"{Kind} {Index}",
            };
            return string.IsNullOrWhiteSpace(FileName) ? label : $"{label}  {FileName}";
        }
    }

    public string Key => $"{Kind}:{Index}";
}

public sealed record VideoPromptTemplateRole(string Key, string Label, string MediaKind);

public sealed record VideoPromptDutyChoice(VideoPromptPictureDuty Duty, string Label);

public sealed record VideoPromptTemplateSpec(
    VideoPromptTemplateKind Kind,
    string Title,
    string Description,
    IReadOnlyList<VideoPromptTemplateRole> Roles,
    int Size = 0,
    int MinSize = 0,
    int MaxSize = 0);

public sealed record VideoPromptTemplateInsert(string Title, string Text);

/// <summary>
/// Official-style prompt blocks. Catalog is sized to the H3 node ceiling
/// (9 pictures / 3 videos / 3 audios). Callers assign each role to uploaded media.
/// </summary>
public static class VideoPromptTemplates
{
    public const int NodeMaxPictures = 9;
    public const int NodeMaxVideos = 3;
    public const int NodeMaxAudios = 3;

    public static IReadOnlyList<VideoPromptDutyChoice> PictureDuties { get; } =
    [
        new(VideoPromptPictureDuty.Appearance, "长相"),
        new(VideoPromptPictureDuty.Pose, "姿势"),
        new(VideoPromptPictureDuty.Scene, "场景"),
        new(VideoPromptPictureDuty.Character, "人物"),
        new(VideoPromptPictureDuty.Clothing, "服装"),
        new(VideoPromptPictureDuty.Style, "风格"),
        new(VideoPromptPictureDuty.WeakReference, "弱参考"),
        new(VideoPromptPictureDuty.Unused, "不使用"),
    ];

    public static readonly VideoPromptTemplateSpec AppearanceAndPoseSpec = new(
        VideoPromptTemplateKind.AppearanceAndPose,
        "长相 + 姿势",
        "一张管脸和衣服，另一张只管站位、动作和机位；姿势图里的人不会出镜。",
        [
            new("appearance", "长相", "Picture"),
            new("pose", "姿势", "Picture"),
        ]);

    public static readonly VideoPromptTemplateSpec AppearanceAndSceneSpec = new(
        VideoPromptTemplateKind.AppearanceAndScene,
        "人物 + 场景",
        "一张管人物外貌，另一张只管环境和背景。",
        [
            new("appearance", "人物", "Picture"),
            new("scene", "场景", "Picture"),
        ]);

    public static readonly VideoPromptTemplateSpec AppearancePoseSceneSpec = new(
        VideoPromptTemplateKind.AppearancePoseScene,
        "长相 + 姿势 + 场景",
        "三张图拆成脸、动作、环境。",
        [
            new("appearance", "长相", "Picture"),
            new("pose", "姿势", "Picture"),
            new("scene", "场景", "Picture"),
        ]);

    public static readonly VideoPromptTemplateSpec AppearancePoseSceneClothesSpec = new(
        VideoPromptTemplateKind.AppearancePoseSceneClothes,
        "长相 + 姿势 + 场景 + 服装",
        "四张图拆成脸、动作、环境、衣服。",
        [
            new("appearance", "长相", "Picture"),
            new("pose", "姿势", "Picture"),
            new("scene", "场景", "Picture"),
            new("clothing", "服装", "Picture"),
        ]);

    public static readonly VideoPromptTemplateSpec TextToVideoSpec = new(
        VideoPromptTemplateKind.TextToVideo,
        "文生视频",
        "没有参考图。用四格写画面、运镜、声音和配乐。",
        []);

    public static readonly VideoPromptTemplateSpec FirstFrameLockSpec = new(
        VideoPromptTemplateKind.FirstFrameLock,
        "首帧对齐",
        "把这张图钉成视频第 0 秒的真实开场。",
        [new("first", "首帧", "Picture")]);

    public static readonly VideoPromptTemplateSpec FirstLastFrameSpec = new(
        VideoPromptTemplateKind.FirstLastFrame,
        "首帧 + 尾帧",
        "一张对齐开头，一张对齐结尾，正文写中间过程。Comfy 固定 <Picture 1> = 首帧、<Picture 2> = 尾帧。",
        [
            new("first", "首帧", "Picture"),
            new("last", "尾帧", "Picture"),
        ]);

    public static readonly VideoPromptTemplateSpec LastFrameLockSpec = new(
        VideoPromptTemplateKind.LastFrameLock,
        "尾帧对齐",
        "把这张图钉成视频结尾的真实落点。Comfy 把唯一尾帧标成 <Picture 1>。",
        [new("last", "尾帧", "Picture")]);

    public static readonly VideoPromptTemplateSpec AppearanceAndVideoSpec = new(
        VideoPromptTemplateKind.AppearanceAndVideo,
        "长相 + 参考视频",
        "图管脸和衣服，视频只管动作、姿势和运镜；视频里的人不会出镜。",
        [
            new("appearance", "长相", "Picture"),
            new("video", "动作视频", "Video"),
        ]);

    public static VideoPromptTemplateSpec CustomPictureDutiesSpec(int pictureCount)
    {
        var n = Math.Clamp(pictureCount, 1, NodeMaxPictures);
        return new(
            VideoPromptTemplateKind.CustomPictureDuties,
            "按图分工",
            $"给每张已上传的参考图选职责，最多 {NodeMaxPictures} 张。同一张图只承担一个任务。",
            [],
            n);
    }

    public static VideoPromptTemplateSpec CharactersCatalogSpec { get; } = new(
        VideoPromptTemplateKind.Characters,
        "多人物",
        $"2 到 {NodeMaxPictures} 个人物，每人一张参考图，不要换脸。",
        [],
        NodeMaxPictures,
        2,
        NodeMaxPictures);

    public static VideoPromptTemplateSpec VideosCatalogSpec { get; } = new(
        VideoPromptTemplateKind.Videos,
        "参考视频",
        $"1 到 {NodeMaxVideos} 段参考视频，跟动作、运镜和节奏。",
        [],
        NodeMaxVideos,
        1,
        NodeMaxVideos);

    public static VideoPromptTemplateSpec AudiosCatalogSpec { get; } = new(
        VideoPromptTemplateKind.Audios,
        "参考音频",
        $"1 到 {NodeMaxAudios} 段参考音频，只借音色、节奏或环境声。",
        [],
        NodeMaxAudios,
        1,
        NodeMaxAudios);

    public static VideoPromptTemplateSpec CharactersSpec(int count)
    {
        var n = Math.Clamp(count, 2, NodeMaxPictures);
        return new(
            VideoPromptTemplateKind.Characters,
            "多人物",
            $"2 到 {NodeMaxPictures} 个人物，每人一张参考图，不要换脸。",
            Enumerable.Range(1, n)
                .Select(i => new VideoPromptTemplateRole($"character{i}", $"人物 {i}", "Picture"))
                .ToArray(),
            n,
            2,
            NodeMaxPictures);
    }

    public static VideoPromptTemplateSpec AppearancePoseAndWeakRestSpec(int pictureCount)
    {
        var n = Math.Clamp(pictureCount, 3, NodeMaxPictures);
        var roles = new List<VideoPromptTemplateRole>
        {
            new("appearance", "长相", "Picture"),
            new("pose", "姿势", "Picture"),
        };
        for (var i = 3; i <= n; i++)
            roles.Add(new($"weak{i}", $"弱参考 {i - 2}", "Picture"));
        return new(
            VideoPromptTemplateKind.AppearancePoseAndWeakRest,
            "长相 + 姿势 + 其余弱参考",
            $"前两张拆脸和动作，其余最多到第 {NodeMaxPictures} 张只作弱参考。",
            roles,
            n);
    }

    public static VideoPromptTemplateSpec VideosSpec(int count)
    {
        var n = Math.Clamp(count, 1, NodeMaxVideos);
        return new(
            VideoPromptTemplateKind.Videos,
            "参考视频",
            $"1 到 {NodeMaxVideos} 段参考视频，跟动作、运镜和节奏。",
            Enumerable.Range(1, n)
                .Select(i => new VideoPromptTemplateRole($"video{i}", n == 1 ? "参考视频" : $"视频 {i}", "Video"))
                .ToArray(),
            n,
            1,
            NodeMaxVideos);
    }

    public static VideoPromptTemplateSpec AudiosSpec(int count)
    {
        var n = Math.Clamp(count, 1, NodeMaxAudios);
        return new(
            VideoPromptTemplateKind.Audios,
            "参考音频",
            $"1 到 {NodeMaxAudios} 段参考音频，只借音色、节奏或环境声。",
            Enumerable.Range(1, n)
                .Select(i => new VideoPromptTemplateRole($"audio{i}", n == 1 ? "参考音频" : $"音频 {i}", "Audio"))
                .ToArray(),
            n,
            1,
            NodeMaxAudios);
    }

    public static IReadOnlyList<VideoPromptTemplateSpec> AvailableFor(
        VideoConditioningMode mode,
        int imageCount,
        int videoCount,
        int audioCount)
    {
        var images = Math.Clamp(imageCount, 0, NodeMaxPictures);
        var videos = Math.Clamp(videoCount, 0, NodeMaxVideos);
        var audios = Math.Clamp(audioCount, 0, NodeMaxAudios);
        var list = new List<VideoPromptTemplateSpec>();
        switch (mode)
        {
            case VideoConditioningMode.Text:
                list.Add(TextToVideoSpec);
                break;
            case VideoConditioningMode.SingleReferenceImage:
            case VideoConditioningMode.ReferenceVideo:
            case VideoConditioningMode.ReferenceAudio:
                if (images >= 1) list.Add(CustomPictureDutiesSpec(images));
                if (images >= 2)
                {
                    list.Add(AppearanceAndPoseSpec);
                    list.Add(AppearanceAndSceneSpec);
                }
                if (images >= 3) list.Add(AppearancePoseSceneSpec);
                if (images >= 4) list.Add(AppearancePoseSceneClothesSpec);
                if (images >= 3) list.Add(AppearancePoseAndWeakRestSpec(images));
                if (images >= 1 && videos >= 1) list.Add(AppearanceAndVideoSpec);
                list.Add(CharactersCatalogSpec);
                list.Add(VideosCatalogSpec);
                list.Add(AudiosCatalogSpec);
                break;
            case VideoConditioningMode.FirstFrame:
                if (images >= 1) list.Add(FirstFrameLockSpec);
                break;
            case VideoConditioningMode.FirstLastFrame:
                if (images >= 2) list.Add(FirstLastFrameSpec);
                break;
            case VideoConditioningMode.LastFrame:
                if (images >= 1) list.Add(LastFrameLockSpec);
                break;
        }
        return list;
    }

    public static bool CanInsertAny(VideoConditioningMode mode, int imageCount, int videoCount, int audioCount)
        => AvailableFor(mode, imageCount, videoCount, audioCount).Count > 0;

    public static bool HasAdjustableSize(VideoPromptTemplateSpec spec)
        => spec.MinSize > 0 && spec.MaxSize > spec.MinSize;

    public static VideoPromptTemplateSpec WithSize(VideoPromptTemplateSpec spec, int size)
        => spec.Kind switch
        {
            VideoPromptTemplateKind.Characters => CharactersSpec(size),
            VideoPromptTemplateKind.Videos => VideosSpec(size),
            VideoPromptTemplateKind.Audios => AudiosSpec(size),
            _ => spec,
        };

    public static string Render(
        VideoPromptTemplateKind kind,
        IReadOnlyDictionary<string, int> assignments,
        VideoPromptTemplatePhrases? phrases = null)
    {
        assignments ??= new Dictionary<string, int>();
        var text = phrases;
        return kind switch
        {
            VideoPromptTemplateKind.AppearanceAndPose => AppearanceAndPose(
                Require(assignments, "appearance"),
                Require(assignments, "pose"),
                text),
            VideoPromptTemplateKind.AppearanceAndScene => AppearanceAndScene(
                Require(assignments, "appearance"),
                Require(assignments, "scene"),
                text),
            VideoPromptTemplateKind.AppearancePoseScene => AppearancePoseScene(
                Require(assignments, "appearance"),
                Require(assignments, "pose"),
                Require(assignments, "scene"),
                text),
            VideoPromptTemplateKind.AppearancePoseSceneClothes => AppearancePoseSceneClothes(
                Require(assignments, "appearance"),
                Require(assignments, "pose"),
                Require(assignments, "scene"),
                Require(assignments, "clothing"),
                text),
            VideoPromptTemplateKind.AppearancePoseAndWeakRest => AppearancePoseAndWeakRest(assignments, text),
            VideoPromptTemplateKind.Characters => Characters(Indexed(assignments, "character", NodeMaxPictures), text),
            VideoPromptTemplateKind.FirstFrameLock => FirstFrameLock(Require(assignments, "first"), text),
            VideoPromptTemplateKind.FirstLastFrame => FirstLastFrame(
                Require(assignments, "first"),
                Require(assignments, "last"),
                text),
            VideoPromptTemplateKind.LastFrameLock => LastFrameLock(Require(assignments, "last"), text),
            VideoPromptTemplateKind.AppearanceAndVideo => AppearanceAndVideo(
                Require(assignments, "appearance"),
                Require(assignments, "video"),
                text),
            VideoPromptTemplateKind.Videos => Videos(Indexed(assignments, "video", NodeMaxVideos), text),
            VideoPromptTemplateKind.Audios => Audios(Indexed(assignments, "audio", NodeMaxAudios), text),
            VideoPromptTemplateKind.TextToVideo => TextToVideo(text),
            VideoPromptTemplateKind.CustomPictureDuties =>
                throw new InvalidOperationException("Custom picture duties use RenderCustom."),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    public static string RenderCustom(
        IReadOnlyList<(int Index, VideoPromptPictureDuty Duty)> duties,
        VideoPromptTemplatePhrases? phrases = null,
        IReadOnlyList<int>? videos = null,
        IReadOnlyList<int>? audios = null)
    {
        var used = (duties ?? [])
            .Where(item => item.Index >= 1 && item.Index <= NodeMaxPictures && item.Duty != VideoPromptPictureDuty.Unused)
            .ToArray();
        var unused = (duties ?? [])
            .Where(item => item.Index >= 1 && item.Index <= NodeMaxPictures && item.Duty == VideoPromptPictureDuty.Unused)
            .ToArray();
        if (used.Length == 0)
            throw new ArgumentException("At least one picture must have a duty.", nameof(duties));

        var copy = (phrases ?? VideoPromptTemplatePhrases.OfficialDefaults).WithDefaults();
        var ordered = used.OrderBy(entry => entry.Index).ToArray();
        var people = ordered.Where(item =>
            item.Duty is VideoPromptPictureDuty.Appearance or VideoPromptPictureDuty.Character).ToArray();
        var poses = ordered.Where(item => item.Duty == VideoPromptPictureDuty.Pose).ToArray();
        var clothes = ordered.Where(item => item.Duty == VideoPromptPictureDuty.Clothing).ToArray();
        var scenes = ordered.Where(item => item.Duty == VideoPromptPictureDuty.Scene).ToArray();
        var extras = ordered.Where(item =>
            item.Duty is VideoPromptPictureDuty.Style or VideoPromptPictureDuty.WeakReference).ToArray();
        var videoIds = (videos ?? []).Where(id => id >= 1 && id <= NodeMaxVideos).Distinct().ToArray();
        var audioIds = (audios ?? []).Where(id => id >= 1 && id <= NodeMaxAudios).Distinct().ToArray();

        var definitions = new List<string>();
        var summary = new List<string>();
        var retention = new List<string>();
        var details = new List<string>();
        var subject = 1;
        var hostIdentity = people.Length > 0;

        if (!hostIdentity)
        {
            foreach (var item in poses)
                AppendStandaloneDuty(copy, item.Duty, Pic(item.Index), $"<Subject {subject++}>", definitions, summary, retention, details);
            foreach (var item in clothes)
                AppendStandaloneDuty(copy, item.Duty, Pic(item.Index), $"<Subject {subject++}>", definitions, summary, retention, details);
        }

        var firstPerson = true;
        for (var personIndex = 0; personIndex < people.Length; personIndex++)
        {
            var person = people[personIndex];
            var pic = Pic(person.Index);
            var id = $"<Subject {subject}>";
            var clauses = new List<string>
            {
                VideoPromptTemplatePhrases.Apply(
                    person.Duty == VideoPromptPictureDuty.Appearance ? copy.AppearanceClause : copy.CharacterClause,
                    id, pic),
            };
            var poseLines = new List<string>();
            var retentionBits = new List<string> { VideoPromptTemplatePhrases.Apply(copy.AppearanceRetention, id, pic) };
            foreach (var pose in PosesForPerson(poses, personIndex, people.Length))
            {
                var posePic = Pic(pose.Index);
                var poseLine = VideoPromptTemplatePhrases.Apply(copy.PoseClause, id, posePic);
                if (LooksLikeRelativeClause(poseLine))
                    clauses.Add(poseLine);
                else
                    poseLines.Add(poseLine);
                retentionBits.Add(VideoPromptTemplatePhrases.Apply(copy.PoseRetention, id, posePic));
                summary.Add(VideoPromptTemplatePhrases.Apply(copy.PoseSummary, id, posePic));
                details.Add(VideoPromptTemplatePhrases.Apply(copy.PoseDetail, id, posePic));
                details.Add(VideoPromptTemplatePhrases.Apply(copy.PoseReplaceDetail, id, posePic));
                details.Add(VideoPromptTemplatePhrases.Apply(copy.PoseContinueDetail, id, posePic));
            }
            foreach (var item in ClothesForPerson(clothes, personIndex, people.Length))
            {
                var clothPic = Pic(item.Index);
                clauses.Add(VideoPromptTemplatePhrases.Apply(copy.ClothingClause, id, clothPic));
                retentionBits.Add(VideoPromptTemplatePhrases.Apply(copy.ClothingRetention, id, clothPic));
                summary.Add(VideoPromptTemplatePhrases.Apply(copy.ClothingSummary, id, clothPic));
                details.Add(VideoPromptTemplatePhrases.Apply(copy.ClothingDetail, id, clothPic));
            }

            definitions.Add(VideoPromptTemplatePhrases.Apply(copy.PersonDefinition, id, pic, string.Join(", and ", clauses)));
            definitions.AddRange(poseLines);
            summary.Add(VideoPromptTemplatePhrases.Apply(copy.AppearanceSummary, id, pic));
            retention.Add($"{id} (appears in [Shot 1]): fully_preserved - {string.Join("; ", retentionBits)}.");
            details.Insert(firstPerson ? 0 : details.Count, VideoPromptTemplatePhrases.Apply(copy.AppearanceDetail, id, pic));
            firstPerson = false;
            subject++;
        }

        foreach (var item in scenes)
            AppendStandaloneDuty(copy, item.Duty, Pic(item.Index), $"<Subject {subject++}>", definitions, summary, retention, details);
        foreach (var item in extras)
            AppendStandaloneDuty(copy, item.Duty, Pic(item.Index), $"<Subject {subject++}>", definitions, summary, retention, details);

        var hostId = people.Length > 0 ? "<Subject 1>" : "";
        foreach (var video in videoIds)
        {
            var vid = Vid(video);
            if (hostId.Length > 0)
            {
                definitions.Add(VideoPromptTemplatePhrases.Apply(copy.VideoOfSubjectDefinition, hostId, vid: vid));
                summary.Add(VideoPromptTemplatePhrases.Apply(copy.VideoSummary, hostId, vid: vid));
                retention.Add(VideoPromptTemplatePhrases.Apply(copy.VideoRetention, hostId, vid: vid));
                details.Add(VideoPromptTemplatePhrases.Apply(copy.VideoOfSubjectDetail, hostId, vid: vid));
            }
            else
            {
                definitions.Add(VideoPromptTemplatePhrases.Apply(copy.VideoDefinition, vid: vid));
                summary.Add(VideoPromptTemplatePhrases.Apply(copy.VideoSummary, vid: vid));
                retention.Add(VideoPromptTemplatePhrases.Apply(copy.VideoRetention, vid: vid));
                details.Add(VideoPromptTemplatePhrases.Apply(copy.VideoDetail, vid: vid));
            }
        }
        foreach (var audio in audioIds)
        {
            var aud = Aud(audio);
            definitions.Add(VideoPromptTemplatePhrases.Apply(copy.AudioDefinition, aud: aud));
            summary.Add(VideoPromptTemplatePhrases.Apply(copy.AudioSummary, aud: aud));
            retention.Add(VideoPromptTemplatePhrases.Apply(copy.AudioRetention, aud: aud));
            details.Add(VideoPromptTemplatePhrases.Apply(copy.AudioDetail, aud: aud));
        }
        foreach (var item in unused)
        {
            var pic = Pic(item.Index);
            definitions.Add($"Ignore {pic} completely. Do not copy identity, pose, clothing, or setting from it.");
            summary.Add($"Do not use {pic}.");
        }

        return
            $"""
            subject_definitions:
            {string.Join("\n", definitions)}

            summary:
            {copy.SummaryTag} {string.Join(" ", summary)}

            retention_analysis:
            {string.Join("\n", retention)}

            detailed_description:
            {FormatDetailedDescription(copy.StyleLine, details)}

            overall_soundscape: {copy.Soundscape}
            non_diegetic_music: {copy.Music}
            """;
    }

    public static bool TryValidate(
        VideoPromptTemplateSpec spec,
        IReadOnlyDictionary<string, int> assignments,
        out string? error)
    {
        error = null;
        if (spec.Kind == VideoPromptTemplateKind.CustomPictureDuties)
        {
            error = "按图分工请给每张图选择职责。";
            return false;
        }
        if (spec.Kind == VideoPromptTemplateKind.TextToVideo)
            return true;

        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in spec.Roles)
        {
            if (assignments is null || !assignments.TryGetValue(role.Key, out var index) || index < 1)
            {
                error = $"请选择「{role.Label}」对应的素材。";
                return false;
            }

            var token = $"{role.MediaKind}:{index}";
            if (!used.Add(token))
            {
                error = "同一个素材不能同时承担两个角色。";
                return false;
            }
        }
        return true;
    }

    public static VideoPromptPictureDuty DefaultDuty(int pictureIndex, int pictureCount)
    {
        if (pictureIndex == 1) return VideoPromptPictureDuty.Appearance;
        if (pictureIndex == 2 && pictureCount >= 2) return VideoPromptPictureDuty.Pose;
        if (pictureIndex == 3 && pictureCount >= 3) return VideoPromptPictureDuty.Scene;
        return VideoPromptPictureDuty.WeakReference;
    }

    public static string AppearanceAndPose(int appearancePicture, int posePicture, VideoPromptTemplatePhrases? phrases = null)
        => RenderCustom(
        [
            (appearancePicture, VideoPromptPictureDuty.Appearance),
            (posePicture, VideoPromptPictureDuty.Pose),
        ], phrases);

    public static string AppearanceAndScene(int appearancePicture, int scenePicture, VideoPromptTemplatePhrases? phrases = null)
        => RenderCustom(
        [
            (appearancePicture, VideoPromptPictureDuty.Appearance),
            (scenePicture, VideoPromptPictureDuty.Scene),
        ], phrases);

    public static string AppearancePoseScene(int appearancePicture, int posePicture, int scenePicture, VideoPromptTemplatePhrases? phrases = null)
        => RenderCustom(
        [
            (appearancePicture, VideoPromptPictureDuty.Appearance),
            (posePicture, VideoPromptPictureDuty.Pose),
            (scenePicture, VideoPromptPictureDuty.Scene),
        ], phrases);

    public static string AppearancePoseSceneClothes(
        int appearancePicture,
        int posePicture,
        int scenePicture,
        int clothingPicture,
        VideoPromptTemplatePhrases? phrases = null)
        => RenderCustom(
        [
            (appearancePicture, VideoPromptPictureDuty.Appearance),
            (posePicture, VideoPromptPictureDuty.Pose),
            (scenePicture, VideoPromptPictureDuty.Scene),
            (clothingPicture, VideoPromptPictureDuty.Clothing),
        ], phrases);

    public static string AppearancePoseAndWeakRest(
        IReadOnlyDictionary<string, int> assignments,
        VideoPromptTemplatePhrases? phrases = null)
    {
        var duties = new List<(int Index, VideoPromptPictureDuty Duty)>
        {
            (Require(assignments, "appearance"), VideoPromptPictureDuty.Appearance),
            (Require(assignments, "pose"), VideoPromptPictureDuty.Pose),
        };
        foreach (var pair in assignments.Where(item => item.Key.StartsWith("weak", StringComparison.Ordinal)))
            duties.Add((pair.Value, VideoPromptPictureDuty.WeakReference));
        return RenderCustom(duties, phrases);
    }

    public static string Characters(IReadOnlyList<int> pictures, VideoPromptTemplatePhrases? phrases = null)
    {
        if (pictures is null || pictures.Count < 2 || pictures.Count > NodeMaxPictures)
            throw new ArgumentOutOfRangeException(nameof(pictures));
        if (pictures.Distinct().Count() != pictures.Count)
            throw new ArgumentException("Each character must use a different picture.", nameof(pictures));
        return RenderCustom(pictures.Select(index => (index, VideoPromptPictureDuty.Character)).ToArray(), phrases);
    }

    public static string TextToVideo(VideoPromptTemplatePhrases? phrases = null)
    {
        var copy = (phrases ?? VideoPromptTemplatePhrases.OfficialDefaults).WithDefaults();
        return
            $"""
            integrated_multimodal_description:
            {copy.StyleLine}
            [Shot 1]

            overall_soundscape: {copy.Soundscape}
            non_diegetic_music: {copy.Music}
            """;
    }

    public static string FirstFrameLock(int firstPicture, VideoPromptTemplatePhrases? phrases = null)
    {
        if (firstPicture < 1 || firstPicture > NodeMaxPictures)
            throw new ArgumentOutOfRangeException(nameof(firstPicture));
        var copy = (phrases ?? VideoPromptTemplatePhrases.OfficialDefaults).WithDefaults();
        // Comfy MiniMaxH3ImageToVideo tokenizes first_frame as <Picture 1>.
        var pic = Pic(1);
        return
            $"""
            For the target video, at 0.00 seconds into the target video, {pic} (from [Shot 1]) is fully referenced.

            integrated_multimodal_description:
            {FormatDetailedDescription(
                copy.StyleLine,
                $"The opening frame matches {pic} in appearance, pose, and framing. {VideoPromptComposer.FirstFrameGenericMotion}")}

            overall_soundscape: {copy.Soundscape}
            non_diegetic_music: {copy.Music}
            """;
    }

    public static string FirstLastFrame(int firstPicture, int lastPicture, VideoPromptTemplatePhrases? phrases = null)
    {
        DistinctPictures(firstPicture, lastPicture);
        var copy = (phrases ?? VideoPromptTemplatePhrases.OfficialDefaults).WithDefaults();
        // Comfy prepends "<Picture 1>: " for first_frame and "<Picture 2>: " for last_frame.
        var first = Pic(1);
        var last = Pic(2);
        return
            $"""
            How the reference pictures align with the target video — Picture 1 (from Shot 1) aligns with the 0.00-second mark of the target video; Picture 2 (from Shot 1) aligns with the end of the target video.

            integrated_multimodal_description:
            {FormatDetailedDescription(
                copy.StyleLine,
                $"The shot begins in the pose and framing of {first}. The person then moves continuously until they settle into the pose, spacing, and composition of {last}.")}

            overall_soundscape: {copy.Soundscape}
            non_diegetic_music: {copy.Music}
            """;
    }

    public static string LastFrameLock(int lastPicture, VideoPromptTemplatePhrases? phrases = null)
    {
        if (lastPicture < 1 || lastPicture > NodeMaxPictures)
            throw new ArgumentOutOfRangeException(nameof(lastPicture));
        var copy = (phrases ?? VideoPromptTemplatePhrases.OfficialDefaults).WithDefaults();
        // Last-frame-only ImageToVideo tokenizes the single last_frame as <Picture 1>.
        var pic = Pic(1);
        return
            $"""
            How the reference pictures align with the target video — {pic} (from [Shot 1]) aligns with the end of the target video.

            integrated_multimodal_description:
            {FormatDetailedDescription(
                copy.StyleLine,
                $"A plausible preceding state develops until the pose, spacing, lighting, and composition settle on {pic} at the end of the shot.")}

            overall_soundscape: {copy.Soundscape}
            non_diegetic_music: {copy.Music}
            """;
    }

    public static string AppearanceAndVideo(int appearancePicture, int video, VideoPromptTemplatePhrases? phrases = null)
    {
        if (appearancePicture < 1 || appearancePicture > NodeMaxPictures)
            throw new ArgumentOutOfRangeException(nameof(appearancePicture));
        if (video < 1 || video > NodeMaxVideos)
            throw new ArgumentOutOfRangeException(nameof(video));
        var copy = (phrases ?? VideoPromptTemplatePhrases.OfficialDefaults).WithDefaults();
        var id = "<Subject 1>";
        var pic = Pic(appearancePicture);
        var vid = Vid(video);
        var appearanceClause = VideoPromptTemplatePhrases.Apply(copy.AppearanceClause, id, pic);
        return
            $"""
            subject_definitions:
            {VideoPromptTemplatePhrases.Apply(copy.PersonDefinition, id, pic, appearanceClause)}
            {VideoPromptTemplatePhrases.Apply(copy.VideoOfSubjectDefinition, id, vid: vid)}

            summary:
            {copy.SummaryTag} {VideoPromptTemplatePhrases.Apply(copy.AppearanceSummary, id, pic)} {VideoPromptTemplatePhrases.Apply(copy.VideoSummary, id, vid: vid)}

            retention_analysis:
            {id} (appears in [Shot 1]): fully_preserved - {VideoPromptTemplatePhrases.Apply(copy.AppearanceRetention, id, pic)}.
            {VideoPromptTemplatePhrases.Apply(copy.VideoRetention, id, vid: vid)}

            detailed_description:
            {FormatDetailedDescription(
                copy.StyleLine,
                VideoPromptTemplatePhrases.Apply(copy.AppearanceDetail, id, pic),
                VideoPromptTemplatePhrases.Apply(copy.VideoOfSubjectDetail, id, vid: vid),
                VideoPromptTemplatePhrases.Apply(copy.PoseContinueDetail, id))}

            overall_soundscape: {copy.Soundscape}
            non_diegetic_music: {copy.Music}
            """;
    }

    public static string Videos(IReadOnlyList<int> videos, VideoPromptTemplatePhrases? phrases = null)
    {
        var ids = RequireRange(videos, 1, NodeMaxVideos, nameof(videos));
        var copy = (phrases ?? VideoPromptTemplatePhrases.OfficialDefaults).WithDefaults();
        var definitions = ids.Select(id => VideoPromptTemplatePhrases.Apply(copy.VideoDefinition, vid: Vid(id)));
        var summary = string.Join(" ", ids.Select(id => VideoPromptTemplatePhrases.Apply(copy.VideoSummary, vid: Vid(id))));
        var retention = ids.Select(id => VideoPromptTemplatePhrases.Apply(copy.VideoRetention, vid: Vid(id)));
        var details = ids.Select(id => VideoPromptTemplatePhrases.Apply(copy.VideoDetail, vid: Vid(id)));
        return
            $"""
            subject_definitions:
            {string.Join("\n", definitions)}

            summary:
            {copy.SummaryTag} {summary}

            retention_analysis:
            {string.Join("\n", retention)}

            detailed_description:
            {FormatDetailedDescription(copy.StyleLine, details.Append("Keep the action readable from start to finish."))}

            overall_soundscape: {copy.Soundscape}
            non_diegetic_music: {copy.Music}
            """;
    }

    public static string Audios(IReadOnlyList<int> audios, VideoPromptTemplatePhrases? phrases = null)
    {
        var ids = RequireRange(audios, 1, NodeMaxAudios, nameof(audios));
        var copy = (phrases ?? VideoPromptTemplatePhrases.OfficialDefaults).WithDefaults();
        var definitions = ids.Select(id => VideoPromptTemplatePhrases.Apply(copy.AudioDefinition, aud: Aud(id)));
        var summary = string.Join(" ", ids.Select(id => VideoPromptTemplatePhrases.Apply(copy.AudioSummary, aud: Aud(id))));
        var retention = ids.Select(id => VideoPromptTemplatePhrases.Apply(copy.AudioRetention, aud: Aud(id)));
        var details = ids.Select(id => VideoPromptTemplatePhrases.Apply(copy.AudioDetail, aud: Aud(id)));
        return
            $"""
            subject_definitions:
            {string.Join("\n", definitions)}

            summary:
            {copy.SummaryTag} + [audio reference] {summary}

            retention_analysis:
            {string.Join("\n", retention)}

            detailed_description:
            {FormatDetailedDescription(copy.StyleLine, details)}

            overall_soundscape: {copy.Soundscape}
            non_diegetic_music: {copy.Music}
            """;
    }

    private static IReadOnlyList<int> Indexed(IReadOnlyDictionary<string, int> assignments, string prefix, int max)
    {
        var values = new List<int>();
        for (var i = 1; i <= max; i++)
        {
            if (assignments.TryGetValue(prefix + i, out var index) && index >= 1)
                values.Add(index);
        }
        if (values.Count == 0)
            throw new ArgumentException($"Missing '{prefix}' assignments.", nameof(assignments));
        return values;
    }

    private static IReadOnlyList<int> RequireRange(IReadOnlyList<int> values, int minCount, int maxCount, string name)
    {
        if (values is null || values.Count < minCount || values.Count > maxCount)
            throw new ArgumentOutOfRangeException(name);
        if (values.Any(value => value < 1) || values.Distinct().Count() != values.Count)
            throw new ArgumentException("Each slot must use a different item.", name);
        return values.ToArray();
    }

    private static int Require(IReadOnlyDictionary<string, int> assignments, string key)
    {
        if (!assignments.TryGetValue(key, out var index) || index < 1)
            throw new ArgumentException($"Missing assignment for '{key}'.", nameof(assignments));
        return index;
    }

    private static void DistinctPictures(int left, int right)
    {
        if (left < 1 || left > NodeMaxPictures) throw new ArgumentOutOfRangeException(nameof(left));
        if (right < 1 || right > NodeMaxPictures) throw new ArgumentOutOfRangeException(nameof(right));
        if (left == right)
            throw new ArgumentException("Each role must use a different picture.");
    }

    private static void AppendStandaloneDuty(
        VideoPromptTemplatePhrases copy,
        VideoPromptPictureDuty duty,
        string pic,
        string id,
        List<string> definitions,
        List<string> summary,
        List<string> retention,
        List<string> details)
    {
        string Fill(string template) => VideoPromptTemplatePhrases.Apply(template, id, pic);
        switch (duty)
        {
            case VideoPromptPictureDuty.Pose:
                definitions.Add(Fill(copy.StandalonePoseDefinition));
                summary.Add(Fill(copy.StandalonePoseSummary));
                retention.Add(Fill(copy.StandalonePoseRetention));
                details.Add(Fill(copy.StandalonePoseDetail));
                break;
            case VideoPromptPictureDuty.Scene:
                definitions.Add(Fill(copy.SceneDefinition));
                summary.Add(Fill(copy.SceneSummary));
                retention.Add(Fill(copy.SceneRetention));
                details.Add(Fill(copy.SceneDetail));
                break;
            case VideoPromptPictureDuty.Clothing:
                definitions.Add(Fill(copy.StandaloneClothingDefinition));
                summary.Add(Fill(copy.ClothingSummary));
                retention.Add(Fill(copy.StandaloneClothingRetention));
                details.Add(Fill(copy.ClothingDetail));
                break;
            case VideoPromptPictureDuty.Style:
                definitions.Add(Fill(copy.StyleDefinition));
                summary.Add(Fill(copy.StyleSummary));
                retention.Add(Fill(copy.StyleRetention));
                details.Add(Fill(copy.StyleDetail));
                break;
            default:
                definitions.Add(Fill(copy.WeakDefinition));
                summary.Add(Fill(copy.WeakSummary));
                retention.Add(Fill(copy.WeakRetention));
                details.Add(Fill(copy.WeakDetail));
                break;
        }
    }

    private static string FormatDetailedDescription(string styleLine, params string[] details)
        => FormatDetailedDescription(styleLine, (IEnumerable<string>)details);

    private static string FormatDetailedDescription(string styleLine, IEnumerable<string> details)
    {
        var lines = (details ?? []).Where(item => !string.IsNullOrWhiteSpace(item)).ToArray();
        if (lines.Length == 0) return styleLine;
        if (!lines.Any(line => line.Contains("[Shot 1]", StringComparison.OrdinalIgnoreCase)))
            lines[0] = "[Shot 1] " + lines[0].TrimStart();
        return string.IsNullOrWhiteSpace(styleLine)
            ? string.Join("\n", lines)
            : styleLine + "\n" + string.Join("\n", lines);
    }

    private static IReadOnlyList<(int Index, VideoPromptPictureDuty Duty)> PosesForPerson(
        IReadOnlyList<(int Index, VideoPromptPictureDuty Duty)> poses,
        int personIndex,
        int personCount)
    {
        if (poses.Count == 0) return [];
        if (poses.Count == 1) return poses;
        var assigned = new List<(int Index, VideoPromptPictureDuty Duty)>();
        if (personIndex < poses.Count) assigned.Add(poses[personIndex]);
        if (personIndex == 0 && poses.Count > personCount)
            assigned.AddRange(poses.Skip(personCount));
        return assigned;
    }

    private static IReadOnlyList<(int Index, VideoPromptPictureDuty Duty)> ClothesForPerson(
        IReadOnlyList<(int Index, VideoPromptPictureDuty Duty)> clothes,
        int personIndex,
        int personCount)
    {
        if (clothes.Count == 0) return [];
        if (clothes.Count == 1)
            return personIndex == 0 ? clothes : [];
        var assigned = new List<(int Index, VideoPromptPictureDuty Duty)>();
        if (personIndex < clothes.Count) assigned.Add(clothes[personIndex]);
        if (personIndex == 0 && clothes.Count > personCount)
            assigned.AddRange(clothes.Skip(personCount));
        return assigned;
    }

    private static bool LooksLikeRelativeClause(string text)
    {
        var trimmed = (text ?? "").TrimStart();
        return trimmed.StartsWith("whose ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("and whose ", StringComparison.OrdinalIgnoreCase);
    }

    private static string Pic(int index) => $"<Picture {index}>";
    private static string Vid(int index) => $"<Video {index}>";
    private static string Aud(int index) => $"<Audio {index}>";
}
