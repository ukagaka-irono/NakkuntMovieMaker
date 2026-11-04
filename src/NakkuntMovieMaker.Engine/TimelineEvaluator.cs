using System.Collections.Immutable;
using System.Numerics;
using NakkuntMovieMaker.Core.Collections;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;
using NakkuntMovieMaker.Rendering;
using NakkuntMovieMaker.Rendering.Effects;
using NakkuntMovieMaker.Rendering.Transitions;
using NakkuntMovieMaker.Engine.Tachie;

namespace NakkuntMovieMaker.Engine;

/// <summary>
/// シーンのあるフレームに何をどう描くかを求めて、FramePlan にする。
/// キーフレーム・変形・不透明度・切り抜き・反転・合成モード・動画の速度と逆再生をここで計算し、
/// 描画の処理はその結果を描くだけにする
/// </summary>
public static class TimelineEvaluator
{
    /// <summary>
    /// frame（シーンのフレーム番号）の FramePlan を作る。非表示のレイヤーは描かず、
    /// ソロのレイヤーが 1 つでもあれば、ソロのレイヤーだけを描く。
    /// エフェクトとトランジションは、effects・transitions（省略すると組み込みのもの）に登録された種類だけを評価し、
    /// 知らない種類は飛ばす（トランジションは、かけずにそのまま切り替える）。
    /// セリフの字幕と立ち絵は、timeline（プロジェクトのキャラクター・立ち絵の読み込み・口パク用の声の大きさ）を使って描く
    /// </summary>
    public static FramePlan Evaluate(
        Scene scene,
        long frame,
        RgbaColor? background = null,
        EffectRegistry? effects = null,
        TransitionRegistry? transitions = null,
        TimelineContext? timeline = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var context = new Context(scene, effects ?? EffectRegistry.Default, timeline ?? TimelineContext.Empty);
        transitions ??= TransitionRegistry.Default;

        var anySolo = scene.Layers.Any(layer => layer.Solo);
        var nodes = ImmutableArray.CreateBuilder<RenderNode>();
        foreach (var layer in scene.Layers.Where(layer => layer.Visible && (!anySolo || layer.Solo)))
        {
            nodes.AddRange(EvaluateLayer(context, layer, frame, transitions));
        }

        return new FramePlan(scene.Format.Width, scene.Format.Height, background ?? RgbaColor.Black, new EquatableArray<RenderNode>(nodes.ToImmutable()));
    }

    /// <summary>
    /// トランジションをかける範囲（シーンのフレーム、[Start, Start + Duration)）と、切り替える元のアイテム。
    /// 長さは、前後のアイテムの長さを超えないように縮める（隣のトランジションと重ならないように）。
    /// 前に隣り合うアイテムがあれば切れ目を中心に、なければアイテムの始まりからかける
    /// </summary>
    public static (long Start, long Duration, Item? From)? TransitionWindow(Scene scene, Item item)
    {
        if (item.Transition is not { } transition)
        {
            return null;
        }

        var from = SceneIndex.Of(scene).EndingAt(item.Layer, item.Start, item.Id);
        var duration = Math.Min(transition.Duration, Math.Min(item.Length, from?.Length ?? long.MaxValue));
        if (duration <= 0)
        {
            return null;
        }

        var start = from is null ? item.Start : item.Start - duration / 2;
        return (start, duration, from);
    }

    // 1 つのレイヤーのノード。トランジションの範囲では、前後のアイテムを 1 つの TransitionNode にまとめる
    private static IEnumerable<RenderNode> EvaluateLayer(Context context, Layer layer, long frame, TransitionRegistry transitions)
    {
        var scene = context.Scene;
        // このフレームに関わりうるアイテムだけ（始まりの順）
        var items = SceneIndex.Of(scene).Near(layer.Id, frame).ToArray();
        var replaced = new HashSet<ItemId>();
        var transitionNodes = new List<(long Start, RenderNode Node)>();
        foreach (var item in items)
        {
            if (TransitionWindow(scene, item) is not { } window
                || frame < window.Start || frame >= window.Start + window.Duration
                || transitions.Find(item.Transition!.Type) is not { } definition)
            {
                continue;
            }

            var local = frame - window.Start;
            var parameters = EvaluateParameters(definition.Parameters, item.Transition.Parameters, local);
            var progress = local / (double)window.Duration;
            var from = window.From is null ? null : EvaluateItem(context, window.From, frame);
            var to = EvaluateItem(context, item, frame);
            transitionNodes.Add((item.Start, new TransitionNode(definition.Type, progress, parameters, from, to)));
            replaced.Add(item.Id);
            if (window.From is not null)
            {
                replaced.Add(window.From.Id);
            }
        }

        return items
            .Where(item => item.Contains(frame) && !replaced.Contains(item.Id))
            .Select(item => (item.Start, Node: (RenderNode?)EvaluateItem(context, item, frame)))
            .Concat(transitionNodes.Select(entry => (entry.Start, Node: (RenderNode?)entry.Node)))
            .Where(entry => entry.Node is not null)
            .OrderBy(entry => entry.Start)
            .Select(entry => entry.Node!);
    }

    private static DrawNode? EvaluateItem(Context context, Item item, long frame)
    {
        var (scene, effects, timeline) = context;
        if (item is not VisualItem visual)
        {
            return null;
        }

        var local = item.ToLocalFrame(frame);
        DrawContent? content = visual switch
        {
            VideoItem video => new VideoFrameContent(video.Asset, SourceTime(video.Playback, local, video.Length, scene.Fps)),
            ImageItem image => new ImageContent(image.Asset),
            TextItem text => new TextContent(text.Text, text.Style, VisibleCharacters(text.Text, text.Style, local, scene.Fps)),
            ShapeItem shape => new ShapeContent(shape.Shape),
            VoiceItem voice => Subtitle(voice, timeline.Characters, local, scene.Fps),
            TachieItem tachie => Tachie(scene, tachie, frame, timeline),
            _ => null,
        };
        if (content is null)
        {
            return null;
        }

        var opacity = Math.Clamp(visual.Opacity.Evaluate(local), 0, 1);
        var transform = Transform(visual.Transform, local, scene.Format);
        if (opacity <= 0 || transform.GetDeterminant() == 0)
        {
            return null;
        }

        var evaluated = EvaluateEffects(item.Effects, new EffectTime(local, item.Length, scene.Fps), effects);
        return new DrawNode(item.Id, content, visual.Crop, transform, opacity, visual.Blend, evaluated);
    }

    // セリフの字幕：セリフで指定したスタイル、なければキャラクターの字幕のスタイル（キャラクターがなければ既定のスタイル）
    private static TextContent? Subtitle(VoiceItem voice, IReadOnlyDictionary<CharacterId, Character> characters, long local, Rational fps)
    {
        if (string.IsNullOrEmpty(voice.Text))
        {
            return null;
        }

        var style = voice.SubtitleOverride ?? characters.GetValueOrDefault(voice.Character)?.Subtitle ?? TextStyle.Default;
        return new TextContent(voice.Text, style, VisibleCharacters(voice.Text, style, local, fps));
    }

    /// <summary>
    /// アイテムのエフェクトを、ある時点の値に評価する。オフのエフェクトと知らない種類のエフェクトは飛ばす。
    /// 指定のないパラメーターと型の合わないパラメーターは既定値にし、数値は定義の範囲に収める
    /// </summary>
    public static EquatableArray<EvaluatedEffect> EvaluateEffects(IEnumerable<EffectInstance> instances, EffectTime time, EffectRegistry effects)
    {
        var result = ImmutableArray.CreateBuilder<EvaluatedEffect>();
        foreach (var instance in instances)
        {
            if (!instance.Enabled || effects.Find(instance.Type) is not { } definition)
            {
                continue;
            }

            var resolved = definition.Resolve(EvaluateParameters(definition.Parameters, instance.Parameters, time.LocalFrame), time);
            result.Add(new EvaluatedEffect(definition.Type, resolved));
        }

        return new EquatableArray<EvaluatedEffect>(result.ToImmutable());
    }

    // 定義のパラメーターを、保存された値（なければ既定値）から local フレームの値にする
    private static EffectParameters EvaluateParameters(
        IEnumerable<ParameterDefinition> definitions, IReadOnlyDictionary<string, ParameterValue> stored, long local)
    {
        var values = ImmutableSortedDictionary.CreateBuilder<string, object>(StringComparer.Ordinal);
        foreach (var parameter in definitions)
        {
            var value = stored.TryGetValue(parameter.Name, out var saved) ? EvaluateParameter(saved, parameter, local) : null;
            values[parameter.Name] = value ?? parameter.Default;
        }

        return new EffectParameters(values.ToImmutable());
    }

    private static object? EvaluateParameter(ParameterValue stored, ParameterDefinition parameter, long local) => (stored, parameter.Kind) switch
    {
        (NumberParameter number, ParameterKind.Number) => Math.Clamp(number.Value.Evaluate(local), parameter.Min, parameter.Max),
        (ColorParameter color, ParameterKind.Color) => color.Value.Evaluate(local),
        (BoolParameter flag, ParameterKind.Bool) => flag.Value,
        (TextParameter text, ParameterKind.Choice) when parameter.Choices.Contains(text.Value) => text.Value,
        (TextParameter text, ParameterKind.File) => text.Value,
        _ => null,
    };

    /// <summary>
    /// アイテムの先頭から local フレーム目に表示する、素材の時刻（秒）。
    /// アイテムは素材の [SourceOffset, SourceOffset + 長さ × Speed) を使い、逆再生ならその範囲を後ろから使う
    /// （最初のフレームが範囲の最後のフレーム、最後のフレームが範囲の最初のフレームになる）
    /// </summary>
    public static Rational SourceTime(MediaPlayback playback, long local, long length, Rational fps)
    {
        var position = playback.Reverse ? length - 1 - local : local;
        return playback.SourceOffset + TimeMath.FrameToSeconds(position, fps) * playback.Speed;
    }

    /// <summary>
    /// 変形の行列（ローカル座標 → シーンのピクセル座標）。ローカル座標の原点は素材（切り抜いた後）の中心。
    /// 反転は素材の中心で行い、中心点（Anchor）を位置（X, Y。シーンの中心が原点）に置いて、中心点のまわりに拡大・回転する。
    /// 回転は時計回り（画面の y は下向き）
    /// </summary>
    public static Matrix3x2 Transform(Transform2D transform, double local, SceneFormat format)
    {
        var flip = Matrix3x2.CreateScale(transform.FlipHorizontal ? -1 : 1, transform.FlipVertical ? -1 : 1);
        var anchor = Matrix3x2.CreateTranslation((float)-transform.AnchorX.Evaluate(local), (float)-transform.AnchorY.Evaluate(local));
        var scale = Matrix3x2.CreateScale((float)transform.ScaleX.Evaluate(local), (float)transform.ScaleY.Evaluate(local));
        var rotation = Matrix3x2.CreateRotation((float)(transform.Rotation.Evaluate(local) * Math.PI / 180));
        var position = Matrix3x2.CreateTranslation(
            (float)(format.Width / 2.0 + transform.X.Evaluate(local)),
            (float)(format.Height / 2.0 + transform.Y.Evaluate(local)));
        return flip * anchor * scale * rotation * position;
    }

    // 一文字ずつ表示するときに見えている文字の数。サロゲートペアなどの組み合わせ文字は 1 文字と数える
    private static int? VisibleCharacters(string text, TextStyle style, long local, Rational fps)
    {
        if (style.RevealCharsPerSecond <= 0)
        {
            return null;
        }

        var seconds = TimeMath.FrameToSeconds(local, fps).ToDouble();
        var total = new System.Globalization.StringInfo(text).LengthInTextElements;

        // トランジションでアイテムの始まりより前を描くときは、まだ 1 文字も見えない
        return (int)Math.Clamp(Math.Floor(seconds * style.RevealCharsPerSecond + 1e-9), 0, total);
    }

    // 立ち絵：キャラクターの立ち絵のパーツを重ねる。目は目パチ、口は同じキャラクターのセリフの声の大きさで口パクする
    private static TachieContent? Tachie(Scene scene, TachieItem item, long frame, TimelineContext timeline)
    {
        if (!timeline.Characters.TryGetValue(item.Character, out var character)
            || character.Tachie is not { } spec
            || timeline.Tachie?.Load(spec) is not { Parts.IsEmpty: false } model)
        {
            return null;
        }

        var local = item.ToLocalFrame(frame);
        var mouth = MouthLevel(scene, item.Character, frame, timeline.LipSync);
        var seed = TachieAnimation.Seed(item.Id);
        var layers = model.Parts.Select(part => part.Kind switch
        {
            TachiePartKind.Mouth => part.Frames[TachieAnimation.MouthFrame(part.Frames.Length, mouth)],
            TachiePartKind.Eyes => part.Frames[TachieAnimation.EyeFrame(part.Frames.Length, local, scene.Fps, seed)],
            _ => part.Frames[0],
        });
        return new TachieContent([.. layers]);
    }

    // 同じキャラクターの、frame に鳴っているセリフの声の大きさ（いくつかあれば大きいほう）。ミュートのレイヤーのセリフは数えない
    private static float MouthLevel(Scene scene, CharacterId character, long frame, ILipSyncSource? lipSync)
    {
        if (lipSync is null)
        {
            return 0;
        }

        var level = 0f;
        foreach (var candidate in SceneIndex.Of(scene).VoicesNear(frame))
        {
            if (candidate is not VoiceItem voice || voice.Character != character || !voice.Contains(frame) || scene.FindLayer(voice.Layer) is not { Muted: false })
            {
                continue;
            }

            var local = voice.ToLocalFrame(frame);
            if (lipSync.Envelope(voice, scene.Fps) is { } envelope && local < envelope.Length)
            {
                level = Math.Max(level, envelope[local]);
            }
        }

        return level;
    }

    // 評価に使うもの（シーン・エフェクトの種類・キャラクターと立ち絵）
    private sealed record Context(Scene Scene, EffectRegistry Effects, TimelineContext Timeline);
}
