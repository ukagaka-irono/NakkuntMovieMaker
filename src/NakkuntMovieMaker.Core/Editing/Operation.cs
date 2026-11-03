using System.Collections.Immutable;
using NakkuntMovieMaker.Core.Model;

namespace NakkuntMovieMaker.Core.Editing;

/// <summary>
/// 編集で描き直しが必要になった範囲。Scene が null ならすべてのシーン。
/// フレームの範囲は [Start, End) で、シーン全体なら long の最小値から最大値まで
/// </summary>
public readonly record struct DirtyRange(SceneId? Scene, long Start, long End)
{
    /// <summary>すべてのシーンのすべてのフレーム（キャラクターや素材の変更など）</summary>
    public static readonly DirtyRange Everything = new(null, long.MinValue, long.MaxValue);

    public static DirtyRange WholeScene(SceneId scene) => new(scene, long.MinValue, long.MaxValue);

    public static DirtyRange OfItem(SceneId scene, Item item) => new(scene, item.Start, item.End);

    /// <summary>指定したシーンのフレーム範囲と重なるか</summary>
    public bool Overlaps(SceneId scene, long start, long end) =>
        (Scene is null || Scene == scene) && Start < end && start < End;
}

/// <summary>操作を適用した結果。Inverse を適用すると、適用前の状態に戻る</summary>
public sealed record OperationResult(Project Project, IOperation Inverse, ImmutableArray<DirtyRange> Dirty);

/// <summary>
/// プロジェクトへの編集操作。適用するときに、元に戻すための逆の操作を作る。
/// 操作は不変で、同じ状態に適用すれば必ず同じ結果になる
/// </summary>
public interface IOperation
{
    /// <summary>操作の説明（「移動」「分割」など。元に戻すメニューに表示する）</summary>
    string Description { get; }

    /// <exception cref="EditException">この状態には適用できない</exception>
    OperationResult Apply(Project project);
}

/// <summary>複数の操作を 1 つにまとめたもの。元に戻すときは 1 回で全部戻る</summary>
public sealed record Transaction(string Description, ImmutableArray<IOperation> Operations) : IOperation
{
    public OperationResult Apply(Project project)
    {
        var inverses = new List<IOperation>(Operations.Length);
        var dirty = ImmutableArray.CreateBuilder<DirtyRange>();
        foreach (var operation in Operations)
        {
            var result = operation.Apply(project);
            project = result.Project;
            inverses.Add(result.Inverse);
            dirty.AddRange(result.Dirty);
        }

        inverses.Reverse();
        return new OperationResult(project, new Transaction(Description, [.. inverses]), dirty.ToImmutable());
    }
}

/// <summary>編集できない操作をしようとした（重なり、ロック中のレイヤーなど）。Message は利用者に表示できる文</summary>
public sealed class EditException(string message) : Exception(message);
