using SkiaSharp;

namespace NakkuntMovieMaker.Rendering.Skia.Effects;

/// <summary>
/// エフェクトをかける途中の画像（BGRA・premultiplied）。ローカル座標を Scale 倍した「ピクセル座標」で置き場所を持ち、
/// ビットマップの左上は、ピクセル座標の (PixelLeft, PixelTop)。ローカル座標の原点は、ピクセル座標の (0, 0)。
/// 置き場所を整数で持つので、エフェクトが画像を広げても、ずれずに重ねられる
/// </summary>
internal sealed unsafe class EffectImage : IDisposable
{
    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.None);

    private EffectImage(SKBitmap bitmap, int pixelLeft, int pixelTop, float scale)
    {
        Bitmap = bitmap;
        PixelLeft = pixelLeft;
        PixelTop = pixelTop;
        Scale = scale;
    }

    public SKBitmap Bitmap { get; }

    public int PixelLeft { get; }

    public int PixelTop { get; }

    /// <summary>ローカル座標 1 あたりのピクセルの数</summary>
    public float Scale { get; }

    public int Width => Bitmap.Width;

    public int Height => Bitmap.Height;

    /// <summary>ピクセル座標での範囲</summary>
    public SKRectI PixelBounds => new(PixelLeft, PixelTop, PixelLeft + Width, PixelTop + Height);

    /// <summary>ローカル座標での範囲</summary>
    public SKRect LocalBounds => new(PixelLeft / Scale, PixelTop / Scale, (PixelLeft + Width) / Scale, (PixelTop + Height) / Scale);

    public byte* Pixels => (byte*)Bitmap.GetPixels();

    public int RowBytes => Bitmap.RowBytes;

    /// <summary>ピクセル座標の範囲（空なら 1×1）の、透明な画像を作る</summary>
    public static EffectImage Create(SKRectI pixelBounds, float scale)
    {
        var width = Math.Max(1, pixelBounds.Width);
        var height = Math.Max(1, pixelBounds.Height);
        // sRGB として扱う（リニアのモードで重ねるとき、Skia がリニアに変換する）
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul, SkiaCpuBackend.Srgb));
        bitmap.Erase(SKColors.Transparent);
        return new EffectImage(bitmap, pixelBounds.Left, pixelBounds.Top, scale);
    }

    /// <summary>ローカル座標の範囲を含む画像を作る</summary>
    public static EffectImage Create(SKRect localBounds, float scale) => Create(ToPixels(localBounds, scale), scale);

    /// <summary>ローカル座標の範囲を、それを含むピクセル座標の範囲にする</summary>
    public static SKRectI ToPixels(SKRect localBounds, float scale) => new(
        (int)Math.Floor(localBounds.Left * scale),
        (int)Math.Floor(localBounds.Top * scale),
        (int)Math.Ceiling(localBounds.Right * scale),
        (int)Math.Ceiling(localBounds.Bottom * scale));

    /// <summary>同じ大きさ・置き場所の透明な画像</summary>
    public EffectImage CreateLike() => Create(PixelBounds, Scale);

    /// <summary>上下左右に margin ピクセル広げて、今の画像を写したもの</summary>
    public EffectImage Expand(int margin)
    {
        var bounds = PixelBounds;
        bounds.Inflate(margin, margin);
        var expanded = Create(bounds, Scale);
        using var canvas = expanded.OpenPixelCanvas();
        DrawPixels(canvas, null);
        return expanded;
    }

    /// <summary>この画像に、ピクセル座標で描くためのキャンバス</summary>
    public SKCanvas OpenPixelCanvas()
    {
        var canvas = new SKCanvas(Bitmap);
        canvas.Translate(-PixelLeft, -PixelTop);
        return canvas;
    }

    /// <summary>この画像に、ローカル座標で描くためのキャンバス</summary>
    public SKCanvas OpenLocalCanvas()
    {
        var canvas = OpenPixelCanvas();
        canvas.Scale(Scale);
        return canvas;
    }

    /// <summary>ピクセル座標で描くキャンバス（OpenPixelCanvas で開いたもの）に、この画像を置き場所どおりに描く</summary>
    public void DrawPixels(SKCanvas canvas, SKPaint? paint)
    {
        using var image = AsImage();
        canvas.DrawImage(image, PixelLeft, PixelTop, paint);
    }

    /// <summary>ローカル座標のキャンバスに、この画像を置き場所どおりに描く</summary>
    public void DrawLocal(SKCanvas canvas, SKPaint? paint)
    {
        using var image = AsImage();
        canvas.DrawImage(image, LocalBounds, Sampling, paint);
    }

    /// <summary>画素を写さずに、Skia の画像として扱う（この画像を変えると、そちらも変わる）</summary>
    public SKImage AsImage() => SKImage.FromPixels(Bitmap.PeekPixels());

    public void Dispose() => Bitmap.Dispose();
}
