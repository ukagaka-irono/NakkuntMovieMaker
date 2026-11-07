using System.Collections.Immutable;

namespace NakkuntMovieMaker.ColorManagement;

/// <summary>OpenColorIO の処理で失敗した（構成・色空間が見つからないなど）</summary>
public sealed class OcioException(string message) : Exception(message);

/// <summary>OpenColorIO に組み込まれた構成（ACES の cg-config・studio-config）</summary>
/// <param name="Name">版付きの名前（プロジェクトに保存するのはこれ）</param>
/// <param name="UiName">人が読むための説明</param>
/// <param name="Recommended">今の版の OCIO が勧める構成か</param>
public sealed record OcioBuiltinConfig(string Name, string UiName, bool Recommended);

/// <summary>OpenColorIO（nmm_ocio）の入口</summary>
public static class Ocio
{
    private static readonly Lazy<(bool Loaded, string? Error)> State = new(Load);

    /// <summary>nmm_ocio を読み込めたか。読めなければ、色の変換は使えない（既定の設定なら変換は不要なので、そのまま描ける）</summary>
    public static bool TryLoad(out string? error)
    {
        (var loaded, error) = State.Value;
        return loaded;
    }

    /// <summary>組み込んでいる OpenColorIO の版（例: "2.5.2"）</summary>
    public static string Version => Require(() => OcioNative.Text(OcioNative.Version()) ?? "");

    /// <summary>nmm_ocio の API の版</summary>
    public static int ApiVersion => Require(OcioNative.ApiVersion);

    /// <summary>組み込みの構成の一覧</summary>
    public static ImmutableArray<OcioBuiltinConfig> BuiltinConfigs => Require(() =>
    {
        var count = OcioNative.BuiltinConfigCount();
        var configs = ImmutableArray.CreateBuilder<OcioBuiltinConfig>(Math.Max(0, count));
        for (var i = 0; i < count; i++)
        {
            configs.Add(new OcioBuiltinConfig(
                OcioNative.Text(OcioNative.BuiltinConfigName(i)) ?? "",
                OcioNative.Text(OcioNative.BuiltinConfigUiName(i)) ?? "",
                OcioNative.BuiltinConfigIsRecommended(i) != 0));
        }

        return configs.ToImmutable();
    });

    /// <summary>OCIO の既定の組み込みの構成の、版付きの名前</summary>
    public static string DefaultBuiltinConfig => Require(() => OcioNative.Text(OcioNative.DefaultBuiltinConfigName()) ?? throw new OcioException(OcioNative.Error()));

    private static T Require<T>(Func<T> read)
    {
        if (!TryLoad(out var error))
        {
            throw new OcioException(error ?? "nmm_ocio を読み込めません");
        }

        return read();
    }

    private static (bool, string?) Load()
    {
        try
        {
            var version = OcioNative.ApiVersion();
            return version == OcioNative.ExpectedApiVersion
                ? (true, null)
                : (false, $"nmm_ocio の API の版が違います（{version}。このソフトは {OcioNative.ExpectedApiVersion}）");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return (false, $"nmm_ocio を読み込めません（{ex.Message}）");
        }
    }
}

/// <summary>
/// OpenColorIO の構成。作ったあとは、複数のスレッドから同時に使ってよい
/// </summary>
public sealed class OcioConfig : IDisposable
{
    private readonly object _gate = new();
    private nint _handle;

    private OcioConfig(nint handle)
    {
        _handle = handle;
        Name = OcioNative.Text(OcioNative.ConfigName(handle)) ?? "";
        ColorSpaces = ReadList(OcioNative.ColorSpaceCount(handle), i => OcioNative.ColorSpaceName(handle, i));
        Displays = ReadList(OcioNative.DisplayCount(handle), i => OcioNative.DisplayName(handle, i));
        DefaultDisplay = OcioNative.Text(OcioNative.DefaultDisplay(handle)) ?? "";
    }

    /// <summary>構成の名前（組み込みの構成では、版付きの名前）</summary>
    public string Name { get; }

    /// <summary>色空間の名前（シーンとディスプレイの両方）</summary>
    public ImmutableArray<string> ColorSpaces { get; }

    /// <summary>ディスプレイの名前</summary>
    public ImmutableArray<string> Displays { get; }

    public string DefaultDisplay { get; }

    /// <summary>組み込みの構成を、版付きの名前（または "ocio://名前"・"default"）で読む</summary>
    /// <exception cref="OcioException">構成がない・nmm_ocio が読めない</exception>
    public static OcioConfig LoadBuiltin(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!Ocio.TryLoad(out var error))
        {
            throw new OcioException(error ?? "nmm_ocio を読み込めません");
        }

        return OcioNative.ConfigCreateBuiltin(name, out var handle) == 0
            ? new OcioConfig(handle)
            : throw new OcioException($"OpenColorIO の構成「{name}」を読めません（{OcioNative.Error()}）");
    }

    /// <summary>ディスプレイのビューの名前</summary>
    public ImmutableArray<string> Views(string display)
    {
        var handle = Handle();
        return ReadList(OcioNative.ViewCount(handle, display), i => OcioNative.ViewName(handle, display, i));
    }

    public string? DefaultView(string display) => OcioNative.Text(OcioNative.DefaultView(Handle(), display));

    /// <summary>名前・別名・ロールから、色空間の正式な名前（ないなら null）</summary>
    public string? CanonicalColorSpace(string name) => OcioNative.Text(OcioNative.CanonicalColorSpace(Handle(), name));

    /// <summary>色空間 source から destination への変換</summary>
    public OcioProcessor CreateProcessor(string source, string destination) =>
        OcioNative.ProcessorCreate(Handle(), source, destination, out var processor) == 0
            ? new OcioProcessor(processor)
            : throw new OcioException($"「{source}」から「{destination}」への変換を作れません（{OcioNative.Error()}）");

    /// <summary>色空間 source から、ディスプレイとビューへの変換（inverse なら、その逆）</summary>
    public OcioProcessor CreateDisplayViewProcessor(string source, string display, string view, bool inverse = false) =>
        OcioNative.ProcessorCreateDisplayView(Handle(), source, display, view, inverse ? 1 : 0, out var processor) == 0
            ? new OcioProcessor(processor)
            : throw new OcioException($"「{source}」から「{display} / {view}」への変換を作れません（{OcioNative.Error()}）");

    public void Dispose()
    {
        lock (_gate)
        {
            if (_handle != 0)
            {
                OcioNative.ConfigDestroy(_handle);
                _handle = 0;
            }
        }
    }

    private nint Handle()
    {
        var handle = _handle;
        ObjectDisposedException.ThrowIf(handle == 0, this);
        return handle;
    }

    private static ImmutableArray<string> ReadList(int count, Func<int, nint> read)
    {
        var names = ImmutableArray.CreateBuilder<string>(Math.Max(0, count));
        for (var i = 0; i < count; i++)
        {
            if (OcioNative.Text(read(i)) is { } name)
            {
                names.Add(name);
            }
        }

        return names.ToImmutable();
    }
}

/// <summary>色の変換（CPU）。作ったあとは、複数のスレッドから同時に使ってよい</summary>
public sealed unsafe class OcioProcessor : IDisposable
{
    private readonly object _gate = new();
    private nint _handle;

    internal OcioProcessor(nint handle)
    {
        _handle = handle;
        IsNoOp = OcioNative.ProcessorIsNoOp(handle) != 0;
        HasChannelCrosstalk = OcioNative.ProcessorHasChannelCrosstalk(handle) != 0;
    }

    /// <summary>何もしない変換か</summary>
    public bool IsNoOp { get; }

    /// <summary>ある色のチャンネルが、ほかのチャンネルの結果に影響するか（1D の LUT では表せない）</summary>
    public bool HasChannelCrosstalk { get; }

    /// <summary>RGB（3 つずつ詰めたもの）を、その場で変換する</summary>
    public void Apply(Span<float> rgb)
    {
        if (rgb.Length % 3 != 0)
        {
            throw new ArgumentException("RGB の数（3 の倍数）にしてください", nameof(rgb));
        }

        var handle = _handle;
        ObjectDisposedException.ThrowIf(handle == 0, this);
        fixed (float* pointer = rgb)
        {
            if (OcioNative.ProcessorApplyRgb(handle, pointer, rgb.Length / 3) != 0)
            {
                throw new OcioException($"色を変換できません（{OcioNative.Error()}）");
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_handle != 0)
            {
                OcioNative.ProcessorDestroy(_handle);
                _handle = 0;
            }
        }
    }
}
