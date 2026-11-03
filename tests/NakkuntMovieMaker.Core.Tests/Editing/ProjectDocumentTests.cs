using NakkuntMovieMaker.Core.Editing;

namespace NakkuntMovieMaker.Core.Tests.Editing;

public class ProjectDocumentTests
{
    private readonly TestProject _t = new();

    [Fact]
    public void ExecuteUndoRedo()
    {
        var document = new ProjectDocument(_t.Project);
        var move = EditCommands.MoveItems(_t.Project, _t.SceneId, [_t.Shape.Id], 5);

        document.Execute(move);
        var moved = document.Current;

        Assert.True(document.CanUndo);
        Assert.Equal("移動", document.UndoDescription);
        Assert.Equal(45, _t.Scene(moved).GetItem(_t.Shape.Id).Start);

        Assert.True(document.Undo());
        Assert.Equal(_t.Project, document.Current);
        Assert.True(document.CanRedo);
        Assert.Equal("移動", document.RedoDescription);

        Assert.True(document.Redo());
        Assert.Equal(moved, document.Current);
        Assert.False(document.CanRedo);
    }

    [Fact]
    public void ExecuteClearsRedoHistory()
    {
        var document = new ProjectDocument(_t.Project);
        document.Execute(EditCommands.MoveItems(document.Current, _t.SceneId, [_t.Shape.Id], 5));
        document.Undo();

        document.Execute(EditCommands.DeleteItems(document.Current, _t.SceneId, [_t.Image.Id]));

        Assert.False(document.CanRedo);
        Assert.False(document.Redo());
    }

    [Fact]
    public void UndoAndRedoReturnFalseWhenHistoryIsEmpty()
    {
        var document = new ProjectDocument(_t.Project);

        Assert.False(document.Undo());
        Assert.False(document.Redo());
        Assert.Null(document.UndoDescription);
    }

    [Fact]
    public void TracksModificationsAgainstTheSavedState()
    {
        var document = new ProjectDocument(_t.Project);
        Assert.False(document.IsModified);

        document.Execute(EditCommands.MoveItems(document.Current, _t.SceneId, [_t.Shape.Id], 5));
        Assert.True(document.IsModified);

        document.MarkSaved();
        Assert.False(document.IsModified);

        document.Undo();
        Assert.True(document.IsModified);

        document.Redo();
        Assert.False(document.IsModified);
    }

    [Fact]
    public void ChangedReportsKindAndDirtyRanges()
    {
        var document = new ProjectDocument(_t.Project);
        var changes = new List<DocumentChange>();
        document.Changed += (_, change) => changes.Add(change);

        document.Execute(EditCommands.MoveItems(document.Current, _t.SceneId, [_t.Shape.Id], 30));
        document.Undo();
        document.Redo();

        Assert.Equal([ChangeKind.Execute, ChangeKind.Undo, ChangeKind.Redo], changes.Select(change => change.Kind));
        Assert.Contains(new DirtyRange(_t.SceneId, 40, 50), changes[0].Dirty);
        Assert.Contains(new DirtyRange(_t.SceneId, 70, 80), changes[0].Dirty);
        Assert.Same(changes[0].Current, changes[1].Previous);
    }

    [Fact]
    public void FailedOperationsLeaveTheDocumentUnchanged()
    {
        var document = new ProjectDocument(_t.Project);
        var stale = new RemoveItem(_t.SceneId, _t.Shape.Id);
        document.Execute(stale);

        Assert.ThrowsAny<Exception>(() => document.Execute(stale)); // もう存在しないアイテムの削除

        Assert.False(_t.Scene(document.Current).Items.ContainsKey(_t.Shape.Id));
        Assert.True(document.Undo());
        Assert.Equal(_t.Project, document.Current);
        Assert.False(document.CanUndo);
    }
}
