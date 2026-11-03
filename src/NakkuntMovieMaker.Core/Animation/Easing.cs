namespace NakkuntMovieMaker.Core.Animation;

/// <summary>イージングの曲線の種類</summary>
public enum EasingCurve
{
    /// <summary>一定の速さ</summary>
    Linear,

    /// <summary>次のキーフレームまで値を保ち、そこで一気に変わる（瞬間移動）</summary>
    Hold,

    Sine,
    Quad,
    Cubic,
    Quart,
    Quint,
    Expo,
    Circ,
    Back,
    Elastic,
    Bounce,
}

/// <summary>加速・減速のかけ方</summary>
public enum EasingMode
{
    /// <summary>ゆっくり始まる（加速）</summary>
    In,

    /// <summary>ゆっくり終わる（減速）</summary>
    Out,

    /// <summary>ゆっくり始まり、ゆっくり終わる</summary>
    InOut,
}

/// <summary>
/// キーフレーム間の変化のしかた。進み具合 t（0〜1）を、補間に使う割合に変える。
/// Back と Elastic は途中で 0〜1 の範囲をはみ出す
/// </summary>
public readonly record struct Easing
{
    public static readonly Easing Linear = new(EasingCurve.Linear);
    public static readonly Easing Hold = new(EasingCurve.Hold);

    public Easing(EasingCurve curve, EasingMode mode = EasingMode.InOut)
    {
        Curve = curve;
        // Linear と Hold は向きに意味がないので、default(Easing) と等しくなるように In にそろえる
        Mode = curve is EasingCurve.Linear or EasingCurve.Hold ? EasingMode.In : mode;
    }

    public EasingCurve Curve { get; }

    public EasingMode Mode { get; }

    public double Evaluate(double t)
    {
        t = Math.Clamp(t, 0, 1);

        switch (Curve)
        {
            case EasingCurve.Linear:
                return t;
            case EasingCurve.Hold:
                return t >= 1 ? 1 : 0;
        }

        return Mode switch
        {
            EasingMode.In => EaseIn(Curve, t),
            EasingMode.Out => 1 - EaseIn(Curve, 1 - t),
            EasingMode.InOut => t < 0.5
                ? EaseIn(Curve, 2 * t) / 2
                : 1 - EaseIn(Curve, 2 - 2 * t) / 2,
            _ => throw new InvalidOperationException($"不明なイージングの向きです: {Mode}"),
        };
    }

    /// <summary>保存用の名前。"linear"、"hold"、"cubic-in-out" など</summary>
    public override string ToString() => Curve switch
    {
        EasingCurve.Linear => "linear",
        EasingCurve.Hold => "hold",
        _ => $"{CurveName(Curve)}-{ModeName(Mode)}",
    };

    public static Easing Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (!TryParse(text, out var easing))
        {
            throw new FormatException($"イージングとして読めません: {text}");
        }

        return easing;
    }

    public static bool TryParse(string? text, out Easing easing)
    {
        easing = Linear;
        switch (text)
        {
            case "linear":
                return true;
            case "hold":
                easing = Hold;
                return true;
            case null:
                return false;
        }

        var dash = text.IndexOf('-', StringComparison.Ordinal);
        if (dash <= 0)
        {
            return false;
        }

        var curveName = text[..dash];
        var modeName = text[(dash + 1)..];
        foreach (var curve in Enum.GetValues<EasingCurve>())
        {
            if (curve is EasingCurve.Linear or EasingCurve.Hold || CurveName(curve) != curveName)
            {
                continue;
            }

            foreach (var mode in Enum.GetValues<EasingMode>())
            {
                if (ModeName(mode) == modeName)
                {
                    easing = new Easing(curve, mode);
                    return true;
                }
            }
        }

        return false;
    }

    // 「加速」側の曲線。Out と InOut はこれを折り返して作る（Robert Penner のイージング関数）
    private static double EaseIn(EasingCurve curve, double t)
    {
        const double backOvershoot = 1.70158;

        return curve switch
        {
            EasingCurve.Sine => 1 - Math.Cos(t * Math.PI / 2),
            EasingCurve.Quad => t * t,
            EasingCurve.Cubic => t * t * t,
            EasingCurve.Quart => t * t * t * t,
            EasingCurve.Quint => t * t * t * t * t,
            EasingCurve.Expo => t == 0 ? 0 : Math.Pow(2, 10 * t - 10),
            EasingCurve.Circ => 1 - Math.Sqrt(1 - t * t),
            EasingCurve.Back => (backOvershoot + 1) * t * t * t - backOvershoot * t * t,
            EasingCurve.Elastic => t is 0 or 1
                ? t
                : -Math.Pow(2, 10 * t - 10) * Math.Sin((t * 10 - 10.75) * (2 * Math.PI / 3)),
            EasingCurve.Bounce => 1 - BounceOut(1 - t),
            _ => throw new InvalidOperationException($"不明なイージングの曲線です: {curve}"),
        };
    }

    private static double BounceOut(double t)
    {
        const double n = 7.5625;
        const double d = 2.75;

        if (t < 1 / d)
        {
            return n * t * t;
        }

        if (t < 2 / d)
        {
            t -= 1.5 / d;
            return n * t * t + 0.75;
        }

        if (t < 2.5 / d)
        {
            t -= 2.25 / d;
            return n * t * t + 0.9375;
        }

        t -= 2.625 / d;
        return n * t * t + 0.984375;
    }

    private static string CurveName(EasingCurve curve) => curve.ToString().ToLowerInvariant();

    private static string ModeName(EasingMode mode) => mode switch
    {
        EasingMode.In => "in",
        EasingMode.Out => "out",
        EasingMode.InOut => "in-out",
        _ => throw new InvalidOperationException($"不明なイージングの向きです: {mode}"),
    };
}
