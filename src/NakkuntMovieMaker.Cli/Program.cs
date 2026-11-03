using System.CommandLine;

namespace NakkuntMovieMaker.Cli;

internal static class Program
{
    // export / render-frame / probe / bench のサブコマンドは、エンジンの実装に合わせて追加する
    public static int Main(string[] args)
    {
        var root = new RootCommand("NakkuntMovieMaker のコマンドラインツール");
        return root.Parse(args).Invoke();
    }
}
