using System.Reflection;

namespace NakkuntMovieMaker.Engine.Tests;

public class ArchitectureTests
{
    // Engine とその下のプロジェクト（CLI・テスト・リモートレンダリングから使う部分）
    private static readonly string[] EngineLayer =
    [
        "NakkuntMovieMaker.Core",
        "NakkuntMovieMaker.Media",
        "NakkuntMovieMaker.Text",
        "NakkuntMovieMaker.Rendering",
        "NakkuntMovieMaker.Rendering.Skia",
        "NakkuntMovieMaker.Audio",
        "NakkuntMovieMaker.Voice",
        "NakkuntMovieMaker.Voice.AquesTalk",
        "NakkuntMovieMaker.Voice.Sapi",
        "NakkuntMovieMaker.Engine",
    ];

    public static TheoryData<string> EngineLayerAssemblies => new(EngineLayer);

    [Theory]
    [MemberData(nameof(EngineLayerAssemblies))]
    public void EngineLayerDoesNotDependOnAvalonia(string assemblyName)
    {
        var assembly = Assembly.Load(assemblyName);

        var uiReferences = assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? "")
            .Where(name => name.StartsWith("Avalonia", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(uiReferences);
    }
}
