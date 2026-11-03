using NakkuntMovieMaker.Text;

namespace NakkuntMovieMaker.Rendering.Tests;

/// <summary>
/// 同梱のフォントが必要なテストの前提。tools/fetch-native でフォントを取得していない環境ではスキップする。
/// CI では NMM_REQUIRE_FONTS=1 にして、スキップで失敗を見逃さないようにする
/// </summary>
internal static class Fonts
{
    public static void RequireOrSkip()
    {
        var available = FontProvider.Default.HasBundledFont;
        var required = Environment.GetEnvironmentVariable("NMM_REQUIRE_FONTS") == "1";
        Assert.SkipWhen(!available && !required, "同梱のフォントがないためスキップします（tools/fetch-native で取得できます）");
        Assert.True(available, $"同梱のフォント（{FontProvider.BundledFamily}）が見つかりません");
    }
}
