using FFmpeg.AutoGen.Abstractions;

namespace NakkuntMovieMaker.Media;

/// <summary>FFmpeg のフレーム・ストリームの色のタグを <see cref="VideoColorInfo"/> にする</summary>
internal static unsafe class FfmpegColor
{
    /// <summary>
    /// デコードしたフレームの色の情報。フレームにないタグとサイドデータは、ストリームの情報（codecpar）から補う。
    /// format はフレームの画素の形式（ハードウェアのフレームなら、メインメモリに移した後の形式）
    /// </summary>
    public static VideoColorInfo FromFrame(AVFrame* frame, AVPixelFormat format, AVCodecParameters* parameters)
    {
        var mastering = ReadMastering(ffmpeg.av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_MASTERING_DISPLAY_METADATA));
        var light = ReadContentLight(ffmpeg.av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_CONTENT_LIGHT_LEVEL));
        if (parameters is not null)
        {
            mastering ??= ReadMastering(parameters);
            light ??= ReadContentLight(parameters);
        }

        return Resolve(
            Primaries(frame->color_primaries) ?? (parameters is null ? null : Primaries(parameters->color_primaries)),
            Transfer(frame->color_trc) ?? (parameters is null ? null : Transfer(parameters->color_trc)),
            Matrix(frame->colorspace) ?? (parameters is null ? null : Matrix(parameters->color_space)),
            Range(frame->color_range) ?? (parameters is null ? null : Range(parameters->color_range)),
            ChromaLocation(frame->chroma_location) ?? (parameters is null ? VideoChromaLocation.Unspecified : ChromaLocation(parameters->chroma_location) ?? VideoChromaLocation.Unspecified),
            format,
            frame->height,
            mastering,
            light);
    }

    /// <summary>ストリームの情報（codecpar）から読んだ色の情報</summary>
    public static VideoColorInfo FromParameters(AVCodecParameters* parameters) =>
        Resolve(
            Primaries(parameters->color_primaries),
            Transfer(parameters->color_trc),
            Matrix(parameters->color_space),
            Range(parameters->color_range),
            ChromaLocation(parameters->chroma_location) ?? VideoChromaLocation.Unspecified,
            (AVPixelFormat)parameters->format,
            parameters->height,
            ReadMastering(parameters),
            ReadContentLight(parameters));

    /// <summary>swscale の YUV → RGB の係数の番号（SWS_CS_*）</summary>
    public static int SwsColorspace(VideoColorInfo color, int height) => color.Matrix switch
    {
        VideoMatrix.Bt709 => ffmpeg.SWS_CS_ITU709,
        VideoMatrix.Bt470BG or VideoMatrix.Smpte170M => ffmpeg.SWS_CS_ITU601,
        VideoMatrix.Smpte240M => ffmpeg.SWS_CS_SMPTE240M,
        VideoMatrix.Fcc => ffmpeg.SWS_CS_FCC,

        // 定輝度（CL）は行列では表せないので、非定輝度（NCL）の係数で近似する
        VideoMatrix.Bt2020Ncl or VideoMatrix.Bt2020Cl => ffmpeg.SWS_CS_BT2020,

        // swscale が扱えない行列（YCgCo・ICtCp など）は、タグがないときと同じく大きさで決める
        _ => VideoColorInfo.GuessMatrix(height) == VideoMatrix.Bt709 ? ffmpeg.SWS_CS_ITU709 : ffmpeg.SWS_CS_ITU601,
    };

    private static VideoColorInfo Resolve(
        VideoColorPrimaries? primaries,
        VideoTransferCharacteristic? transfer,
        VideoMatrix? matrix,
        VideoColorRange? range,
        VideoChromaLocation chromaLocation,
        AVPixelFormat format,
        int height,
        MasteringDisplayInfo? mastering,
        ContentLightLevelInfo? light)
    {
        var descriptor = ffmpeg.av_pix_fmt_desc_get(format);
        var isRgb = descriptor is not null && (descriptor->flags & ffmpeg.AV_PIX_FMT_FLAG_RGB) != 0;
        var name = ffmpeg.av_get_pix_fmt_name(format) ?? "";
        var fullRangeFormat = name.StartsWith("yuvj", StringComparison.Ordinal);
        return VideoColorInfo.Resolve(primaries, transfer, matrix, range, chromaLocation, height, isRgb, fullRangeFormat, mastering, light);
    }

    private static VideoColorPrimaries? Primaries(AVColorPrimaries value) => value switch
    {
        AVColorPrimaries.AVCOL_PRI_BT709 => VideoColorPrimaries.Bt709,
        AVColorPrimaries.AVCOL_PRI_BT470M => VideoColorPrimaries.Bt470M,
        AVColorPrimaries.AVCOL_PRI_BT470BG => VideoColorPrimaries.Bt470BG,
        AVColorPrimaries.AVCOL_PRI_SMPTE170M => VideoColorPrimaries.Smpte170M,
        AVColorPrimaries.AVCOL_PRI_SMPTE240M => VideoColorPrimaries.Smpte240M,
        AVColorPrimaries.AVCOL_PRI_FILM => VideoColorPrimaries.Film,
        AVColorPrimaries.AVCOL_PRI_BT2020 => VideoColorPrimaries.Bt2020,
        AVColorPrimaries.AVCOL_PRI_SMPTE428 => VideoColorPrimaries.Smpte428,
        AVColorPrimaries.AVCOL_PRI_SMPTE431 => VideoColorPrimaries.DciP3,
        AVColorPrimaries.AVCOL_PRI_SMPTE432 => VideoColorPrimaries.DisplayP3,
        AVColorPrimaries.AVCOL_PRI_EBU3213 => VideoColorPrimaries.Ebu3213,
        _ => null,
    };

    private static VideoTransferCharacteristic? Transfer(AVColorTransferCharacteristic value) => value switch
    {
        AVColorTransferCharacteristic.AVCOL_TRC_BT709 => VideoTransferCharacteristic.Bt709,
        AVColorTransferCharacteristic.AVCOL_TRC_GAMMA22 => VideoTransferCharacteristic.Gamma22,
        AVColorTransferCharacteristic.AVCOL_TRC_GAMMA28 => VideoTransferCharacteristic.Gamma28,
        AVColorTransferCharacteristic.AVCOL_TRC_SMPTE170M => VideoTransferCharacteristic.Smpte170M,
        AVColorTransferCharacteristic.AVCOL_TRC_SMPTE240M => VideoTransferCharacteristic.Smpte240M,
        AVColorTransferCharacteristic.AVCOL_TRC_LINEAR => VideoTransferCharacteristic.Linear,
        AVColorTransferCharacteristic.AVCOL_TRC_LOG => VideoTransferCharacteristic.Log100,
        AVColorTransferCharacteristic.AVCOL_TRC_LOG_SQRT => VideoTransferCharacteristic.Log316,
        AVColorTransferCharacteristic.AVCOL_TRC_IEC61966_2_4 => VideoTransferCharacteristic.Iec61966_2_4,
        AVColorTransferCharacteristic.AVCOL_TRC_BT1361_ECG => VideoTransferCharacteristic.Bt1361,
        AVColorTransferCharacteristic.AVCOL_TRC_IEC61966_2_1 => VideoTransferCharacteristic.Srgb,
        AVColorTransferCharacteristic.AVCOL_TRC_BT2020_10 => VideoTransferCharacteristic.Bt2020_10,
        AVColorTransferCharacteristic.AVCOL_TRC_BT2020_12 => VideoTransferCharacteristic.Bt2020_12,
        AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084 => VideoTransferCharacteristic.Pq,
        AVColorTransferCharacteristic.AVCOL_TRC_SMPTE428 => VideoTransferCharacteristic.Smpte428,
        AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67 => VideoTransferCharacteristic.Hlg,
        _ => null,
    };

    private static VideoMatrix? Matrix(AVColorSpace value) => value switch
    {
        AVColorSpace.AVCOL_SPC_RGB => VideoMatrix.Rgb,
        AVColorSpace.AVCOL_SPC_BT709 => VideoMatrix.Bt709,
        AVColorSpace.AVCOL_SPC_FCC => VideoMatrix.Fcc,
        AVColorSpace.AVCOL_SPC_BT470BG => VideoMatrix.Bt470BG,
        AVColorSpace.AVCOL_SPC_SMPTE170M => VideoMatrix.Smpte170M,
        AVColorSpace.AVCOL_SPC_SMPTE240M => VideoMatrix.Smpte240M,
        AVColorSpace.AVCOL_SPC_YCGCO => VideoMatrix.YCgCo,
        AVColorSpace.AVCOL_SPC_BT2020_NCL => VideoMatrix.Bt2020Ncl,
        AVColorSpace.AVCOL_SPC_BT2020_CL => VideoMatrix.Bt2020Cl,
        AVColorSpace.AVCOL_SPC_SMPTE2085 => VideoMatrix.Smpte2085,
        AVColorSpace.AVCOL_SPC_CHROMA_DERIVED_NCL => VideoMatrix.ChromaDerivedNcl,
        AVColorSpace.AVCOL_SPC_CHROMA_DERIVED_CL => VideoMatrix.ChromaDerivedCl,
        AVColorSpace.AVCOL_SPC_ICTCP => VideoMatrix.ICtCp,
        _ => null,
    };

    private static VideoColorRange? Range(AVColorRange value) => value switch
    {
        AVColorRange.AVCOL_RANGE_MPEG => VideoColorRange.Limited,
        AVColorRange.AVCOL_RANGE_JPEG => VideoColorRange.Full,
        _ => null,
    };

    private static VideoChromaLocation? ChromaLocation(AVChromaLocation value) => value switch
    {
        AVChromaLocation.AVCHROMA_LOC_LEFT => VideoChromaLocation.Left,
        AVChromaLocation.AVCHROMA_LOC_CENTER => VideoChromaLocation.Center,
        AVChromaLocation.AVCHROMA_LOC_TOPLEFT => VideoChromaLocation.TopLeft,
        AVChromaLocation.AVCHROMA_LOC_TOP => VideoChromaLocation.Top,
        AVChromaLocation.AVCHROMA_LOC_BOTTOMLEFT => VideoChromaLocation.BottomLeft,
        AVChromaLocation.AVCHROMA_LOC_BOTTOM => VideoChromaLocation.Bottom,
        _ => null,
    };

    private static MasteringDisplayInfo? ReadMastering(AVFrameSideData* sideData) =>
        sideData is null || sideData->size < (ulong)sizeof(AVMasteringDisplayMetadata)
            ? null
            : ReadMastering((AVMasteringDisplayMetadata*)sideData->data);

    private static MasteringDisplayInfo? ReadMastering(AVCodecParameters* parameters)
    {
        var sideData = ffmpeg.av_packet_side_data_get(
            parameters->coded_side_data, parameters->nb_coded_side_data, AVPacketSideDataType.AV_PKT_DATA_MASTERING_DISPLAY_METADATA);
        return sideData is null || sideData->size < (ulong)sizeof(AVMasteringDisplayMetadata)
            ? null
            : ReadMastering((AVMasteringDisplayMetadata*)sideData->data);
    }

    private static MasteringDisplayInfo? ReadMastering(AVMasteringDisplayMetadata* metadata)
    {
        if (metadata->has_primaries == 0 && metadata->has_luminance == 0)
        {
            return null;
        }

        // display_primaries は [R, G, B][x, y]、white_point は [x, y]
        var primaries = (AVRational*)&metadata->display_primaries;
        var white = (AVRational*)&metadata->white_point;
        Chromaticity? Point(AVRational* xy) => metadata->has_primaries != 0 ? new Chromaticity(ToDouble(xy[0]), ToDouble(xy[1])) : null;
        return new MasteringDisplayInfo(
            Point(primaries),
            Point(primaries + 2),
            Point(primaries + 4),
            Point(white),
            metadata->has_luminance != 0 ? ToDouble(metadata->min_luminance) : null,
            metadata->has_luminance != 0 ? ToDouble(metadata->max_luminance) : null);
    }

    private static ContentLightLevelInfo? ReadContentLight(AVFrameSideData* sideData) =>
        sideData is null || sideData->size < (ulong)sizeof(AVContentLightMetadata)
            ? null
            : ReadContentLight((AVContentLightMetadata*)sideData->data);

    private static ContentLightLevelInfo? ReadContentLight(AVCodecParameters* parameters)
    {
        var sideData = ffmpeg.av_packet_side_data_get(
            parameters->coded_side_data, parameters->nb_coded_side_data, AVPacketSideDataType.AV_PKT_DATA_CONTENT_LIGHT_LEVEL);
        return sideData is null || sideData->size < (ulong)sizeof(AVContentLightMetadata)
            ? null
            : ReadContentLight((AVContentLightMetadata*)sideData->data);
    }

    private static ContentLightLevelInfo ReadContentLight(AVContentLightMetadata* metadata) =>
        new((int)metadata->MaxCLL, (int)metadata->MaxFALL);

    private static double ToDouble(AVRational value) => value.den == 0 ? 0 : (double)value.num / value.den;
}
