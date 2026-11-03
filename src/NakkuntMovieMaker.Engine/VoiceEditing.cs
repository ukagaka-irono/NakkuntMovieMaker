using NakkuntMovieMaker.Audio;
using NakkuntMovieMaker.Core.Assets;
using NakkuntMovieMaker.Core.Editing;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;
using NakkuntMovieMaker.Voice;

namespace NakkuntMovieMaker.Engine;

/// <summary>声を用意できなかったセリフと、その理由（利用者に表示できる文）</summary>
public sealed record VoiceFailure(ItemId Item, string Message);

/// <summary>セリフの声の合成・録音した声の利用と、それに合わせた長さの調整</summary>
public static class VoiceEditing
{
    /// <summary>声の長さが収まるアイテムの長さ（フレーム。最低 1）</summary>
    public static long LengthFor(TimeSpan duration, Rational fps) =>
        Math.Max(1, (long)Math.Ceiling(Math.Round(duration.TotalSeconds * fps.ToDouble(), 6)));

    /// <summary>
    /// セリフの声を source にし、長さを声に合わせる。長くなって同じレイヤーの後ろのアイテムと重なるときは、
    /// 後ろのアイテムをまとめて同じだけ後ろへずらす（短くなったときは、後ろのアイテムは動かさない）
    /// </summary>
    public static IOperation ApplyVoice(Project project, SceneId sceneId, ItemId itemId, VoiceSource source, TimeSpan duration)
    {
        var scene = project.GetScene(sceneId);
        var voice = scene.GetItem(itemId) as VoiceItem ?? throw new EditException("セリフのアイテムではありません");
        var length = LengthFor(duration, scene.Fps);
        var updated = voice with { Source = source, Length = length };
        var growth = updated.End - voice.End;

        var operations = new List<IOperation>();
        var following = scene.ItemsOnLayer(voice.Layer).Where(item => item.Id != voice.Id && item.Start >= voice.Start).Select(item => item.Id).ToList();
        if (growth > 0 && following.Count > 0 && scene.ItemsOnLayer(voice.Layer).Any(item => following.Contains(item.Id) && item.Start < updated.End))
        {
            var move = EditCommands.MoveItems(project, sceneId, following, growth);
            operations.Add(move);
            project = move.Apply(project).Project;
        }

        operations.Add(EditCommands.UpdateItem(project, sceneId, updated));
        return operations.Count == 1 ? operations[0] : Operations.Combine("セリフの声", operations);
    }

    /// <summary>
    /// セリフの声を合成して、アイテムに付ける操作（変わったセリフがなければ null）。録音した声のセリフは飛ばす。
    /// 声はキャラクターの設定（セリフで調整を変えていればその値）で、読みはセリフで直していればその読みで合成する。
    /// 合成できなかったセリフは Failures に入れ、ほかのセリフは続けて処理する
    /// </summary>
    public static async Task<(IOperation? Operation, IReadOnlyList<VoiceFailure> Failures)> SynthesizeAsync(
        Project project, SceneId sceneId, IEnumerable<ItemId> items, VoiceService voices, CancellationToken cancellationToken = default)
    {
        var operations = new List<IOperation>();
        var failures = new List<VoiceFailure>();
        foreach (var itemId in items)
        {
            if (project.GetScene(sceneId).GetItem(itemId) is not VoiceItem { Source: SynthesizedVoice current } voice)
            {
                continue;
            }

            if (!project.Characters.TryGetValue(voice.Character, out var character))
            {
                failures.Add(new VoiceFailure(itemId, "セリフのキャラクターが見つかりません"));
                continue;
            }

            var spec = voice.VoiceOverride is { } parameters ? character.Voice with { Params = parameters } : character.Voice;
            VoiceResult result;
            try
            {
                result = await voices.GetAsync(spec, voice.Text, voice.Reading, cancellationToken).ConfigureAwait(false);
            }
            catch (VoiceEngineException ex)
            {
                failures.Add(new VoiceFailure(itemId, ex.Message));
                continue;
            }

            var scene = project.GetScene(sceneId);
            if (current.CacheKey == result.CacheKey && voice.Length == LengthFor(result.Duration, scene.Fps))
            {
                continue;
            }

            var operation = ApplyVoice(project, sceneId, itemId, new SynthesizedVoice(result.CacheKey), result.Duration);
            operations.Add(operation);
            project = operation.Apply(project).Project;
        }

        var combined = operations.Count switch
        {
            0 => null,
            1 => operations[0],
            _ => Operations.Combine("セリフの声の合成", operations),
        };
        return (combined, failures);
    }

    /// <summary>
    /// 録音した声をセリフの声にする操作（セリフごとの収録）。録音を素材として加え、長さを録音に合わせる
    /// </summary>
    public static IOperation UseRecording(Project project, SceneId sceneId, ItemId itemId, RecordingResult recording, string projectFolder)
    {
        var asset = new Asset(AssetId.New(), AssetKind.Audio, AssetLinks.ToStoredPath(recording.Path, projectFolder));
        var withAsset = project with { Assets = project.Assets.SetItem(asset.Id, asset) };
        var duration = TimeSpan.FromSeconds((double)recording.Frames / recording.SampleRate);
        return Operations.Combine(
            "セリフの録音",
            [new SetAsset(asset.Id, asset), ApplyVoice(withAsset, sceneId, itemId, new RecordedVoice(asset.Id), duration)]);
    }
}
