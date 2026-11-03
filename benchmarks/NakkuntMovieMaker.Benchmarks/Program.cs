using BenchmarkDotNet.Running;

namespace NakkuntMovieMaker.Benchmarks;

internal static class Program
{
    // ベンチマークはエンジンの実装に合わせて追加する。実行例: dotnet run -c Release -- --filter *
    public static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
