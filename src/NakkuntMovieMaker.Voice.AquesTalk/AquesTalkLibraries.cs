using System.Runtime.InteropServices;
using System.Text;

namespace NakkuntMovieMaker.Voice.AquesTalk;

/// <summary>
/// AquesTalk の置き場所と使用ライセンスのキー。Folder は engines/aquestalk（設定画面で変えられる）で、その下に
/// <list type="bullet">
/// <item>aquestalk1/&lt;rid&gt;/&lt;声の名前&gt;/ … AquesTalk1 のライブラリ（声ごとに 1 つ）</item>
/// <item>aquestalk10/&lt;rid&gt;/ … AquesTalk10 のライブラリ</item>
/// <item>aquestalk2/&lt;rid&gt;/ … AquesTalk2 のライブラリと、声の素片（phont フォルダの .phont）</item>
/// <item>aqkanji2koe/&lt;rid&gt;/ … AqKanji2Koe のライブラリと辞書（aq_dic フォルダ）</item>
/// </list>
/// を置く（rid は win-x64・linux-x64・osx-arm64 など）。キーは、利用者が A-quest から購入したもの（なければ評価版として動く）
/// </summary>
public sealed record AquesTalkOptions(string Folder, string? UsrKey = null, string? DevKey = null)
{
    /// <summary>アプリのフォルダの engines/aquestalk</summary>
    public static AquesTalkOptions Default { get; } = new(Path.Combine(AppContext.BaseDirectory, "engines", "aquestalk"));

    /// <summary>今の環境の RID（win-x64 など）</summary>
    public static string Rid => RuntimeInformation.RuntimeIdentifier;

    public string ProductFolder(string product) => Path.Combine(Folder, product, Rid);
}

/// <summary>利用者が置いたネイティブのライブラリを読み込み、関数を取り出す</summary>
internal sealed class UserLibrary : IDisposable
{
    private nint _handle;

    private UserLibrary(nint handle, string path)
    {
        _handle = handle;
        Path = path;
    }

    public string Path { get; }

    /// <summary>
    /// folder にある、名前が stems のどれかに当たるライブラリを読み込む（Windows は Name.dll、Linux は libName.so*、macOS は libName.dylib）。
    /// 見つからない・読み込めないときは null
    /// </summary>
    public static UserLibrary? TryLoad(string folder, params string[] stems)
    {
        if (!Directory.Exists(folder))
        {
            return null;
        }

        foreach (var path in Candidates(folder, stems))
        {
            if (NativeLibrary.TryLoad(path, out var handle))
            {
                return new UserLibrary(handle, path);
            }
        }

        return null;
    }

    /// <summary>folder に、名前が stems のどれかに当たるライブラリのファイルがあるか（読み込みはしない）</summary>
    public static bool Exists(string folder, params string[] stems) =>
        Directory.Exists(folder) && Candidates(folder, stems).Any(File.Exists);

    public nint Export(string name) =>
        NativeLibrary.TryGetExport(Handle, name, out var address) ? address : 0;

    public nint RequireExport(string name) =>
        Export(name) is var address and not 0 ? address : throw new VoiceEngineException($"{System.IO.Path.GetFileName(Path)} に {name} がありません（対応していない版です）");

    private nint Handle => _handle != 0 ? _handle : throw new ObjectDisposedException(nameof(UserLibrary));

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0)
        {
            NativeLibrary.Free(handle);
        }
    }

    private static IEnumerable<string> Candidates(string folder, string[] stems)
    {
        foreach (var stem in stems)
        {
            if (OperatingSystem.IsWindows())
            {
                yield return System.IO.Path.Combine(folder, stem + ".dll");
            }
            else if (OperatingSystem.IsMacOS())
            {
                yield return System.IO.Path.Combine(folder, $"lib{stem}.dylib");
            }
            else
            {
                // Linux では libAquesTalk.so.1.0 のように版の付いた名前のこともある
                foreach (var path in Directory.EnumerateFiles(folder, $"lib{stem}.so*").Order(StringComparer.Ordinal))
                {
                    yield return path;
                }
            }
        }
    }
}

/// <summary>ライブラリとのやり取りの道具（UTF-8 の文字列と、ライブラリが確保した WAV）</summary>
internal static unsafe class Interop
{
    /// <summary>NUL で終わる UTF-8 の文字列にする</summary>
    public static byte[] Utf8(string text)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(text) + 1];
        Encoding.UTF8.GetBytes(text, bytes);
        return bytes;
    }

    /// <summary>ライブラリが返した WAV（size バイト）を読み、free で解放する。volume を掛ける</summary>
    public static VoiceAudio TakeWave(byte* wave, int size, delegate* unmanaged[Cdecl]<byte*, void> free, double volume, string engine)
    {
        if (wave is null)
        {
            // 失敗したときは、size にエラーの番号が入る
            throw new VoiceEngineException($"{engine} で合成できませんでした（エラー {size}。読みに使えない記号が含まれていないか確かめてください）");
        }

        try
        {
            var wav = Audio.WavReader.Read(new ReadOnlySpan<byte>(wave, size), engine);
            var samples = new float[wav.FrameCount];
            var gain = (float)Math.Max(0, volume);
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = Math.Clamp(wav.Samples[i * wav.Channels] * gain, -1f, 1f);
            }

            return new VoiceAudio(samples, wav.SampleRate);
        }
        finally
        {
            free(wave);
        }
    }

    public static int Clamp(double value, int min, int max) => (int)Math.Clamp(Math.Round(value), min, max);
}
