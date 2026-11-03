using System.Collections.Immutable;
using System.Numerics;
using NakkuntMovieMaker.Core.Collections;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;
using NakkuntMovieMaker.Rendering;
using NakkuntMovieMaker.Rendering.Effects;

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
    /// エフェクトは effects（省略すると組み込みのエフェクト）に登録された種類だけを評価し、知らない種類は飛ばす
    /// </summary>
    public static FramePlan Evaluate(Scene scene, long frame, RgbaColor? background = null, EffectRegistry? effects = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        effects ??= EffectRegistry.Default;

        var anySolo = scene.Layers.Any(layer => layer.Solo);
        var shown = scene.Layers
            .Select((layer, index) => (layer, index))
            .Where(entry => entry.layer.Visible && (!anySolo || entry.layer.Solo))
            .ToDictionary(entry => entry.layer.Id, entry => entry.index);

        var nodes = scene.Items.Values
            .Where(item => item.Contains(frame) && shown.ContainsKey(item.Layer))
            .OrderBy(item => shown[item.Layer])
            .ThenBy(item => item.Start)
            .ThenBy(item => item.Id.Value)
            .Select(item => EvaluateItem(scene, item, frame, effects))
            .OfType<RenderNode>()
            .ToImmutableArray();

        return new FramePlan(scene.Format.Width, scene.Format.Height, background ?? RgbaColor.Black, new EquatableArray<RenderNode>(nodes));
    }

    private static DrawNode? EvaluateItem(Scene scene, Item item, long frame, EffectRegistry effects)
    {
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

            // セリフの字幕と立ち絵は、キャラクターの設定を使うので、セリフ・立ち絵の手順で加える
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

            var values = ImmutableSortedDictionary.CreateBuilder<string, object>(StringComparer.Ordinal);
            foreach (var parameter in definition.Parameters)
            {
                var value = instance.Parameters.TryGetValue(parameter.Name, out var stored)
                    ? EvaluateParameter(stored, parameter, time.LocalFrame)
                    : null;
                values[parameter.Name] = value ?? parameter.Default;
            }

            var resolved = definition.Resolve(new EffectParameters(values.ToImmutable()), time);
            result.Add(new EvaluatedEffect(definition.Type, resolved));
        }

        return new EquatableArray<EvaluatedEffect>(result.ToImmutable());
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
        return (int)Math.Min(total, Math.Floor(seconds * style.RevealCharsPerSecond + 1e-9));
    }
}
