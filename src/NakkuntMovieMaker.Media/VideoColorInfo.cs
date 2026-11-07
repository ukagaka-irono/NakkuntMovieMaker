namespace NakkuntMovieMaker.Media;

/// <summary>原色（色域）。値は ITU-T H.273 の ColourPrimaries と同じ</summary>
public enum VideoColorPrimaries
{
    Bt709 = 1,
    Bt470M = 4,

    /// <summary>BT.601 625 本（PAL）</summary>
    Bt470BG = 5,

    /// <summary>BT.601 525 本（NTSC）</summary>
    Smpte170M = 6,
    Smpte240M = 7,
    Film = 8,
    Bt2020 = 9,
    Smpte428 = 10,

    /// <summary>DCI-P3（SMPTE RP 431-2）</summary>
    DciP3 = 11,

    /// <summary>Display P3（SMPTE EG 432-1、P3-D65）</summary>
    DisplayP3 = 12,
    Ebu3213 = 22,
}

/// <summary>伝達特性（ガンマ）。値は ITU-T H.273 の TransferCharacteristics と同じ</summary>
public enum VideoTransferCharacteristic
{
    Bt709 = 1,
    Gamma22 = 4,
    Gamma28 = 5,
    Smpte170M = 6,
    Smpte240M = 7,
    Linear = 8,
    Log100 = 9,
    Log316 = 10,
    Iec61966_2_4 = 11,
    Bt1361 = 12,

    /// <summary>sRGB（IEC 61966-2-1）</summary>
    Srgb = 13,
    Bt2020_10 = 14,
    Bt2020_12 = 15,

    /// <summary>PQ（SMPTE ST 2084、BT.2100）</summary>
    Pq = 16,
    Smpte428 = 17,

    /// <summary>HLG（ARIB STD-B67、BT.2100）</summary>
    Hlg = 18,
}

/// <summary>YUV と RGB の間の行列。値は ITU-T H.273 の MatrixCoefficients と同じ</summary>
public enum VideoMatrix
{
    /// <summary>YUV ではなく RGB（GBR）</summary>
    Rgb = 0,
    Bt709 = 1,
    Fcc = 4,

    /// <summary>BT.601 625 本（PAL）。係数は Smpte170M と同じ</summary>
    Bt470BG = 5,

    /// <summary>BT.601 525 本（NTSC）</summary>
    Smpte170M = 6,
    Smpte240M = 7,
    YCgCo = 8,

    /// <summary>BT.2020 の非定輝度（ふつうの BT.2020・BT.2100 の YCbCr）</summary>
    Bt2020Ncl = 9,

    /// <summary>BT.2020 の定輝度（行列では表せない。変換は Bt2020Ncl の係数で近似する）</summary>
    Bt2020Cl = 10,
    Smpte2085 = 11,
    ChromaDerivedNcl = 12,
    ChromaDerivedCl = 13,
    ICtCp = 14,
}

/// <summary>値の範囲</summary>
public enum VideoColorRange
{
    /// <summary>リミテッド（8 ビットで Y が 16〜235、色差が 16〜240）</summary>
    Limited,

    /// <summary>フル（0〜255）</summary>
    Full,
}

/// <summary>色差の標本の位置（4:2:0 などで、輝度の標本に対してどこにあるか）。値は FFmpeg の AVChromaLocation と同じ</summary>
public enum VideoChromaLocation
{
    Unspecified = 0,

    /// <summary>左（MPEG-2・H.264・HEVC の既定）</summary>
    Left = 1,

    /// <summary>中央（JPEG・MPEG-1）</summary>
    Center = 2,

    /// <summary>左上（BT.2020 の 4:2:0 で使う）</summary>
    TopLeft = 3,
    Top = 4,
    BottomLeft = 5,
    Bottom = 6,
}

/// <summary>タグがなく、推測で決めた項目</summary>
[Flags]
public enum VideoColorGuesses
{
    None = 0,
    Primaries = 1,
    Transfer = 2,
    Matrix = 4,
    Range = 8,
}

/// <summary>CIE 1931 の xy 色度</summary>
public readonly record struct Chromaticity(double X, double Y);

/// <summary>
/// マスタリングに使ったディスプレイの情報（SMPTE ST 2086）。輝度は cd/m²
/// </summary>
public sealed record MasteringDisplayInfo(
    Chromaticity? Red,
    Chromaticity? Green,
    Chromaticity? Blue,
    Chromaticity? WhitePoint,
    double? MinLuminance,
    double? MaxLuminance);

/// <summary>コンテンツの明るさ（CTA-861.3）。MaxCll は最も明るい画素、MaxFall はフレームの平均の最大（cd/m²）</summary>
public sealed record ContentLightLevelInfo(int MaxCll, int MaxFall);

/// <summary>
/// 色の解釈に使う、後で OCIO の入力の色空間を決めるための候補。<see cref="InputColorSpaceNames"/> で名前にする
/// </summary>
public enum InputColorSpace
{
    /// <summary>sRGB・BT.709（SDR。BT.601 の素材もここに入れる）</summary>
    SrgbRec709,

    /// <summary>BT.2020 の原色の SDR</summary>
    Rec2020,

    /// <summary>P3 の原色の SDR（Display P3・DCI-P3）</summary>
    DisplayP3,

    /// <summary>BT.2100 PQ（HDR10 など）</summary>
    Rec2100Pq,

    /// <summary>BT.2100 HLG</summary>
    Rec2100Hlg,
}

/// <summary><see cref="InputColorSpace"/> の名前（OCIO の構成の色空間を探すときの手がかり。OCIO の名前そのものではない）</summary>
public static class InputColorSpaceNames
{
    public static string Name(this InputColorSpace space) => space switch
    {
        InputColorSpace.SrgbRec709 => "sRGB/Rec.709",
        InputColorSpace.Rec2020 => "Rec.2020",
        InputColorSpace.DisplayP3 => "Display P3",
        InputColorSpace.Rec2100Pq => "Rec.2100-PQ",
        InputColorSpace.Rec2100Hlg => "Rec.2100-HLG",
        _ => throw new ArgumentOutOfRangeException(nameof(space), space, null),
    };
}

/// <summary>
/// 映像の色の情報。タグがない項目は推測した値を入れ、<see cref="Guessed"/> に記録する。
/// 推測のしかた：行列は高さ 720 以上なら BT.709、それ未満なら BT.601（RGB の素材は RGB）。
/// 原色は行列に合わせる。伝達特性は YUV なら BT.709、RGB なら sRGB。範囲は YUV ならリミテッド、RGB ならフル
/// </summary>
public sealed record VideoColorInfo(
    VideoColorPrimaries Primaries,
    VideoTransferCharacteristic Transfer,
    VideoMatrix Matrix,
    VideoColorRange Range,
    VideoChromaLocation ChromaLocation,
    MasteringDisplayInfo? MasteringDisplay = null,
    ContentLightLevelInfo? ContentLightLevel = null,
    VideoColorGuesses Guessed = VideoColorGuesses.None)
{
    /// <summary>タグのない sRGB の画像として扱うときの値（色の情報がまだ分からないときの既定値）</summary>
    public static VideoColorInfo Srgb { get; } = new(
        VideoColorPrimaries.Bt709,
        VideoTransferCharacteristic.Srgb,
        VideoMatrix.Rgb,
        VideoColorRange.Full,
        VideoChromaLocation.Unspecified,
        Guessed: VideoColorGuesses.Primaries | VideoColorGuesses.Transfer | VideoColorGuesses.Matrix | VideoColorGuesses.Range);

    /// <summary>HDR（PQ・HLG）か</summary>
    public bool IsHdr => Transfer is VideoTransferCharacteristic.Pq or VideoTransferCharacteristic.Hlg;

    /// <summary>入力の色空間の候補（伝達特性が PQ・HLG なら BT.2100、そうでなければ原色で決める）</summary>
    public InputColorSpace SuggestedInputColorSpace => Transfer switch
    {
        VideoTransferCharacteristic.Pq => InputColorSpace.Rec2100Pq,
        VideoTransferCharacteristic.Hlg => InputColorSpace.Rec2100Hlg,
        _ => Primaries switch
        {
            VideoColorPrimaries.Bt2020 => InputColorSpace.Rec2020,
            VideoColorPrimaries.DciP3 or VideoColorPrimaries.DisplayP3 => InputColorSpace.DisplayP3,
            _ => InputColorSpace.SrgbRec709,
        },
    };

    /// <summary>
    /// タグのない映像の行列の推測：高さ 720 以上なら BT.709、それ未満なら BT.601
    /// </summary>
    public static VideoMatrix GuessMatrix(int height) => height >= 720 ? VideoMatrix.Bt709 : VideoMatrix.Smpte170M;

    /// <summary>
    /// タグの値（未指定は null）から、推測を補った色の情報を作る。isRgb は画素の形式が RGB か、
    /// fullRangeFormat は画素の形式そのものがフルレンジ（yuvj420p など）か
    /// </summary>
    public static VideoColorInfo Resolve(
        VideoColorPrimaries? primaries,
        VideoTransferCharacteristic? transfer,
        VideoMatrix? matrix,
        VideoColorRange? range,
        VideoChromaLocation chromaLocation,
        int height,
        bool isRgb,
        bool fullRangeFormat = false,
        MasteringDisplayInfo? masteringDisplay = null,
        ContentLightLevelInfo? contentLightLevel = null)
    {
        var guessed = VideoColorGuesses.None;

        if (isRgb)
        {
            // RGB の素材に YUV の行列のタグがあっても使わない
            matrix = VideoMatrix.Rgb;
        }
        else if (matrix is null or VideoMatrix.Rgb)
        {
            matrix = GuessMatrix(height);
            guessed |= VideoColorGuesses.Matrix;
        }

        if (fullRangeFormat)
        {
            range = VideoColorRange.Full;
        }
        else if (range is null)
        {
            range = isRgb ? VideoColorRange.Full : VideoColorRange.Limited;
            guessed |= VideoColorGuesses.Range;
        }

        if (primaries is null)
        {
            primaries = matrix switch
            {
                VideoMatrix.Bt2020Ncl or VideoMatrix.Bt2020Cl => VideoColorPrimaries.Bt2020,
                VideoMatrix.Bt470BG => VideoColorPrimaries.Bt470BG,
                VideoMatrix.Smpte170M => height == 576 ? VideoColorPrimaries.Bt470BG : VideoColorPrimaries.Smpte170M,
                VideoMatrix.Smpte240M => VideoColorPrimaries.Smpte240M,
                _ => VideoColorPrimaries.Bt709,
            };
            guessed |= VideoColorGuesses.Primaries;
        }

        if (transfer is null)
        {
            transfer = isRgb ? VideoTransferCharacteristic.Srgb : VideoTransferCharacteristic.Bt709;
            guessed |= VideoColorGuesses.Transfer;
        }

        return new VideoColorInfo(
            primaries.Value, transfer.Value, matrix.Value, range.Value, chromaLocation, masteringDisplay, contentLightLevel, guessed);
    }
}
