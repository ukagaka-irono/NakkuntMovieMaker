namespace NakkuntMovieMaker.Media.Tests;

/// <summary>
/// FFmpeg が必要なテストの前提。FFmpeg を用意していない環境ではスキップする。
/// CI では NMM_REQUIRE_FFMPEG=1 にして、スキップで失敗を見逃さないようにする
/// </summary>
internal static class Ffmpeg
{
    public static void RequireOrSkip()
    {
        var loaded = FfmpegLibrary.TryLoad(out _, out var error);
        var required = Environment.GetEnvironmentVariable("NMM_REQUIRE_FFMPEG") == "1";
        Assert.SkipWhen(!loaded && !required, $"FFmpeg を読み込めないためスキップします（{error}）");
        Assert.True(loaded, error);
    }

    public static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
