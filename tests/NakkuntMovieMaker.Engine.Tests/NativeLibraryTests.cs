using NakkuntMovieMaker.Audio;
using NakkuntMovieMaker.Media;

namespace NakkuntMovieMaker.Engine.Tests;

// tools/fetch-native と tools/build-native で用意したネイティブライブラリを読み込めるか確かめる。
// 用意していない環境ではスキップする。CI では環境変数で「必須」にして、スキップで失敗を見逃さないようにする
public class NativeLibraryTests
{
    [Fact]
    public void FfmpegLoadsAsLgpl()
    {
        var loaded = FfmpegLibrary.TryLoad(out var info, out var error);
        SkipUnlessRequired(loaded, "NMM_REQUIRE_FFMPEG", $"FFmpeg を読み込めないためスキップします（{error}）");

        Assert.True(loaded, error);
        Assert.StartsWith("9.", info!.Version.TrimStart('n'), StringComparison.Ordinal);
        Assert.True(info.IsLgpl, $"LGPL ではない構成です: {info.License}");
    }

    [Fact]
    public void NmmAudioLoads()
    {
        var loaded = NativeAudio.TryGetInfo(out var info, out var error);
        SkipUnlessRequired(loaded, "NMM_REQUIRE_NMM_AUDIO", $"nmm_audio を読み込めないためスキップします（{error}）");

        Assert.True(loaded, error);
        Assert.Equal(NativeAudio.ExpectedApiVersion, info!.ApiVersion);
        Assert.Equal("0.11.25", info.MiniaudioVersion);
    }

    private static void SkipUnlessRequired(bool loaded, string requireVariable, string reason)
    {
        var required = Environment.GetEnvironmentVariable(requireVariable) == "1";
        Assert.SkipWhen(!loaded && !required, reason);
    }
}
