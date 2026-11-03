using System.Collections.Immutable;
using System.Text.Json;
using NakkuntMovieMaker.Core.Animation;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;

namespace NakkuntMovieMaker.Core.Serialization;

/// <summary>プロジェクトの入口のファイル（.nmmproj）の中身</summary>
internal sealed record ProjectEntry(int SchemaVersion, string Name, SceneId RootScene, ImmutableArray<SceneId> Scenes);

/// <summary>
/// JSON からモデルを読む。足りない項目や型の違いは、ファイル名と項目名を付けた ProjectFormatException にする。
/// 知らない種類のアイテム・パラメーターは、データを失わないよう UnknownItem・UnknownParameter として読む
/// </summary>
internal sealed class ProjectJsonReader(string file)
{
    public ProjectEntry ReadEntry(JsonElement root) => new(
        GetInt(root, "schemaVersion"),
        GetString(root, "name"),
        new SceneId(GetGuid(root, "rootScene")),
        [.. GetArray(root, "scenes").Select(element => new SceneId(ParseGuid(element, "scenes")))]);

    /// <summary>schemaVersion だけを読む（移行処理の前に使う）</summary>
    public int ReadSchemaVersion(JsonElement root) => GetInt(root, "schemaVersion");

    public Scene ReadScene(JsonElement root)
    {
        var format = Get(root, "format");
        var layers = GetArray(root, "layers").Select(element => new Layer(
            new LayerId(GetGuid(element, "id")),
            GetString(element, "name"),
            GetBool(element, "visible"),
            GetBool(element, "locked"),
            GetBool(element, "muted"),
            GetBool(element, "solo"))).ToImmutableList();
        var markers = GetArray(root, "markers").Select(element => new Marker(
            new MarkerId(GetGuid(element, "id")),
            GetLong(element, "frame"),
            GetString(element, "name"),
            GetColor(element, "color"))).ToImmutableList();

        var items = ImmutableDictionary.CreateBuilder<ItemId, Item>();
        foreach (var element in GetArray(root, "items"))
        {
            var item = ReadItem(element);
            if (!items.TryAdd(item.Id, item))
            {
                throw Error($"アイテムの ID が重複しています: {item.Id}");
            }
        }

        return new Scene(
            new SceneId(GetGuid(root, "id")),
            GetString(root, "name"),
            new SceneFormat(GetInt(format, "width"), GetInt(format, "height"), GetRational(format, "fps"), GetInt(format, "sampleRate")),
            layers,
            items.ToImmutable(),
            markers);
    }

    public Character ReadCharacter(JsonElement root)
    {
        var voice = Get(root, "voice");
        TachieSpec? tachie = null;
        if (TryGet(root, "tachie", out var tachieElement))
        {
            tachie = new TachieSpec(GetEnum<TachieKind>(tachieElement, "kind"), GetString(tachieElement, "path"));
        }

        return new Character(
            new CharacterId(GetGuid(root, "id")),
            GetString(root, "name"),
            new VoiceSpec(GetString(voice, "engineId"), GetString(voice, "voiceId"), ReadVoiceParams(Get(voice, "params"))),
            ReadTextStyle(Get(root, "subtitle")),
            tachie);
    }

    public ImmutableDictionary<AssetId, Asset> ReadAssets(JsonElement root)
    {
        var assets = ImmutableDictionary.CreateBuilder<AssetId, Asset>();
        foreach (var element in GetArray(root, "assets"))
        {
            var asset = new Asset(
                new AssetId(GetGuid(element, "id")),
                GetEnum<AssetKind>(element, "kind"),
                GetString(element, "path"),
                TryGet(element, "hash", out var hash) ? ReadString(hash, "hash") : null);
            if (!assets.TryAdd(asset.Id, asset))
            {
                throw Error($"素材の ID が重複しています: {asset.Id}");
            }
        }

        return assets.ToImmutable();
    }

    private Item ReadItem(JsonElement element)
    {
        var type = GetString(element, "$type");
        var id = new ItemId(GetGuid(element, "id"));
        var layer = new LayerId(GetGuid(element, "layer"));
        var start = GetLong(element, "start");
        var length = GetLong(element, "length");

        Item item = type switch
        {
            ItemTypeIds.Video => new VideoItem(id, layer, start, length, new AssetId(GetGuid(element, "asset")))
            {
                Playback = ReadPlayback(Get(element, "playback")),
                Audio = ReadAudioMix(Get(element, "audio")),
            },
            ItemTypeIds.Image => new ImageItem(id, layer, start, length, new AssetId(GetGuid(element, "asset"))),
            ItemTypeIds.Audio => new AudioItem(id, layer, start, length, new AssetId(GetGuid(element, "asset")))
            {
                Playback = ReadPlayback(Get(element, "playback")),
                Audio = ReadAudioMix(Get(element, "audio")),
            },
            ItemTypeIds.Text => new TextItem(id, layer, start, length, GetString(element, "text"))
            {
                Style = ReadTextStyle(Get(element, "style")),
            },
            ItemTypeIds.Shape => new ShapeItem(id, layer, start, length, ReadShapeStyle(Get(element, "shape"))),
            ItemTypeIds.Voice => ReadVoice(element, id, layer, start, length),
            ItemTypeIds.Tachie => new TachieItem(id, layer, start, length, new CharacterId(GetGuid(element, "character"))),
            _ => new UnknownItem(id, layer, start, length, type, WithoutCommonItemProperties(element)),
        };

        item = item with
        {
            Group = TryGet(element, "group", out var group) ? new GroupId(ParseGuid(group, "group")) : null,
            Effects = [.. GetArray(element, "effects").Select(ReadEffect)],
        };

        if (item is VisualItem visual && item is not UnknownItem)
        {
            item = ReadVisual(element, visual);
        }

        return item;
    }

    /// <summary>アイテムに共通の項目（record で持つもの）</summary>
    internal static readonly ImmutableHashSet<string> CommonItemProperties =
        ["$type", "id", "layer", "start", "length", "group", "effects"];

    // 知らない種類のアイテムは、共通の項目を除いた残りだけを Data に持つ（共通の項目は編集で変わるため）
    private static JsonElement WithoutCommonItemProperties(JsonElement element)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().Where(property => !CommonItemProperties.Contains(property.Name)))
            {
                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private VisualItem ReadVisual(JsonElement element, VisualItem visual)
    {
        var transform = Get(element, "transform");
        var crop = Get(element, "crop");
        return visual with
        {
            Transform = new Transform2D(
                GetAnimatableNumber(transform, "x"),
                GetAnimatableNumber(transform, "y"),
                GetAnimatableNumber(transform, "scaleX"),
                GetAnimatableNumber(transform, "scaleY"),
                GetAnimatableNumber(transform, "rotation"),
                GetAnimatableNumber(transform, "anchorX"),
                GetAnimatableNumber(transform, "anchorY"),
                GetBool(transform, "flipHorizontal"),
                GetBool(transform, "flipVertical")),
            Opacity = GetAnimatableNumber(element, "opacity"),
            Blend = GetEnum<BlendMode>(element, "blend"),
            Crop = new Crop(GetDouble(crop, "left"), GetDouble(crop, "top"), GetDouble(crop, "right"), GetDouble(crop, "bottom")),
        };
    }

    private VoiceItem ReadVoice(JsonElement element, ItemId id, LayerId layer, long start, long length)
    {
        var source = Get(element, "source");
        VoiceSource voiceSource = GetString(source, "$type") switch
        {
            "synthesized" => new SynthesizedVoice(TryGet(source, "cacheKey", out var key) ? ReadString(key, "cacheKey") : null),
            "recorded" => new RecordedVoice(new AssetId(GetGuid(source, "asset"))),
            var other => throw Error($"知らない声の種類です: {other}"),
        };

        return new VoiceItem(id, layer, start, length, new CharacterId(GetGuid(element, "character")), GetString(element, "text"))
        {
            Reading = TryGet(element, "reading", out var reading) ? ReadString(reading, "reading") : null,
            Source = voiceSource,
            VoiceOverride = TryGet(element, "voiceOverride", out var voiceOverride) ? ReadVoiceParams(voiceOverride) : null,
            SubtitleOverride = TryGet(element, "subtitleOverride", out var subtitle) ? ReadTextStyle(subtitle) : null,
            Audio = ReadAudioMix(Get(element, "audio")),
        };
    }

    private MediaPlayback ReadPlayback(JsonElement element) =>
        new(GetRational(element, "sourceOffset"), GetRational(element, "speed"), GetBool(element, "reverse"));

    private AudioMix ReadAudioMix(JsonElement element) => new(
        GetAnimatableNumber(element, "volume"),
        GetAnimatableNumber(element, "pan"),
        GetLong(element, "fadeIn"),
        GetLong(element, "fadeOut"),
        GetBool(element, "preservePitch"));

    private VoiceParams ReadVoiceParams(JsonElement element) => new(
        GetDouble(element, "speed"),
        GetDouble(element, "pitch"),
        GetDouble(element, "intonation"),
        GetDouble(element, "volume"));

    private TextStyle ReadTextStyle(JsonElement element)
    {
        TextOutline? outline = null;
        if (TryGet(element, "outline", out var outlineElement))
        {
            outline = new TextOutline(GetColor(outlineElement, "color"), GetDouble(outlineElement, "width"));
        }

        TextShadow? shadow = null;
        if (TryGet(element, "shadow", out var shadowElement))
        {
            shadow = new TextShadow(
                GetColor(shadowElement, "color"),
                GetDouble(shadowElement, "offsetX"),
                GetDouble(shadowElement, "offsetY"),
                GetDouble(shadowElement, "blur"));
        }

        TextBackground? background = null;
        if (TryGet(element, "background", out var backgroundElement))
        {
            background = new TextBackground(
                GetColor(backgroundElement, "color"),
                GetDouble(backgroundElement, "padding"),
                GetDouble(backgroundElement, "cornerRadius"));
        }

        return new TextStyle(
            GetString(element, "fontFamily"),
            GetDouble(element, "fontSize"),
            GetColor(element, "color"),
            GetBool(element, "bold"),
            GetBool(element, "italic"),
            outline,
            shadow,
            background,
            GetEnum<TextAlignment>(element, "alignment"),
            GetDouble(element, "lineSpacing"),
            GetDouble(element, "letterSpacing"),
            GetBool(element, "vertical"),
            GetDouble(element, "revealCharsPerSecond"));
    }

    private ShapeStyle ReadShapeStyle(JsonElement element)
    {
        var fill = Get(element, "fill");
        Paint paint = GetString(fill, "$type") switch
        {
            "solid" => new SolidPaint(GetColor(fill, "color")),
            "linearGradient" => new LinearGradientPaint(GetColor(fill, "start"), GetColor(fill, "end"), GetDouble(fill, "angle")),
            var other => throw Error($"知らない塗りの種類です: {other}"),
        };

        return new ShapeStyle(
            GetEnum<ShapeKind>(element, "kind"),
            GetDouble(element, "width"),
            GetDouble(element, "height"),
            paint,
            TryGet(element, "strokeColor", out var stroke) ? ParseColor(stroke, "strokeColor") : null,
            GetDouble(element, "strokeWidth"),
            GetDouble(element, "cornerRadius"),
            GetInt(element, "sides"),
            GetDouble(element, "innerRadiusRatio"));
    }

    private EffectInstance ReadEffect(JsonElement element)
    {
        var parameters = ImmutableDictionary.CreateBuilder<string, ParameterValue>();
        foreach (var property in Get(element, "parameters").EnumerateObject())
        {
            var value = property.Value;
            parameters[property.Name] = (TryGet(value, "$type", out var type) ? type.GetString() : null) switch
            {
                "number" => new NumberParameter(GetAnimatableNumber(value, "value")),
                "color" => new ColorParameter(GetAnimatableColor(value, "value")),
                "bool" => new BoolParameter(GetBool(value, "value")),
                "text" => new TextParameter(GetString(value, "value")),
                _ => new UnknownParameter(value.Clone()),
            };
        }

        return new EffectInstance(
            new EffectId(GetGuid(element, "id")),
            GetString(element, "type"),
            parameters.ToImmutable(),
            GetBool(element, "enabled"));
    }

    private Animatable<double> GetAnimatableNumber(JsonElement element, string name)
    {
        var value = Get(element, name);
        if (value.ValueKind == JsonValueKind.Number)
        {
            return Animatable.Constant(ReadDouble(value, name));
        }

        return ReadKeys(value, name, key => GetDouble(key, "value"));
    }

    private Animatable<RgbaColor> GetAnimatableColor(JsonElement element, string name)
    {
        var value = Get(element, name);
        if (value.ValueKind == JsonValueKind.String)
        {
            return Animatable.Constant(ParseColor(value, name));
        }

        return ReadKeys(value, name, key => GetColor(key, "value"));
    }

    private Animatable<T> ReadKeys<T>(JsonElement value, string name, Func<JsonElement, T> readValue)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Error($"{name} は数値かキーフレームのオブジェクトにしてください");
        }

        var keys = GetArray(value, "keys").Select(key =>
        {
            var easingText = GetString(key, "easing");
            if (!Easing.TryParse(easingText, out var easing))
            {
                throw Error($"知らないイージングです: {easingText}");
            }

            return new Keyframe<T>(GetLong(key, "frame"), readValue(key), easing);
        }).ToList();

        try
        {
            return Animatable.FromKeys(keys);
        }
        catch (ArgumentException ex)
        {
            throw Error($"{name} のキーフレームが正しくありません: {ex.Message}");
        }
    }

    private JsonElement Get(JsonElement element, string name) =>
        TryGet(element, name, out var value) ? value : throw Error($"項目 {name} がありません");

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null)
        {
            return true;
        }

        value = default;
        return false;
    }

    private IEnumerable<JsonElement> GetArray(JsonElement element, string name)
    {
        var value = Get(element, name);
        return value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : throw Error($"{name} は配列にしてください");
    }

    private string GetString(JsonElement element, string name) => ReadString(Get(element, name), name);

    private string ReadString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.String ? value.GetString()! : throw Error($"{name} は文字列にしてください");

    private bool GetBool(JsonElement element, string name)
    {
        var value = Get(element, name);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Error($"{name} は true か false にしてください"),
        };
    }

    private int GetInt(JsonElement element, string name)
    {
        var value = Get(element, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : throw Error($"{name} は整数にしてください");
    }

    private long GetLong(JsonElement element, string name)
    {
        var value = Get(element, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : throw Error($"{name} は整数にしてください");
    }

    private double GetDouble(JsonElement element, string name) => ReadDouble(Get(element, name), name);

    private double ReadDouble(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)
            ? number
            : throw Error($"{name} は数値にしてください");

    private Guid GetGuid(JsonElement element, string name) => ParseGuid(Get(element, name), name);

    private Guid ParseGuid(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.String && Guid.TryParseExact(value.GetString(), "D", out var guid)
            ? guid
            : throw Error($"{name} は ID（xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx の形）にしてください");

    private Rational GetRational(JsonElement element, string name)
    {
        var text = GetString(element, name);
        return Rational.TryParse(text, out var value) ? value : throw Error($"{name} は \"30000/1001\" のような有理数にしてください");
    }

    private RgbaColor GetColor(JsonElement element, string name) => ParseColor(Get(element, name), name);

    private RgbaColor ParseColor(JsonElement value, string name)
    {
        var text = ReadString(value, name);
        return RgbaColor.TryParse(text, out var color) ? color : throw Error($"{name} は \"#RRGGBB\" か \"#RRGGBBAA\" にしてください");
    }

    private T GetEnum<T>(JsonElement element, string name)
        where T : struct, Enum
    {
        var text = GetString(element, name);
        return EnumNames.TryParse<T>(text, out var value) ? value : throw Error($"{name} の値 {text} は使えません");
    }

    private ProjectFormatException Error(string message) => new(file, message);
}
