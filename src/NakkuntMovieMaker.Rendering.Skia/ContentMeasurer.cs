using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Text;

namespace NakkuntMovieMaker.Rendering.Skia;

/// <summary>
/// 描く内容の大きさを測る（描画の方式によらない）。プレビュー上でアイテムを選ぶ・動かすときに使う
/// </summary>
public sealed class ContentMeasurer(TextLayoutCache? layouts = null)
{
    private readonly TextLayoutCache _layouts = layouts ?? new TextLayoutCache();

    /// <summary>
    /// 素材を切り抜いた残りの大きさ（ローカル座標の単位。中心がローカル座標の原点に来る）。素材を読めなければ null
    /// </summary>
    public (float Width, float Height)? Measure(DrawNode node, IMediaSource media, int sceneWidth, int sceneHeight)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(media);

        // 大きさだけが要るので、素材は小さく読む
        const double Hint = 1.0 / 8;
        (float Width, float Height)? size = node.Content switch
        {
            VideoFrameContent video => media.GetVideoFrame(video.Asset, video.SourceTime, Hint) is { } frame ? (frame.Width, frame.Height) : null,
            ImageContent image => media.GetImage(image.Asset, Hint) is { } picture ? (picture.Width, picture.Height) : null,
            TextContent text => _layouts.Get(text.Text, text.Style) is var layout ? (layout.Width, layout.Height) : null,
            ShapeContent { Shape.Kind: ShapeKind.Background } => (sceneWidth, sceneHeight),
            ShapeContent shape => ((float)shape.Shape.Width, (float)shape.Shape.Height),
            TachieContent tachie => tachie.Layers.Select(path => media.GetImageFile(path, Hint)).OfType<SourceImage>().ToList() is { Count: > 0 } layers
                ? (layers.Max(layer => layer.Width), layers.Max(layer => layer.Height))
                : null,
            _ => null,
        };
        if (size is not var (width, height))
        {
            return null;
        }

        var crop = node.Crop;
        return (Math.Max(0, width - (float)(crop.Left + crop.Right)), Math.Max(0, height - (float)(crop.Top + crop.Bottom)));
    }
}
