using System.Collections.Immutable;
using NakkuntMovieMaker.Core.Model;

namespace NakkuntMovieMaker.Engine.Tachie;

/// <summary>立ち絵のパーツの種類。目と口だけが動く（目パチ・口パク）</summary>
public enum TachiePartKind
{
    Static,
    Eyes,
    Mouth,
}

/// <summary>
/// 立ち絵の 1 つのパーツ。Frames は動きの画像（ファイルの場所）を、閉じた状態から開いた状態の順に並べたもの。
/// 動かないパーツは 1 枚だけ
/// </summary>
public sealed record TachiePart(string Name, TachiePartKind Kind, ImmutableArray<string> Frames);

/// <summary>読み込んだ立ち絵。Parts は描く順（奥から手前）</summary>
public sealed record TachieModel(ImmutableArray<TachiePart> Parts);

/// <summary>
/// 立ち絵を読み込む。
/// <list type="bullet">
/// <item>シンプル立ち絵：画像 1 枚（口パク・目パチはしない）</item>
/// <item>パーツ式（YMM4 の「動く立ち絵」と同じフォルダ構成）：後・体・顔色・口・目・眉・髪・他 のフォルダに PNG を置き、
/// この順に重ねる（同じ大きさの画像を、左上をそろえて重ねる）。
/// 目と口は、「名前.0.png」「名前.1.png」…（閉じた状態から順に）と「名前.png」（いちばん開いた状態）を 1 組の動きとする。
/// フォルダに画像が何組かあるときは、名前の順で最初の組を使う</item>
/// </list>
/// 読み込んだものは覚えておく（素材を差し替えたら Clear する）
/// </summary>
public sealed class TachieLoader
{
    /// <summary>パーツのフォルダの名前と種類（描く順）</summary>
    public static readonly ImmutableArray<(string Folder, TachiePartKind Kind)> PartFolders =
    [
        ("後", TachiePartKind.Static),
        ("体", TachiePartKind.Static),
        ("顔色", TachiePartKind.Static),
        ("口", TachiePartKind.Mouth),
        ("目", TachiePartKind.Eyes),
        ("眉", TachiePartKind.Static),
        ("髪", TachiePartKind.Static),
        ("他", TachiePartKind.Static),
    ];

    private readonly string _projectFolder;
    private readonly Dictionary<TachieSpec, TachieModel?> _loaded = new();
    private readonly Lock _gate = new();

    public TachieLoader(string projectFolder)
    {
        _projectFolder = projectFolder;
    }

    /// <summary>立ち絵を読み込む。ファイル・フォルダがなければ null</summary>
    public TachieModel? Load(TachieSpec spec)
    {
        lock (_gate)
        {
            if (!_loaded.TryGetValue(spec, out var model))
            {
                model = Read(spec);
                _loaded[spec] = model;
            }

            return model;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _loaded.Clear();
        }
    }

    private TachieModel? Read(TachieSpec spec)
    {
        var path = Path.IsPathRooted(spec.Path) ? spec.Path : Path.GetFullPath(Path.Combine(_projectFolder, spec.Path));
        if (spec.Kind == TachieKind.Simple)
        {
            return File.Exists(path) ? new TachieModel([new TachiePart("立ち絵", TachiePartKind.Static, [path])]) : null;
        }

        if (!Directory.Exists(path))
        {
            return null;
        }

        var parts = ImmutableArray.CreateBuilder<TachiePart>();
        foreach (var (folder, kind) in PartFolders)
        {
            var directory = Path.Combine(path, folder);
            if (Directory.Exists(directory) && FirstSet(directory) is { Length: > 0 } frames)
            {
                parts.Add(new TachiePart(folder, kind, kind == TachiePartKind.Static ? [frames[^1]] : frames));
            }
        }

        return new TachieModel(parts.ToImmutable());
    }

    // フォルダの中の最初の組（「名前.数字.png」を数字の順に並べ、最後に「名前.png」）
    private static ImmutableArray<string> FirstSet(string directory)
    {
        var sets = new SortedDictionary<string, List<(int Order, string Path)>>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(directory, "*.png"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var dot = name.LastIndexOf('.');
            var (baseName, order) = dot > 0 && int.TryParse(name[(dot + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)
                ? (name[..dot], number)
                : (name, int.MaxValue);
            if (!sets.TryGetValue(baseName, out var frames))
            {
                frames = [];
                sets[baseName] = frames;
            }

            frames.Add((order, file));
        }

        return sets.Count == 0 ? [] : [.. sets.First().Value.OrderBy(frame => frame.Order).Select(frame => frame.Path)];
    }
}
