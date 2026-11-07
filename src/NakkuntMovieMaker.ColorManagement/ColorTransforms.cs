using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using NakkuntMovieMaker.Media;
using NakkuntMovieMaker.Rendering.Color;

namespace NakkuntMovieMaker.ColorManagement;

/// <summary>このソフトが作業に使う色空間（OpenColorIO の組み込みの構成の中の名前）</summary>
public static class WorkingColorSpaces
{
    /// <summary>sRGB のガンマの Rec.709（素材・出力・sRGB のモードの作業用の画像）</summary>
    public const string Srgb = "sRGB Encoded Rec.709 (sRGB)";

    /// <summary>リニアの Rec.709（リニアのモードの作業用の画像）</summary>
    public const string Linear = "Linear Rec.709 (sRGB)";
}

/// <summary>
/// OpenColorIO の変換を、シェーパー（1D）と 3D LUT（65³）に焼く（ColorLut）。
/// 描画では、素材（入力）・プレビュー（表示）・書き出し（出力）のどれも、0〜1 に収まった値（sRGB や PQ のガンマなど）を変換し、
/// 結果も 0〜1 に収めて使うので、入力も出力も 0〜1 に収める。GPU・CPU のどちらも、同じ表を同じ補間で引く
/// </summary>
public static class BakedColorLut
{
    /// <summary>3D LUT の 1 辺の格子の数</summary>
    public const int Size = 65;

    /// <summary>シェーパーの表の長さ</summary>
    public const int ShaperSize = 4096;

    /// <summary>変換を焼く。何もしない変換なら null</summary>
    public static ColorLut? Bake(OcioProcessor processor, int size = Size)
    {
        ArgumentNullException.ThrowIfNull(processor);
        return processor.IsNoOp ? null : Bake([processor], size);
    }

    /// <summary>
    /// 変換を順につないだものを焼く（HDR の素材のトーンマップなど）。
    /// 格子に出力の値をそのまま入れたものと、リニアに戻して入れたもの（引いたあとで sRGB の伝達関数をかける）の 2 つを作り、
    /// 決まった色の組で OCIO そのものと比べて、誤差の小さいほうを使う（出力がリニアか、ガンマのかかった値かで、向くほうが違う）
    /// </summary>
    public static ColorLut Bake(IReadOnlyList<OcioProcessor> chain, int size = Size)
    {
        ArgumentNullException.ThrowIfNull(chain);
        void Apply(Span<float> rgb)
        {
            foreach (var processor in chain)
            {
                processor.Apply(rgb);
            }
        }

        // シェーパーは、出力の値（0〜1 に収めたもの）の変わり方から作る
        var shaper = CreateShaper(rgb => Apply(rgb));
        var inputs = new float[size];
        for (var i = 0; i < size; i++)
        {
            inputs[i] = Unshape(shaper, i / (float)(size - 1));
        }

        var table = new Vector3[size * size * size];
        for (int b = 0, i = 0; b < size; b++)
        {
            for (var g = 0; g < size; g++)
            {
                for (var r = 0; r < size; r++, i++)
                {
                    table[i] = new Vector3(inputs[r], inputs[g], inputs[b]);
                }
            }
        }

        // 格子の値は 0〜1 に収めない（色域の外で折れ曲がらないように）。引いたあとで収める
        Apply(MemoryMarshal.Cast<Vector3, float>(table.AsSpan()));
        var direct = new ColorLut(shaper, CubeLut.FromTable(size, table, Vector3.Zero, Vector3.One), encodeSrgb: false);
        var linear = new Vector3[table.Length];
        for (var i = 0; i < table.Length; i++)
        {
            var value = table[i];
            linear[i] = new Vector3(SrgbTransfer.Decode(value.X), SrgbTransfer.Decode(value.Y), SrgbTransfer.Decode(value.Z));
        }

        var decoded = new ColorLut(shaper, CubeLut.FromTable(size, linear, Vector3.Zero, Vector3.One), encodeSrgb: true);
        var (samples, expected) = Probe(Apply);
        return MaxError(direct, samples, expected) <= MaxError(decoded, samples, expected) ? direct : decoded;
    }

    // 比べるための色（決まった乱数の 4096 色と、灰色の段階）と、OCIO の結果（0〜1 に収めたもの）
    private static (Vector3[] Samples, Vector3[] Expected) Probe(SpanAction apply)
    {
        var random = new Random(20261107);
        var samples = Enumerable.Range(0, 4096).Select(_ => new Vector3(random.NextSingle(), random.NextSingle(), random.NextSingle()))
            .Concat(Enumerable.Range(0, 256).Select(i => new Vector3(i / 255f)))
            .ToArray();
        var expected = samples.ToArray();
        apply(MemoryMarshal.Cast<Vector3, float>(expected.AsSpan()));
        for (var i = 0; i < expected.Length; i++)
        {
            expected[i] = Vector3.Clamp(expected[i], Vector3.Zero, Vector3.One);
        }

        return (samples, expected);
    }

    private static float MaxError(ColorLut lut, Vector3[] samples, Vector3[] expected)
    {
        var worst = 0f;
        for (var i = 0; i < samples.Length; i++)
        {
            var difference = Vector3.Abs(lut.Sample(samples[i]) - expected[i]);
            worst = Math.Max(worst, Math.Max(difference.X, Math.Max(difference.Y, difference.Z)));
        }

        return worst;
    }

    // 灰色（R = G = B）の入力に対する出力の変わり方から、シェーパーを作る。出力が大きく変わるところほど、u が速く進む
    // （どこにも少しは格子が残るように、変わり方の合計の 1 割を一様に足す）
    private static float[] CreateShaper(SpanAction apply)
    {
        var grays = new float[ShaperSize * 3];
        for (var i = 0; i < ShaperSize; i++)
        {
            var t = i / (float)(ShaperSize - 1);
            grays[i * 3] = grays[i * 3 + 1] = grays[i * 3 + 2] = t * t;
        }

        apply(grays);
        var steps = new double[ShaperSize];
        var total = 0.0;
        for (var i = 1; i < ShaperSize; i++)
        {
            var change = 0.0;
            for (var c = 0; c < 3; c++)
            {
                change = Math.Max(change, Math.Abs(Math.Clamp(grays[i * 3 + c], 0, 1) - Math.Clamp(grays[(i - 1) * 3 + c], 0, 1)));
            }

            steps[i] = change;
            total += change;
        }

        var shaper = new float[ShaperSize];
        var uniform = Math.Max(total, 1e-6) * 0.1;
        var sum = 0.0;
        for (var i = 1; i < ShaperSize; i++)
        {
            // 入力 x の幅に比例した一様な分（x = t² なので、t の幅ではなく x の幅で足す）
            var t0 = (i - 1) / (double)(ShaperSize - 1);
            var t1 = i / (double)(ShaperSize - 1);
            sum += steps[i] + uniform * (t1 * t1 - t0 * t0);
            shaper[i] = (float)sum;
        }

        for (var i = 0; i < ShaperSize; i++)
        {
            shaper[i] /= (float)sum;
        }

        shaper[^1] = 1;
        return shaper;
    }

    private delegate void SpanAction(Span<float> rgb);

    // シェーパーの逆：u になる入力 x（表を二分探索して、線形に補間する）
    private static float Unshape(float[] shaper, float u)
    {
        var high = Array.BinarySearch(shaper, u);
        if (high >= 0)
        {
            var exact = high / (float)(shaper.Length - 1);
            return exact * exact;
        }

        high = Math.Clamp(~high, 1, shaper.Length - 1);
        var low = high - 1;
        var fraction = (u - shaper[low]) / Math.Max(shaper[high] - shaper[low], 1e-12f);
        var t = (low + fraction) / (shaper.Length - 1);
        return t * t;
    }
}

/// <summary>
/// 色の変換の LUT を作って覚えておく（構成ごと・変換ごと）。構成は、プロジェクトの設定の名前（null なら OCIO の既定）で読む。
/// 複数のスレッドから使ってよい
/// </summary>
public sealed class ColorTransforms : IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<OcioConfig>> _configs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<TransformKey, Lazy<ColorLut?>> _luts = new();

    /// <summary>アプリ全体で共有するもの</summary>
    public static ColorTransforms Shared { get; } = new();

    /// <summary>構成（configName が null なら OCIO の既定の組み込みの構成）</summary>
    public OcioConfig Config(string? configName)
    {
        var name = configName ?? Ocio.DefaultBuiltinConfig;
        return _configs.GetOrAdd(name, key => new Lazy<OcioConfig>(() => OcioConfig.LoadBuiltin(key))).Value;
    }

    /// <summary>色空間 source から destination への変換の LUT。何もしない変換なら null</summary>
    /// <exception cref="OcioException">構成・色空間が見つからない、nmm_ocio が読めない</exception>
    public ColorLut? Transform(string? configName, string source, string destination)
    {
        if (string.Equals(source, destination, StringComparison.Ordinal))
        {
            return null;
        }

        var key = new TransformKey(configName, source, destination, null, false);
        return Get(key, config =>
        {
            using var processor = config.CreateProcessor(source, destination);
            return BakedColorLut.Bake(processor);
        });
    }

    /// <summary>色空間 source から、ディスプレイとビューへの変換の LUT（inverse なら、その逆）。何もしない変換なら null</summary>
    public ColorLut? DisplayView(string? configName, string source, string display, string view, bool inverse = false)
    {
        var key = new TransformKey(configName, source, display, view, inverse);
        return Get(key, config =>
        {
            using var processor = config.CreateDisplayViewProcessor(source, display, view, inverse);
            return BakedColorLut.Bake(processor);
        });
    }

    /// <summary>
    /// トーンマップ：ディスプレイ sourceDisplay・ビュー sourceView で表示するための値（HDR の動画など）を、
    /// いったんシーンの光（ACEScg）に戻してから、ディスプレイ display・ビュー view で表示する値にする
    /// </summary>
    public ColorLut? ToneMap(string? configName, string sourceDisplay, string sourceView, string display, string view)
    {
        var key = new TransformKey(configName, sourceDisplay + " / " + sourceView, display, view, true);
        return Get(key, config =>
        {
            using var toScene = config.CreateDisplayViewProcessor(SceneLinear, sourceDisplay, sourceView, inverse: true);
            using var toDisplay = config.CreateDisplayViewProcessor(SceneLinear, display, view);
            return BakedColorLut.Bake([toScene, toDisplay]);
        });
    }

    /// <summary>
    /// 素材（入力の色空間 source）を、作業用の sRGB にする変換。HDR（PQ・HLG）のディスプレイの色空間はトーンマップし、
    /// ほかのディスプレイの色空間は sRGB のディスプレイへ、シーンの色空間は作業用の sRGB へ、そのまま変換する。何もしない変換なら null
    /// </summary>
    public ColorLut? Input(string? configName, string source)
    {
        var config = Config(configName);
        var canonical = config.CanonicalColorSpace(source) ?? throw new OcioException($"色空間「{source}」が構成「{config.Name}」にありません");
        if (!config.Displays.Contains(canonical))
        {
            return Transform(configName, canonical, WorkingColorSpaces.Srgb);
        }

        if (IsHdr(canonical) && HdrView(config, canonical) is { } hdrView && SdrView(config) is { } sdrView)
        {
            return ToneMap(configName, canonical, hdrView, SrgbDisplay, sdrView);
        }

        return Transform(configName, canonical, SrgbDisplay);
    }

    /// <summary>
    /// 描き終えた画像（作業用の sRGB）を、色空間 destination にする変換（書き出し・プレビューの表示）。
    /// ディスプレイの色空間へは sRGB のディスプレイから変換する。何もしない変換なら null
    /// </summary>
    public ColorLut? Output(string? configName, string destination)
    {
        var config = Config(configName);
        var canonical = config.CanonicalColorSpace(destination) ?? throw new OcioException($"色空間「{destination}」が構成「{config.Name}」にありません");
        return Transform(configName, config.Displays.Contains(canonical) ? SrgbDisplay : WorkingColorSpaces.Srgb, canonical);
    }

    /// <summary>
    /// 動画の色のタグから決めた入力の色空間の候補を、組み込みの構成の色空間の名前にする。変換しなくてよい（sRGB・BT.709）か、
    /// 組み込みの構成に合う色空間がない（BT.2020 の SDR・HLG）ときは null（そのまま描く）
    /// </summary>
    public static string? AutomaticInput(InputColorSpace space) => space switch
    {
        InputColorSpace.DisplayP3 => "Display P3 - Display",
        InputColorSpace.Rec2100Pq => "Rec.2100-PQ - Display",
        _ => null,
    };

    private const string SrgbDisplay = "sRGB - Display";

    private static bool IsHdr(string display) =>
        display.Contains("PQ", StringComparison.Ordinal) || display.Contains("HDR", StringComparison.Ordinal)
        || display.Contains("ST2084", StringComparison.Ordinal) || display.Contains("HLG", StringComparison.Ordinal);

    // HDR のディスプレイのビュー（"HDR" を含むもの。Rec.2020 があればそれ）
    private static string? HdrView(OcioConfig config, string display)
    {
        var views = config.Views(display).Where(view => view.Contains("HDR", StringComparison.Ordinal)).ToList();
        return views.FirstOrDefault(view => view.Contains("Rec.2020", StringComparison.Ordinal)) ?? views.FirstOrDefault();
    }

    // sRGB のディスプレイの SDR のビュー
    private static string? SdrView(OcioConfig config) =>
        config.Views(SrgbDisplay).FirstOrDefault(view => view.Contains("SDR", StringComparison.Ordinal)) ?? config.DefaultView(SrgbDisplay);

    // トーンマップでつなぐシーンの色空間
    private const string SceneLinear = "ACEScg";

    public void Dispose()
    {
        foreach (var config in _configs.Values.Where(config => config.IsValueCreated))
        {
            config.Value.Dispose();
        }

        _configs.Clear();
        _luts.Clear();
    }

    // 失敗した変換は覚えない（設定を直したら、作り直せるように）
    private ColorLut? Get(TransformKey key, Func<OcioConfig, ColorLut?> bake)
    {
        var lazy = _luts.GetOrAdd(key, k => new Lazy<ColorLut?>(() => bake(Config(k.Config))));
        try
        {
            return lazy.Value;
        }
        catch
        {
            _luts.TryRemove(KeyValuePair.Create(key, lazy));
            throw;
        }
    }

    private readonly record struct TransformKey(string? Config, string Source, string Destination, string? View, bool Inverse);
}
