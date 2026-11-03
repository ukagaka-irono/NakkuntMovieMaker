using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using NakkuntMovieMaker.Core.Animation;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;

namespace NakkuntMovieMaker.Core.Serialization;

/// <summary>
/// モデルを JSON に書き出す。Git で差分を見やすくするため、同じモデルからは必ず同じバイト列を作る。
/// キーの順番は固定し、ID で区別するものは ID の順に並べる。改行は LF、インデントは 2 文字、文字はそのまま書く
/// </summary>
internal static class ProjectJsonWriter
{
    private static readonly JsonWriterOptions Options = new()
    {
        Indented = true,
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static byte[] WriteEntry(Project project) => Write(writer =>
    {
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", project.SchemaVersion);
        writer.WriteString("name", project.Name);
        writer.WriteString("rootScene", project.RootScene.ToString());
        writer.WriteStartArray("scenes");
        foreach (var id in project.Scenes.Keys.Select(id => id.ToString()).Order(StringComparer.Ordinal))
        {
            writer.WriteStringValue(id);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    });

    public static byte[] WriteScene(Scene scene) => Write(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("id", scene.Id.ToString());
        writer.WriteString("name", scene.Name);

        writer.WriteStartObject("format");
        writer.WriteNumber("width", scene.Format.Width);
        writer.WriteNumber("height", scene.Format.Height);
        writer.WriteString("fps", scene.Format.Fps.ToString());
        writer.WriteNumber("sampleRate", scene.Format.SampleRate);
        writer.WriteEndObject();

        // レイヤーは並び順が表示順なので、ID ではなく並び順のまま書く
        writer.WriteStartArray("layers");
        foreach (var layer in scene.Layers)
        {
            writer.WriteStartObject();
            writer.WriteString("id", layer.Id.ToString());
            writer.WriteString("name", layer.Name);
            writer.WriteBoolean("visible", layer.Visible);
            writer.WriteBoolean("locked", layer.Locked);
            writer.WriteBoolean("muted", layer.Muted);
            writer.WriteBoolean("solo", layer.Solo);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteStartArray("markers");
        foreach (var marker in scene.Markers.OrderBy(marker => marker.Frame).ThenBy(marker => marker.Id.ToString(), StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("id", marker.Id.ToString());
            writer.WriteNumber("frame", marker.Frame);
            writer.WriteString("name", marker.Name);
            writer.WriteString("color", marker.Color.ToString());
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteStartArray("items");
        foreach (var item in scene.Items.Values.OrderBy(item => item.Id.ToString(), StringComparer.Ordinal))
        {
            WriteItem(writer, item);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    });

    public static byte[] WriteCharacter(Character character) => Write(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("id", character.Id.ToString());
        writer.WriteString("name", character.Name);

        writer.WriteStartObject("voice");
        writer.WriteString("engineId", character.Voice.EngineId);
        writer.WriteString("voiceId", character.Voice.VoiceId);
        writer.WritePropertyName("params");
        WriteVoiceParams(writer, character.Voice.Params);
        writer.WriteEndObject();

        writer.WritePropertyName("subtitle");
        WriteTextStyle(writer, character.Subtitle);

        if (character.Tachie is { } tachie)
        {
            writer.WriteStartObject("tachie");
            writer.WriteString("kind", EnumNames.ToName(tachie.Kind));
            writer.WriteString("path", tachie.Path);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    });

    public static byte[] WriteAssets(IEnumerable<Asset> assets) => Write(writer =>
    {
        writer.WriteStartObject();
        writer.WriteStartArray("assets");
        foreach (var asset in assets.OrderBy(asset => asset.Id.ToString(), StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("id", asset.Id.ToString());
            writer.WriteString("kind", EnumNames.ToName(asset.Kind));
            writer.WriteString("path", asset.Path);
            if (asset.Hash is not null)
            {
                writer.WriteString("hash", asset.Hash);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    });

    private static byte[] Write(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, Options))
        {
            write(writer);
        }

        // ファイルの最後は改行で終える
        return [.. buffer.WrittenSpan, (byte)'\n'];
    }

    private static void WriteItem(Utf8JsonWriter writer, Item item)
    {
        writer.WriteStartObject();
        writer.WriteString("$type", item switch
        {
            VideoItem => ItemTypeIds.Video,
            ImageItem => ItemTypeIds.Image,
            AudioItem => ItemTypeIds.Audio,
            TextItem => ItemTypeIds.Text,
            ShapeItem => ItemTypeIds.Shape,
            VoiceItem => ItemTypeIds.Voice,
            TachieItem => ItemTypeIds.Tachie,
            UnknownItem unknown => unknown.TypeId,
            _ => throw new InvalidOperationException($"保存できないアイテムの種類です: {item.GetType().Name}"),
        });
        writer.WriteString("id", item.Id.ToString());
        writer.WriteString("layer", item.Layer.ToString());
        writer.WriteNumber("start", item.Start);
        writer.WriteNumber("length", item.Length);
        if (item.Group is { } group)
        {
            writer.WriteString("group", group.ToString());
        }

        writer.WriteStartArray("effects");
        foreach (var effect in item.Effects)
        {
            WriteEffect(writer, effect);
        }

        writer.WriteEndArray();

        if (item is VisualItem visual)
        {
            WriteVisual(writer, visual);
        }

        switch (item)
        {
            case VideoItem video:
                writer.WriteString("asset", video.Asset.ToString());
                WritePlayback(writer, video.Playback);
                WriteAudioMix(writer, video.Audio);
                break;
            case ImageItem image:
                writer.WriteString("asset", image.Asset.ToString());
                break;
            case AudioItem audio:
                writer.WriteString("asset", audio.Asset.ToString());
                WritePlayback(writer, audio.Playback);
                WriteAudioMix(writer, audio.Audio);
                break;
            case TextItem text:
                writer.WriteString("text", text.Text);
                writer.WritePropertyName("style");
                WriteTextStyle(writer, text.Style);
                break;
            case ShapeItem shape:
                writer.WritePropertyName("shape");
                WriteShapeStyle(writer, shape.Shape);
                break;
            case VoiceItem voice:
                WriteVoice(writer, voice);
                break;
            case TachieItem tachie:
                writer.WriteString("character", tachie.Character.ToString());
                break;
            case UnknownItem unknown:
                // 共通の項目以外は、読み込んだときの JSON をそのまま書き戻す
                foreach (var property in unknown.Data.EnumerateObject())
                {
                    if (!ProjectJsonReader.CommonItemProperties.Contains(property.Name))
                    {
                        property.WriteTo(writer);
                    }
                }

                break;
        }

        writer.WriteEndObject();
    }

    private static void WriteVisual(Utf8JsonWriter writer, VisualItem visual)
    {
        var transform = visual.Transform;
        writer.WriteStartObject("transform");
        WriteAnimatable(writer, "x", transform.X);
        WriteAnimatable(writer, "y", transform.Y);
        WriteAnimatable(writer, "scaleX", transform.ScaleX);
        WriteAnimatable(writer, "scaleY", transform.ScaleY);
        WriteAnimatable(writer, "rotation", transform.Rotation);
        WriteAnimatable(writer, "anchorX", transform.AnchorX);
        WriteAnimatable(writer, "anchorY", transform.AnchorY);
        writer.WriteBoolean("flipHorizontal", transform.FlipHorizontal);
        writer.WriteBoolean("flipVertical", transform.FlipVertical);
        writer.WriteEndObject();

        WriteAnimatable(writer, "opacity", visual.Opacity);
        writer.WriteString("blend", EnumNames.ToName(visual.Blend));

        writer.WriteStartObject("crop");
        WriteNumber(writer, "left", visual.Crop.Left);
        WriteNumber(writer, "top", visual.Crop.Top);
        WriteNumber(writer, "right", visual.Crop.Right);
        WriteNumber(writer, "bottom", visual.Crop.Bottom);
        writer.WriteEndObject();
    }

    private static void WriteVoice(Utf8JsonWriter writer, VoiceItem voice)
    {
        writer.WriteString("character", voice.Character.ToString());
        writer.WriteString("text", voice.Text);
        if (voice.Reading is not null)
        {
            writer.WriteString("reading", voice.Reading);
        }

        writer.WriteStartObject("source");
        switch (voice.Source)
        {
            case SynthesizedVoice synthesized:
                writer.WriteString("$type", "synthesized");
                if (synthesized.CacheKey is not null)
                {
                    writer.WriteString("cacheKey", synthesized.CacheKey);
                }

                break;
            case RecordedVoice recorded:
                writer.WriteString("$type", "recorded");
                writer.WriteString("asset", recorded.Asset.ToString());
                break;
            default:
                throw new InvalidOperationException($"保存できない声の種類です: {voice.Source.GetType().Name}");
        }

        writer.WriteEndObject();

        if (voice.VoiceOverride is { } voiceOverride)
        {
            writer.WritePropertyName("voiceOverride");
            WriteVoiceParams(writer, voiceOverride);
        }

        if (voice.SubtitleOverride is { } subtitleOverride)
        {
            writer.WritePropertyName("subtitleOverride");
            WriteTextStyle(writer, subtitleOverride);
        }

        WriteAudioMix(writer, voice.Audio);
    }

    private static void WritePlayback(Utf8JsonWriter writer, MediaPlayback playback)
    {
        writer.WriteStartObject("playback");
        writer.WriteString("sourceOffset", playback.SourceOffset.ToString());
        writer.WriteString("speed", playback.Speed.ToString());
        writer.WriteBoolean("reverse", playback.Reverse);
        writer.WriteEndObject();
    }

    private static void WriteAudioMix(Utf8JsonWriter writer, AudioMix audio)
    {
        writer.WriteStartObject("audio");
        WriteAnimatable(writer, "volume", audio.Volume);
        WriteAnimatable(writer, "pan", audio.Pan);
        writer.WriteNumber("fadeIn", audio.FadeInFrames);
        writer.WriteNumber("fadeOut", audio.FadeOutFrames);
        writer.WriteBoolean("preservePitch", audio.PreservePitch);
        writer.WriteEndObject();
    }

    private static void WriteVoiceParams(Utf8JsonWriter writer, VoiceParams parameters)
    {
        writer.WriteStartObject();
        WriteNumber(writer, "speed", parameters.Speed);
        WriteNumber(writer, "pitch", parameters.Pitch);
        WriteNumber(writer, "intonation", parameters.Intonation);
        WriteNumber(writer, "volume", parameters.Volume);
        writer.WriteEndObject();
    }

    private static void WriteTextStyle(Utf8JsonWriter writer, TextStyle style)
    {
        writer.WriteStartObject();
        writer.WriteString("fontFamily", style.FontFamily);
        WriteNumber(writer, "fontSize", style.FontSize);
        writer.WriteString("color", style.Color.ToString());
        writer.WriteBoolean("bold", style.Bold);
        writer.WriteBoolean("italic", style.Italic);
        if (style.Outline is { } outline)
        {
            writer.WriteStartObject("outline");
            writer.WriteString("color", outline.Color.ToString());
            WriteNumber(writer, "width", outline.Width);
            writer.WriteEndObject();
        }

        if (style.Shadow is { } shadow)
        {
            writer.WriteStartObject("shadow");
            writer.WriteString("color", shadow.Color.ToString());
            WriteNumber(writer, "offsetX", shadow.OffsetX);
            WriteNumber(writer, "offsetY", shadow.OffsetY);
            WriteNumber(writer, "blur", shadow.Blur);
            writer.WriteEndObject();
        }

        if (style.Background is { } background)
        {
            writer.WriteStartObject("background");
            writer.WriteString("color", background.Color.ToString());
            WriteNumber(writer, "padding", background.Padding);
            WriteNumber(writer, "cornerRadius", background.CornerRadius);
            writer.WriteEndObject();
        }

        writer.WriteString("alignment", EnumNames.ToName(style.Alignment));
        WriteNumber(writer, "lineSpacing", style.LineSpacing);
        WriteNumber(writer, "letterSpacing", style.LetterSpacing);
        writer.WriteBoolean("vertical", style.Vertical);
        WriteNumber(writer, "revealCharsPerSecond", style.RevealCharsPerSecond);
        writer.WriteEndObject();
    }

    private static void WriteShapeStyle(Utf8JsonWriter writer, ShapeStyle shape)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", EnumNames.ToName(shape.Kind));
        WriteNumber(writer, "width", shape.Width);
        WriteNumber(writer, "height", shape.Height);

        writer.WriteStartObject("fill");
        switch (shape.Fill)
        {
            case SolidPaint solid:
                writer.WriteString("$type", "solid");
                writer.WriteString("color", solid.Color.ToString());
                break;
            case LinearGradientPaint gradient:
                writer.WriteString("$type", "linearGradient");
                writer.WriteString("start", gradient.Start.ToString());
                writer.WriteString("end", gradient.End.ToString());
                WriteNumber(writer, "angle", gradient.Angle);
                break;
            default:
                throw new InvalidOperationException($"保存できない塗りの種類です: {shape.Fill.GetType().Name}");
        }

        writer.WriteEndObject();

        if (shape.StrokeColor is { } strokeColor)
        {
            writer.WriteString("strokeColor", strokeColor.ToString());
        }

        WriteNumber(writer, "strokeWidth", shape.StrokeWidth);
        WriteNumber(writer, "cornerRadius", shape.CornerRadius);
        writer.WriteNumber("sides", shape.Sides);
        WriteNumber(writer, "innerRadiusRatio", shape.InnerRadiusRatio);
        writer.WriteEndObject();
    }

    private static void WriteEffect(Utf8JsonWriter writer, EffectInstance effect)
    {
        writer.WriteStartObject();
        writer.WriteString("id", effect.Id.ToString());
        writer.WriteString("type", effect.Type);
        writer.WriteBoolean("enabled", effect.Enabled);
        writer.WriteStartObject("parameters");
        foreach (var (name, value) in effect.Parameters.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            writer.WritePropertyName(name);
            switch (value)
            {
                case NumberParameter number:
                    writer.WriteStartObject();
                    writer.WriteString("$type", "number");
                    WriteAnimatable(writer, "value", number.Value);
                    writer.WriteEndObject();
                    break;
                case ColorParameter color:
                    writer.WriteStartObject();
                    writer.WriteString("$type", "color");
                    WriteAnimatable(writer, "value", color.Value);
                    writer.WriteEndObject();
                    break;
                case BoolParameter boolean:
                    writer.WriteStartObject();
                    writer.WriteString("$type", "bool");
                    writer.WriteBoolean("value", boolean.Value);
                    writer.WriteEndObject();
                    break;
                case TextParameter text:
                    writer.WriteStartObject();
                    writer.WriteString("$type", "text");
                    writer.WriteString("value", text.Value);
                    writer.WriteEndObject();
                    break;
                case UnknownParameter unknown:
                    unknown.Data.WriteTo(writer);
                    break;
                default:
                    throw new InvalidOperationException($"保存できないパラメーターの種類です: {value.GetType().Name}");
            }
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    // アニメーションしない値は値だけ、アニメーションする値は { "keys": [...] } で書く
    private static void WriteAnimatable(Utf8JsonWriter writer, string name, Animatable<double> value)
    {
        if (!value.IsAnimated)
        {
            WriteNumber(writer, name, value.Value);
            return;
        }

        writer.WriteStartObject(name);
        writer.WriteStartArray("keys");
        foreach (var key in value.Keys)
        {
            writer.WriteStartObject();
            writer.WriteNumber("frame", key.Frame);
            WriteNumber(writer, "value", key.Value);
            writer.WriteString("easing", key.Easing.ToString());
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteAnimatable(Utf8JsonWriter writer, string name, Animatable<RgbaColor> value)
    {
        if (!value.IsAnimated)
        {
            writer.WriteString(name, value.Value.ToString());
            return;
        }

        writer.WriteStartObject(name);
        writer.WriteStartArray("keys");
        foreach (var key in value.Keys)
        {
            writer.WriteStartObject();
            writer.WriteNumber("frame", key.Frame);
            writer.WriteString("value", key.Value.ToString());
            writer.WriteString("easing", key.Easing.ToString());
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    // 数値は、読み戻すと同じ値になる最短の表記で書く（Utf8JsonWriter の既定の書き方）
    private static void WriteNumber(Utf8JsonWriter writer, string name, double value)
    {
        if (!double.IsFinite(value))
        {
            throw new InvalidOperationException($"保存できない数値です（{name}: {value}）");
        }

        writer.WriteNumber(name, value);
    }
}
