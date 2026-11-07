using System.Runtime.InteropServices;

namespace NakkuntMovieMaker.ColorManagement;

/// <summary>nmm_ocio（OpenColorIO の C ラッパー、native/nmm_ocio/include/nmm_ocio.h）の関数</summary>
internal static unsafe partial class OcioNative
{
    /// <summary>このプログラムが前提にしている nmm_ocio の API のバージョン</summary>
    public const int ExpectedApiVersion = 1;

    private const string Library = "nmm_ocio";

    [LibraryImport(Library, EntryPoint = "nmm_ocio_api_version")]
    public static partial int ApiVersion();

    [LibraryImport(Library, EntryPoint = "nmm_ocio_version")]
    public static partial nint Version();

    [LibraryImport(Library, EntryPoint = "nmm_ocio_last_error")]
    public static partial nint LastError();

    [LibraryImport(Library, EntryPoint = "nmm_ocio_builtin_config_count")]
    public static partial int BuiltinConfigCount();

    [LibraryImport(Library, EntryPoint = "nmm_ocio_builtin_config_name")]
    public static partial nint BuiltinConfigName(int index);

    [LibraryImport(Library, EntryPoint = "nmm_ocio_builtin_config_ui_name")]
    public static partial nint BuiltinConfigUiName(int index);

    [LibraryImport(Library, EntryPoint = "nmm_ocio_builtin_config_is_recommended")]
    public static partial int BuiltinConfigIsRecommended(int index);

    [LibraryImport(Library, EntryPoint = "nmm_ocio_default_builtin_config_name")]
    public static partial nint DefaultBuiltinConfigName();

    [LibraryImport(Library, EntryPoint = "nmm_ocio_config_create_builtin", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int ConfigCreateBuiltin(string name, out nint config);

    [LibraryImport(Library, EntryPoint = "nmm_ocio_config_destroy")]
    public static partial void ConfigDestroy(nint config);

    [LibraryImport(Library, EntryPoint = "nmm_ocio_config_name")]
    public static partial nint ConfigName(nint config);

    [LibraryImport(Library, EntryPoint = "nmm_ocio_config_color_space_count")]
    public static partial int ColorSpaceCount(nint config);

    [LibraryImport(Library, EntryPoint = "nmm_ocio_config_color_space_name")]
    public static partial nint ColorSpaceName(nint config, int index);

    [LibraryImport(Library, EntryPoint = "nmm_ocio_config_canonical_color_space", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint CanonicalColorSpace(nint config, string name);

    [LibraryImport(Library, EntryPoint = "nmm_ocio_config_display_count")]
    public static partial int DisplayCount(nint config);

    [LibraryImport(Library, EntryPoint = "nmm_ocio_config_display_name")]
    public static partial nint DisplayName(nint config, int index);

    [LibraryImport(Library, EntryPoint = "nmm_ocio_config_default_display")]
    public static partial nint DefaultDisplay(nint config);

    [LibraryImport(Library, EntryPoint = "nmm_ocio_config_view_count", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int ViewCount(nint config, string display);

    [LibraryImport(Library, EntryPoint = "nmm_ocio_config_view_name", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint ViewName(nint config, string display, int index);

    [LibraryImport(Library, EntryPoint = "nmm_ocio_config_default_view", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint DefaultView(nint config, string display);

    [LibraryImport(Library, EntryPoint = "nmm_ocio_processor_create", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int ProcessorCreate(nint config, string source, string destination, out nint processor);

    [LibraryImport(Library, EntryPoint = "nmm_ocio_processor_create_display_view", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int ProcessorCreateDisplayView(nint config, string source, string display, string view, int inverse, out nint processor);

    [LibraryImport(Library, EntryPoint = "nmm_ocio_processor_destroy")]
    public static partial void ProcessorDestroy(nint processor);

    [LibraryImport(Library, EntryPoint = "nmm_ocio_processor_is_noop")]
    public static partial int ProcessorIsNoOp(nint processor);

    [LibraryImport(Library, EntryPoint = "nmm_ocio_processor_has_channel_crosstalk")]
    public static partial int ProcessorHasChannelCrosstalk(nint processor);

    [LibraryImport(Library, EntryPoint = "nmm_ocio_processor_apply_rgb")]
    public static partial int ProcessorApplyRgb(nint processor, float* rgb, long pixelCount);

    /// <summary>nmm_ocio が返した UTF-8 の文字列（NULL なら null）</summary>
    public static string? Text(nint pointer) => pointer == 0 ? null : Marshal.PtrToStringUTF8(pointer);

    /// <summary>このスレッドで最後に失敗した理由</summary>
    public static string Error() => Text(LastError()) is { Length: > 0 } message ? message : "理由はわかりません";
}
