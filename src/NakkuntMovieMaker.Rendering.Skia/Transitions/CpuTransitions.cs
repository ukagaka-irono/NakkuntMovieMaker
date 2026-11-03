using System.Collections.Frozen;
using NakkuntMovieMaker.Rendering.Effects;
using NakkuntMovieMaker.Rendering.Transitions;
using SkiaSharp;

namespace NakkuntMovieMaker.Rendering.Skia.Transitions;

/// <summary>
/// トランジションの CPU での処理。From・To は、それぞれのアイテムを出力と同じ大きさの透明な画像に描いたもの（ないこともある）。
/// canvas は出力のピクセル座標で、Progress は 0（すべて From）〜 1（すべて To）
/// </summary>
internal interface ICpuTransition
{
    string Type { get; }

    void Draw(SKCanvas canvas, TransitionFrames frames, float progress, EffectParameters parameters);
}

/// <summary>トランジションの前後の画像と、出力の大きさ</summary>
internal sealed record TransitionFrames(SKImage? From, SKImage? To, int Width, int Height)
{
    public SKRect Bounds => SKRect.Create(Width, Height);

    public SKPoint Center => new(Width / 2f, Height / 2f);

    /// <summary>画像を、出力の中心のまわりに matrix で変形して描く（画像がなければ何もしない）</summary>
    public static void Draw(SKCanvas canvas, SKImage? image, SKMatrix matrix, SKPaint? paint = null)
    {
        if (image is null)
        {
            return;
        }

        canvas.Save();
        canvas.Concat(in matrix);
        using var plain = paint is null ? new SKPaint { IsAntialias = true } : null;
        canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear), paint ?? plain);
        canvas.Restore();
    }

    /// <summary>出力の中心を基準に、拡大して回す変形</summary>
    public SKMatrix AroundCenter(float scale, float degrees) =>
        SKMatrix.Concat(
            SKMatrix.CreateTranslation(Center.X, Center.Y),
            SKMatrix.Concat(SKMatrix.CreateRotationDegrees(degrees), SKMatrix.Concat(SKMatrix.CreateScale(scale, scale), SKMatrix.CreateTranslation(-Center.X, -Center.Y))));
}

/// <summary>組み込みのトランジションの CPU での処理の一覧</summary>
internal static class CpuTransitions
{
    private static readonly FrozenDictionary<string, ICpuTransition> ByType = new ICpuTransition[]
    {
        new CrossfadeTransition(), new DipTransition(), new WipeTransition(), new SlideTransition(),
        new PushTransition(), new ZoomTransition(), new SpinTransition(),
    }.ToFrozenDictionary(transition => transition.Type, StringComparer.Ordinal);

    public static ICpuTransition? Find(string type) => ByType.GetValueOrDefault(type);

    /// <summary>
    /// From·(1 − m) + To·m を描く（色は premultiplied なので、透明な部分もそのまま混ざる）。
    /// fromMask は (1 − m)、toMask は m を不透明度に持つシェーダー
    /// </summary>
    public static void Mix(SKCanvas canvas, TransitionFrames frames, Action<SKCanvas> drawFrom, SKShader fromMask, Action<SKCanvas> drawTo, SKShader toMask)
    {
        var bounds = frames.Bounds;
        canvas.SaveLayer(bounds, null);

        canvas.SaveLayer(bounds, null);
        drawFrom(canvas);
        Mask(canvas, bounds, fromMask);
        canvas.Restore();

        using var plus = new SKPaint { BlendMode = SKBlendMode.Plus };
        canvas.SaveLayer(bounds, plus);
        drawTo(canvas);
        Mask(canvas, bounds, toMask);
        canvas.Restore();

        canvas.Restore();
    }

    /// <summary>不透明度が一様なシェーダー</summary>
    public static SKShader Uniform(float alpha) =>
        SKShader.CreateColor(SKColors.White.WithAlpha((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255)));

    /// <summary>向き（後のアイテムが入ってくる側）を、出力の上での単位ベクトルにする</summary>
    public static SKPoint Direction(string direction) => direction switch
    {
        "fromRight" => new SKPoint(1, 0),
        "fromTop" => new SKPoint(0, -1),
        "fromBottom" => new SKPoint(0, 1),
        _ => new SKPoint(-1, 0),
    };

    // 描いたものに、シェーダーの不透明度を掛ける
    private static void Mask(SKCanvas canvas, SKRect bounds, SKShader mask)
    {
        using var paint = new SKPaint { Shader = mask, BlendMode = SKBlendMode.DstIn };
        canvas.DrawRect(bounds, paint);
    }
}

/// <summary>クロスフェード</summary>
internal sealed class CrossfadeTransition : ICpuTransition
{
    public string Type => BuiltInTransitions.Crossfade.Type;

    public void Draw(SKCanvas canvas, TransitionFrames frames, float progress, EffectParameters parameters)
    {
        using var fromMask = CpuTransitions.Uniform(1 - progress);
        using var toMask = CpuTransitions.Uniform(progress);
        CpuTransitions.Mix(
            canvas,
            frames,
            c => TransitionFrames.Draw(c, frames.From, SKMatrix.Identity),
            fromMask,
            c => TransitionFrames.Draw(c, frames.To, SKMatrix.Identity),
            toMask);
    }
}

/// <summary>色を挟んだフェード。前半で前のアイテムが色に変わり、後半で色から後のアイテムに変わる（アイテムの形の中だけ）</summary>
internal sealed class DipTransition : ICpuTransition
{
    public string Type => BuiltInTransitions.Dip.Type;

    public void Draw(SKCanvas canvas, TransitionFrames frames, float progress, EffectParameters parameters)
    {
        var first = progress < 0.5f;
        var image = first ? frames.From : frames.To;
        if (image is null)
        {
            return;
        }

        var amount = first ? progress * 2 : 2 - progress * 2;
        var color = parameters.GetColor("color");
        canvas.SaveLayer(frames.Bounds, null);
        TransitionFrames.Draw(canvas, image, SKMatrix.Identity);
        using var paint = new SKPaint
        {
            Color = new SKColor(color.R, color.G, color.B, (byte)Math.Round(color.A * Math.Clamp(amount, 0, 1))),
            BlendMode = SKBlendMode.SrcATop,
        };
        canvas.DrawRect(frames.Bounds, paint);
        canvas.Restore();
    }
}

/// <summary>ワイプ。境界が向きの側から反対側へ動き、後のアイテムが現れる。境界はぼかせる</summary>
internal sealed class WipeTransition : ICpuTransition
{
    public string Type => BuiltInTransitions.Wipe.Type;

    public void Draw(SKCanvas canvas, TransitionFrames frames, float progress, EffectParameters parameters)
    {
        var direction = CpuTransitions.Direction(parameters.GetText("direction"));
        var horizontal = direction.Y == 0;
        var extent = horizontal ? frames.Width : frames.Height;
        var softness = Math.Max(0.5f, parameters.GetFloat("softness") / 100f * extent);

        // 境界の帯の中心を、入ってくる側の外から反対側の外まで動かす（0 で画面の外、1 で反対側の外）
        var center = -softness / 2 + progress * (extent + softness);
        float Along(float distance) => direction.X > 0 || direction.Y > 0 ? extent - distance : distance;
        SKPoint At(float distance) => horizontal ? new SKPoint(Along(distance), 0) : new SKPoint(0, Along(distance));
        var start = At(center - softness / 2);
        var end = At(center + softness / 2);

        using var toMask = SKShader.CreateLinearGradient(start, end, [SKColors.White, SKColors.Transparent], SKShaderTileMode.Clamp);
        using var fromMask = SKShader.CreateLinearGradient(start, end, [SKColors.Transparent, SKColors.White], SKShaderTileMode.Clamp);
        CpuTransitions.Mix(
            canvas,
            frames,
            c => TransitionFrames.Draw(c, frames.From, SKMatrix.Identity),
            fromMask,
            c => TransitionFrames.Draw(c, frames.To, SKMatrix.Identity),
            toMask);
    }
}

/// <summary>スライド。後のアイテムが、向きの側から前のアイテムの上に滑り込む</summary>
internal sealed class SlideTransition : ICpuTransition
{
    public string Type => BuiltInTransitions.Slide.Type;

    public void Draw(SKCanvas canvas, TransitionFrames frames, float progress, EffectParameters parameters)
    {
        var direction = CpuTransitions.Direction(parameters.GetText("direction"));
        var remaining = 1 - progress;
        TransitionFrames.Draw(canvas, frames.From, SKMatrix.Identity);
        TransitionFrames.Draw(canvas, frames.To, SKMatrix.CreateTranslation(direction.X * remaining * frames.Width, direction.Y * remaining * frames.Height));
    }
}

/// <summary>押し出し。後のアイテムが向きの側から入り、前のアイテムを反対側へ押し出す</summary>
internal sealed class PushTransition : ICpuTransition
{
    public string Type => BuiltInTransitions.Push.Type;

    public void Draw(SKCanvas canvas, TransitionFrames frames, float progress, EffectParameters parameters)
    {
        var direction = CpuTransitions.Direction(parameters.GetText("direction"));
        var remaining = 1 - progress;
        TransitionFrames.Draw(canvas, frames.From, SKMatrix.CreateTranslation(-direction.X * progress * frames.Width, -direction.Y * progress * frames.Height));
        TransitionFrames.Draw(canvas, frames.To, SKMatrix.CreateTranslation(direction.X * remaining * frames.Width, direction.Y * remaining * frames.Height));
    }
}

/// <summary>拡大縮小。前のアイテムは拡大しながら消え、後のアイテムは小さい状態から元の大きさになりながら現れる</summary>
internal sealed class ZoomTransition : ICpuTransition
{
    public string Type => BuiltInTransitions.Zoom.Type;

    public void Draw(SKCanvas canvas, TransitionFrames frames, float progress, EffectParameters parameters)
    {
        var amount = parameters.GetFloat("amount") / 100f;
        using var fromMask = CpuTransitions.Uniform(1 - progress);
        using var toMask = CpuTransitions.Uniform(progress);
        CpuTransitions.Mix(
            canvas,
            frames,
            c => TransitionFrames.Draw(c, frames.From, frames.AroundCenter(1 + amount * progress, 0)),
            fromMask,
            c => TransitionFrames.Draw(c, frames.To, frames.AroundCenter(1 / (1 + amount * (1 - progress)), 0)),
            toMask);
    }
}

/// <summary>回転。前半で前のアイテムが回りながら小さくなって消え、後半で後のアイテムが回りながら現れる</summary>
internal sealed class SpinTransition : ICpuTransition
{
    public string Type => BuiltInTransitions.Spin.Type;

    public void Draw(SKCanvas canvas, TransitionFrames frames, float progress, EffectParameters parameters)
    {
        var angle = parameters.GetFloat("angle");
        if (progress < 0.5f)
        {
            TransitionFrames.Draw(canvas, frames.From, frames.AroundCenter(1 - progress * 2, angle * progress));
        }
        else
        {
            TransitionFrames.Draw(canvas, frames.To, frames.AroundCenter(progress * 2 - 1, -angle * (1 - progress)));
        }
    }
}
