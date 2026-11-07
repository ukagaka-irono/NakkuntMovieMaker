using System.Collections.Frozen;
using System.Numerics;
using System.Runtime.InteropServices;
using NakkuntMovieMaker.Rendering.Effects;
using NakkuntMovieMaker.Rendering.Transitions;

namespace NakkuntMovieMaker.Rendering.Vulkan;

/// <summary>
/// トランジションの GPU での処理（CPU 版の CpuTransitions と同じ）。From・To は、それぞれのアイテムを出力と同じ大きさの透明な画像に描いたもの。
/// target（出力の作業用の画像）に、結果を上に重ねる。Progress は 0（すべて From）〜 1（すべて To）
/// </summary>
internal interface IGpuTransition
{
    string Type { get; }

    void Draw(GpuEffectContext context, GpuEffectImage target, GpuEffectImage from, GpuEffectImage to, float progress, EffectParameters parameters);
}

/// <summary>組み込みのトランジションの GPU での処理の一覧（まだないものは、CPU 版で描く）</summary>
internal static class GpuTransitions
{
    private static readonly FrozenDictionary<string, IGpuTransition> ByType = new IGpuTransition[]
    {
        new CrossfadeGpu(), new DipGpu(), new WipeGpu(), new SlideGpu(), new PushGpu(), new ZoomGpu(), new SpinGpu(),
    }.ToFrozenDictionary(transition => transition.Type, StringComparer.Ordinal);

    public static IGpuTransition? Find(string type) => ByType.GetValueOrDefault(type);

    /// <summary>不透明度を、CPU 版（Skia の 8 ビットの不透明度）と同じく 1/255 の単位にする</summary>
    public static float Uniform(float alpha) => (float)Math.Round(Math.Clamp(alpha, 0, 1) * 255) / 255f;

    /// <summary>出力の中心を基準に、拡大して回す変形（画像の画素 → 出力の画素。CPU 版の AroundCenter と同じ）</summary>
    public static Matrix3x2 AroundCenter(GpuEffectImage frame, float scale, float degrees)
    {
        var center = new Vector2(frame.Width / 2f, frame.Height / 2f);
        return Matrix3x2.CreateTranslation(-center) * Matrix3x2.CreateScale(scale) * Matrix3x2.CreateRotation(degrees * MathF.PI / 180f) * Matrix3x2.CreateTranslation(center);
    }

    /// <summary>画像を変形して target に重ねる（線形の補間と縁のアンチエイリアス。変形が潰れていれば何もしない）</summary>
    public static void Draw(GpuEffectContext context, GpuEffectImage target, GpuEffectImage frame, Matrix3x2 matrix)
    {
        if (matrix.IsIdentity)
        {
            context.Combine(target, frame, CombineMode.SourceOver);
        }
        else if (Matrix3x2.Invert(matrix, out var inverse))
        {
            context.Composite(target, frame, inverse);
        }
    }

    /// <summary>
    /// From·a + To·b を target の上に重ねる（色は premultiplied なので、透明な部分もそのまま混ざる）。
    /// a・b は、一様な値か、軸に沿った線形のグラデーション（ワイプ）
    /// </summary>
    public static void Mix(GpuEffectContext context, GpuEffectImage target, GpuEffectImage from, GpuEffectImage to, in MixPush push)
    {
        context.Shaders.Get("fx_transition_mix", [GpuBindingKind.StorageBuffer, GpuBindingKind.StorageBuffer, GpuBindingKind.StorageBuffer], Marshal.SizeOf<MixPush>())
            .Dispatch(context.Command, [target.Buffer, from.Buffer, to.Buffer], push, ComputePipeline.Groups(target.Width, 8), ComputePipeline.Groups(target.Height, 8));
        GpuBarriers.AfterCompute(context.Command);
    }

    /// <summary>
    /// fx_transition_mix の値。Mode：0 は一様（FromAlpha・ToAlpha）、1 はグラデーション（座標 Start で To が 1、End で From が 1）、
    /// 2 は From に色を SrcATop で重ねたもの（色は R・G・B・A、straight）
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly record struct MixPush(
        int Width, int Height, int Mode, float FromAlpha, float ToAlpha, int Horizontal, float Start, float End, float R, float G, float B, float A);
}

internal sealed class CrossfadeGpu : IGpuTransition
{
    public string Type => BuiltInTransitions.Crossfade.Type;

    public void Draw(GpuEffectContext context, GpuEffectImage target, GpuEffectImage from, GpuEffectImage to, float progress, EffectParameters parameters) =>
        GpuTransitions.Mix(context, target, from, to,
            new GpuTransitions.MixPush(target.Width, target.Height, 0, GpuTransitions.Uniform(1 - progress), GpuTransitions.Uniform(progress), 0, 0, 0, 0, 0, 0, 0));
}

// 色を挟んだフェード：前半で前のアイテムが色に変わり、後半で色から後のアイテムに変わる（アイテムの形の中だけ）
internal sealed class DipGpu : IGpuTransition
{
    public string Type => BuiltInTransitions.Dip.Type;

    public void Draw(GpuEffectContext context, GpuEffectImage target, GpuEffectImage from, GpuEffectImage to, float progress, EffectParameters parameters)
    {
        var first = progress < 0.5f;
        var amount = first ? progress * 2 : 2 - progress * 2;
        var color = parameters.GetColor("color");
        var alpha = (float)Math.Round(color.A * Math.Clamp(amount, 0, 1)) / 255f;
        GpuTransitions.Mix(context, target, first ? from : to, first ? from : to,
            new GpuTransitions.MixPush(target.Width, target.Height, 2, 0, 0, 0, 0, 0, color.R / 255f, color.G / 255f, color.B / 255f, alpha));
    }
}

// ワイプ：境界の帯が、向きの側から反対側へ動く（CPU 版と同じ帯の位置）
internal sealed class WipeGpu : IGpuTransition
{
    public string Type => BuiltInTransitions.Wipe.Type;

    public void Draw(GpuEffectContext context, GpuEffectImage target, GpuEffectImage from, GpuEffectImage to, float progress, EffectParameters parameters)
    {
        var direction = RenderMath.TransitionDirection(parameters.GetText("direction"));
        var horizontal = direction.Y == 0;
        var extent = horizontal ? target.Width : target.Height;
        var softness = Math.Max(0.5f, parameters.GetFloat("softness") / 100f * extent);
        var center = -softness / 2 + progress * (extent + softness);
        float Along(float distance) => direction.X > 0 || direction.Y > 0 ? extent - distance : distance;
        GpuTransitions.Mix(context, target, from, to,
            new GpuTransitions.MixPush(target.Width, target.Height, 1, 0, 0, horizontal ? 1 : 0, Along(center - softness / 2), Along(center + softness / 2), 0, 0, 0, 0));
    }
}

internal sealed class SlideGpu : IGpuTransition
{
    public string Type => BuiltInTransitions.Slide.Type;

    public void Draw(GpuEffectContext context, GpuEffectImage target, GpuEffectImage from, GpuEffectImage to, float progress, EffectParameters parameters)
    {
        var direction = RenderMath.TransitionDirection(parameters.GetText("direction"));
        var remaining = 1 - progress;
        GpuTransitions.Draw(context, target, from, Matrix3x2.Identity);
        GpuTransitions.Draw(context, target, to, Matrix3x2.CreateTranslation(direction.X * remaining * target.Width, direction.Y * remaining * target.Height));
    }
}

internal sealed class PushGpu : IGpuTransition
{
    public string Type => BuiltInTransitions.Push.Type;

    public void Draw(GpuEffectContext context, GpuEffectImage target, GpuEffectImage from, GpuEffectImage to, float progress, EffectParameters parameters)
    {
        var direction = RenderMath.TransitionDirection(parameters.GetText("direction"));
        var remaining = 1 - progress;
        GpuTransitions.Draw(context, target, from, Matrix3x2.CreateTranslation(-direction.X * progress * target.Width, -direction.Y * progress * target.Height));
        GpuTransitions.Draw(context, target, to, Matrix3x2.CreateTranslation(direction.X * remaining * target.Width, direction.Y * remaining * target.Height));
    }
}

// 拡大縮小：変形した前後の画像を、それぞれ出力と同じ大きさの画像に描いてから混ぜる
internal sealed class ZoomGpu : IGpuTransition
{
    public string Type => BuiltInTransitions.Zoom.Type;

    public void Draw(GpuEffectContext context, GpuEffectImage target, GpuEffectImage from, GpuEffectImage to, float progress, EffectParameters parameters)
    {
        var amount = parameters.GetFloat("amount") / 100f;
        var zoomedFrom = context.CreateClearImage(0, 0, target.Width, target.Height, 1);
        var zoomedTo = context.CreateClearImage(0, 0, target.Width, target.Height, 1);
        context.Release(zoomedFrom);
        context.Release(zoomedTo);
        GpuTransitions.Draw(context, zoomedFrom, from, GpuTransitions.AroundCenter(from, 1 + amount * progress, 0));
        GpuTransitions.Draw(context, zoomedTo, to, GpuTransitions.AroundCenter(to, 1 / (1 + amount * (1 - progress)), 0));
        GpuTransitions.Mix(context, target, zoomedFrom, zoomedTo,
            new GpuTransitions.MixPush(target.Width, target.Height, 0, GpuTransitions.Uniform(1 - progress), GpuTransitions.Uniform(progress), 0, 0, 0, 0, 0, 0, 0));
    }
}

internal sealed class SpinGpu : IGpuTransition
{
    public string Type => BuiltInTransitions.Spin.Type;

    public void Draw(GpuEffectContext context, GpuEffectImage target, GpuEffectImage from, GpuEffectImage to, float progress, EffectParameters parameters)
    {
        var angle = parameters.GetFloat("angle");
        if (progress < 0.5f)
        {
            GpuTransitions.Draw(context, target, from, GpuTransitions.AroundCenter(from, 1 - progress * 2, angle * progress));
        }
        else
        {
            GpuTransitions.Draw(context, target, to, GpuTransitions.AroundCenter(to, progress * 2 - 1, -angle * (1 - progress)));
        }
    }
}
