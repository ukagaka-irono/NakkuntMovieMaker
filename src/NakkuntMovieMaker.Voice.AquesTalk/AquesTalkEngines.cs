using System.Text;

namespace NakkuntMovieMaker.Voice.AquesTalk;

/// <summary>
/// AquesTalk のエンジンに共通の部分。ライブラリは利用者が置いた場合だけ読み込み、なければ「使用不可」にして導入の案内を出す。
/// 読みは AqKanji2Koe があればそれで作り、なければ本文をそのまま読みとして渡す（ひらがな・カタカナの文なら読める）
/// </summary>
public abstract class AquesTalkEngineBase : IVoiceEngine
{
    private readonly IPhoneticConverter? _converter;

    protected AquesTalkEngineBase(AquesTalkOptions options, IPhoneticConverter? converter)
    {
        Options = options;
        _converter = converter;
    }

    protected AquesTalkOptions Options { get; }

    public abstract string Id { get; }

    public abstract string Name { get; }

    public abstract string Version { get; }

    public abstract VoiceEngineStatus Status { get; }

    public abstract IReadOnlyList<VoiceInfo> Voices { get; }

    public string? MakeReading(string text) => _converter?.Convert(text) ?? text;

    public abstract VoiceAudio Synthesize(VoiceRequest request, CancellationToken cancellationToken);

    public abstract void Dispose();

    protected VoiceEngineStatus NotInstalled(string product, string place, string url) => new(
        false,
        $"{product} は導入されていません。株式会社アクエストから入手したライブラリを {place} に置いてください（営利目的で使う場合は使用ライセンスが必要です）",
        url);
}

/// <summary>
/// AquesTalk1。声ごとにライブラリが分かれている（f1 が「ゆっくり霊夢」の声）。
/// aquestalk1/&lt;rid&gt;/ の下のフォルダの名前が声の名前になる。調整できるのは速さだけで、音量は合成した後に掛ける
/// </summary>
public sealed unsafe class AquesTalk1Engine : AquesTalkEngineBase
{
    public const string EngineId = "aquestalk1";

    private readonly Dictionary<string, UserLibrary?> _libraries = new(StringComparer.Ordinal);
    private bool _keysSet;

    public AquesTalk1Engine(AquesTalkOptions options, IPhoneticConverter? converter = null)
        : base(options, converter)
    {
    }

    public override string Id => EngineId;

    public override string Name => "AquesTalk1";

    public override string Version => "1";

    /// <summary>ライブラリを置いたフォルダの名前が、声の名前になる</summary>
    public override IReadOnlyList<VoiceInfo> Voices =>
        Directory.Exists(Options.ProductFolder("aquestalk1"))
            ? [.. Directory.EnumerateDirectories(Options.ProductFolder("aquestalk1"))
                .Where(folder => UserLibrary.Exists(folder, "AquesTalk"))
                .Select(Path.GetFileName)
                .OfType<string>()
                .Order(StringComparer.Ordinal)
                .Select(name => new VoiceInfo(name, name))]
            : [];

    public override VoiceEngineStatus Status =>
        Voices.Count > 0
            ? VoiceEngineStatus.Ready
            : NotInstalled("AquesTalk1", $"{Path.Combine("engines", "aquestalk", "aquestalk1", AquesTalkOptions.Rid)}/<声の名前（f1 など）>/", "https://www.a-quest.com/products/aquestalk_1.html");

    public override VoiceAudio Synthesize(VoiceRequest request, CancellationToken cancellationToken)
    {
        var library = Library(request.VoiceId) ?? throw new VoiceEngineException($"AquesTalk1 の声「{request.VoiceId}」のライブラリが見つからないか、読み込めません（この環境用のライブラリか確かめてください）");
        if (!_keysSet)
        {
            // 使用ライセンスのキーを持っていれば、最初に一度だけ渡す（ない版のライブラリもある）
            if (Options.UsrKey is { } usrKey && library.Export("AquesTalk_SetUsrKey") is var setUsrKey and not 0)
            {
                fixed (byte* key = Interop.Utf8(usrKey))
                {
                    ((delegate* unmanaged[Cdecl]<byte*, int>)setUsrKey)(key);
                }
            }

            _keysSet = true;
        }

        var synthe = (delegate* unmanaged[Cdecl]<byte*, int, int*, byte*>)library.RequireExport("AquesTalk_Synthe_Utf8");
        var free = (delegate* unmanaged[Cdecl]<byte*, void>)library.RequireExport("AquesTalk_FreeWave");
        fixed (byte* koe = Interop.Utf8(request.Reading ?? request.Text))
        {
            var size = 0;
            var wave = synthe(koe, Interop.Clamp(request.Params.Speed * 100, 50, 300), &size);
            return Interop.TakeWave(wave, size, free, request.Params.Volume, Name);
        }
    }

    public override void Dispose()
    {
        foreach (var library in _libraries.Values)
        {
            library?.Dispose();
        }

        _libraries.Clear();
    }

    private UserLibrary? Library(string voice)
    {
        if (!_libraries.TryGetValue(voice, out var library))
        {
            library = UserLibrary.TryLoad(Path.Combine(Options.ProductFolder("aquestalk1"), voice), "AquesTalk");
            _libraries[voice] = library;
        }

        return library;
    }
}

/// <summary>
/// AquesTalk10。声の種類（F1E・F2E・M1E）と、速さ・音量・高さ・抑揚を調整できる
/// </summary>
public sealed unsafe class AquesTalk10Engine : AquesTalkEngineBase
{
    public const string EngineId = "aquestalk10";

    private readonly Lazy<UserLibrary?> _library;
    private bool _keysSet;

    public AquesTalk10Engine(AquesTalkOptions options, IPhoneticConverter? converter = null)
        : base(options, converter)
    {
        _library = new Lazy<UserLibrary?>(() => UserLibrary.TryLoad(options.ProductFolder("aquestalk10"), "AquesTalk10", "AquesTalk"));
    }

    public override string Id => EngineId;

    public override string Name => "AquesTalk10";

    public override string Version => "10";

    public override IReadOnlyList<VoiceInfo> Voices { get; } =
        [new("F1E", "女声 1（F1E）"), new("F2E", "女声 2（F2E）"), new("M1E", "男声 1（M1E）")];

    public override VoiceEngineStatus Status =>
        _library.Value is not null
            ? VoiceEngineStatus.Ready
            : NotInstalled("AquesTalk10", $"{Path.Combine("engines", "aquestalk", "aquestalk10", AquesTalkOptions.Rid)}/", "https://www.a-quest.com/products/aquestalk10.html");

    public override VoiceAudio Synthesize(VoiceRequest request, CancellationToken cancellationToken)
    {
        var library = _library.Value ?? throw new VoiceEngineException(Status.Message!);
        SetKeys(library);

        var basis = Voices.Select((voice, index) => (voice.Id, index)).FirstOrDefault(entry => entry.Id == request.VoiceId);
        var parameters = request.Params;
        var voice = new AquesTalkVoice(
            basis.Id is null ? 0 : basis.index,
            Interop.Clamp(parameters.Speed * 100, 50, 300),
            Interop.Clamp(parameters.Volume * 100, 0, 300),
            Interop.Clamp(parameters.Pitch * 100, 20, 200),
            Interop.Clamp(parameters.Intonation * 100, 0, 200),
            100,
            100);

        var synthe = (delegate* unmanaged[Cdecl]<AquesTalkVoice*, byte*, int*, byte*>)library.RequireExport("AquesTalk_Synthe_Utf8");
        var free = (delegate* unmanaged[Cdecl]<byte*, void>)library.RequireExport("AquesTalk_FreeWave");
        fixed (byte* koe = Interop.Utf8(request.Reading ?? request.Text))
        {
            var size = 0;
            var wave = synthe(&voice, koe, &size);

            // 音量は AquesTalk10 が掛けるので、ここでは 1 のまま
            return Interop.TakeWave(wave, size, free, 1, Name);
        }
    }

    public override void Dispose()
    {
        if (_library.IsValueCreated)
        {
            _library.Value?.Dispose();
        }
    }

    private void SetKeys(UserLibrary library)
    {
        if (_keysSet)
        {
            return;
        }

        foreach (var (key, function) in new[] { (Options.DevKey, "AquesTalk_SetDevKey"), (Options.UsrKey, "AquesTalk_SetUsrKey") })
        {
            if (key is not null && library.Export(function) is var address and not 0)
            {
                fixed (byte* text = Interop.Utf8(key))
                {
                    ((delegate* unmanaged[Cdecl]<byte*, int>)address)(text);
                }
            }
        }

        _keysSet = true;
    }

    // AquesTalk10 の AQTK_VOICE：声の種類・速さ・音量・高さ・アクセント（抑揚）・音程 1・音程 2（どれも int）
    private readonly record struct AquesTalkVoice(int Bas, int Spd, int Vol, int Pit, int Acc, int Lmd, int Fsc);
}

/// <summary>
/// AquesTalk2。声は phont フォルダの素片ファイル（.phont）で選び、"default" はライブラリに組み込みの声。調整できるのは速さだけ
/// </summary>
public sealed unsafe class AquesTalk2Engine : AquesTalkEngineBase
{
    public const string EngineId = "aquestalk2";

    public const string DefaultVoice = "default";

    private readonly Lazy<UserLibrary?> _library;
    private readonly Dictionary<string, byte[]> _phonts = new(StringComparer.Ordinal);

    public AquesTalk2Engine(AquesTalkOptions options, IPhoneticConverter? converter = null)
        : base(options, converter)
    {
        _library = new Lazy<UserLibrary?>(() => UserLibrary.TryLoad(options.ProductFolder("aquestalk2"), "AquesTalk2", "AquesTalk2Eva"));
    }

    public override string Id => EngineId;

    public override string Name => "AquesTalk2";

    public override string Version => "2";

    private string PhontFolder => Path.Combine(Options.ProductFolder("aquestalk2"), "phont");

    public override IReadOnlyList<VoiceInfo> Voices =>
        [
            new(DefaultVoice, "標準の声"),
            .. (Directory.Exists(PhontFolder) ? Directory.EnumerateFiles(PhontFolder, "*.phont") : [])
                .Select(Path.GetFileNameWithoutExtension)
                .OfType<string>()
                .Order(StringComparer.Ordinal)
                .Select(name => new VoiceInfo(name, name)),
        ];

    public override VoiceEngineStatus Status =>
        _library.Value is not null
            ? VoiceEngineStatus.Ready
            : NotInstalled("AquesTalk2", $"{Path.Combine("engines", "aquestalk", "aquestalk2", AquesTalkOptions.Rid)}/", "https://www.a-quest.com/products/aquestalk_2.html");

    public override VoiceAudio Synthesize(VoiceRequest request, CancellationToken cancellationToken)
    {
        var library = _library.Value ?? throw new VoiceEngineException(Status.Message!);
        var synthe = (delegate* unmanaged[Cdecl]<byte*, int, int*, byte*, byte*>)library.RequireExport("AquesTalk2_Synthe_Utf8");
        var free = (delegate* unmanaged[Cdecl]<byte*, void>)library.RequireExport("AquesTalk2_FreeWave");
        var phont = Phont(request.VoiceId);
        fixed (byte* koe = Interop.Utf8(request.Reading ?? request.Text))
        fixed (byte* data = phont)
        {
            var size = 0;
            var wave = synthe(koe, Interop.Clamp(request.Params.Speed * 100, 50, 300), &size, phont is null ? null : data);
            return Interop.TakeWave(wave, size, free, request.Params.Volume, Name);
        }
    }

    public override void Dispose()
    {
        if (_library.IsValueCreated)
        {
            _library.Value?.Dispose();
        }
    }

    // 声の素片ファイルの中身（組み込みの声なら null）
    private byte[]? Phont(string voice)
    {
        if (voice == DefaultVoice)
        {
            return null;
        }

        if (!_phonts.TryGetValue(voice, out var data))
        {
            var path = Path.Combine(PhontFolder, voice + ".phont");
            data = File.Exists(path) ? File.ReadAllBytes(path) : throw new VoiceEngineException($"AquesTalk2 の声「{voice}」の素片ファイルが見つかりません");
            _phonts[voice] = data;
        }

        return data;
    }
}

/// <summary>
/// AqKanji2Koe。漢字かな交じり文を、AquesTalk が読む音声記号列（読み）に変える。辞書は aqkanji2koe/&lt;rid&gt;/aq_dic に置く
/// </summary>
public sealed unsafe class AqKanji2KoeConverter : IPhoneticConverter, IDisposable
{
    private readonly UserLibrary _library;
    private readonly Lock _gate = new();
    private nint _handle;

    private AqKanji2KoeConverter(UserLibrary library, nint handle)
    {
        _library = library;
        _handle = handle;
    }

    /// <summary>導入されていれば読み込む。ライブラリか辞書がなければ null</summary>
    /// <exception cref="VoiceEngineException">ライブラリはあるが、辞書を読み込めなかった</exception>
    public static AqKanji2KoeConverter? TryCreate(AquesTalkOptions options)
    {
        var folder = options.ProductFolder("aqkanji2koe");
        var dictionary = Path.Combine(folder, "aq_dic");
        if (!Directory.Exists(dictionary) || UserLibrary.TryLoad(folder, "AqKanji2Koe") is not { } library)
        {
            return null;
        }

        try
        {
            if (options.DevKey is { } devKey && library.Export("AqKanji2Koe_SetDevKey") is var setDevKey and not 0)
            {
                fixed (byte* key = Interop.Utf8(devKey))
                {
                    ((delegate* unmanaged[Cdecl]<byte*, int>)setDevKey)(key);
                }
            }

            var create = (delegate* unmanaged[Cdecl]<byte*, int*, nint>)library.RequireExport("AqKanji2Koe_Create");
            var error = 0;
            nint handle;
            fixed (byte* path = Interop.Utf8(dictionary))
            {
                handle = create(path, &error);
            }

            if (handle == 0)
            {
                throw new VoiceEngineException($"AqKanji2Koe の辞書を読み込めませんでした（エラー {error}）: {dictionary}");
            }

            return new AqKanji2KoeConverter(library, handle);
        }
        catch
        {
            library.Dispose();
            throw;
        }
    }

    /// <exception cref="VoiceEngineException">変換できなかった</exception>
    public string Convert(string text)
    {
        // Windows 版は UTF-8 用の関数の名前が違う
        var convert = (delegate* unmanaged[Cdecl]<nint, byte*, byte*, int, int>)_library.RequireExport(
            OperatingSystem.IsWindows() ? "AqKanji2Koe_Convert_utf8" : "AqKanji2Koe_Convert");
        var buffer = new byte[Math.Max(4096, Encoding.UTF8.GetByteCount(text) * 8)];
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_handle == 0, this);
            fixed (byte* kanji = Interop.Utf8(text))
            fixed (byte* koe = buffer)
            {
                var result = convert(_handle, kanji, koe, buffer.Length);
                if (result != 0)
                {
                    throw new VoiceEngineException($"AqKanji2Koe で読みを作れませんでした（エラー {result}）");
                }
            }
        }

        var length = Array.IndexOf(buffer, (byte)0);
        return Encoding.UTF8.GetString(buffer, 0, length < 0 ? buffer.Length : length);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_handle != 0)
            {
                var release = (delegate* unmanaged[Cdecl]<nint, void>)_library.RequireExport("AqKanji2Koe_Release");
                release(_handle);
                _handle = 0;
            }
        }

        _library.Dispose();
    }
}
