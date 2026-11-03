using System.Collections.Immutable;
using NakkuntMovieMaker.Core.Model;

namespace NakkuntMovieMaker.Core.Editing;

/// <summary>変更の種類</summary>
public enum ChangeKind
{
    Execute,
    Undo,
    Redo,
}

/// <summary>プロジェクトが変わったときの通知。Dirty は描き直しが必要な範囲（キャッシュの無効化に使う）</summary>
public sealed record DocumentChange(ChangeKind Kind, string Description, Project Previous, Project Current, ImmutableArray<DirtyRange> Dirty);

/// <summary>
/// 編集中のプロジェクト。今のスナップショットを持ち、操作の適用・元に戻す・やり直しを管理する。
/// スナップショットは不変なので、描画スレッドは Current を読むだけでよく、ロックは要らない
/// </summary>
public sealed class ProjectDocument
{
    private readonly Lock _gate = new();
    private readonly Stack<HistoryEntry> _undo = new();
    private readonly Stack<HistoryEntry> _redo = new();
    private Project _current;
    private long _state;
    private long _nextState = 1;
    private long _savedState;

    public ProjectDocument(Project project)
    {
        _current = project;
    }

    /// <summary>プロジェクトが変わった（編集・元に戻す・やり直し）。変更したスレッドで呼ばれる</summary>
    public event EventHandler<DocumentChange>? Changed;

    /// <summary>今のスナップショット</summary>
    public Project Current => Volatile.Read(ref _current);

    public bool CanUndo
    {
        get
        {
            lock (_gate)
            {
                return _undo.Count > 0;
            }
        }
    }

    public bool CanRedo
    {
        get
        {
            lock (_gate)
            {
                return _redo.Count > 0;
            }
        }
    }

    /// <summary>元に戻す操作の説明。なければ null</summary>
    public string? UndoDescription
    {
        get
        {
            lock (_gate)
            {
                return _undo.TryPeek(out var entry) ? entry.Description : null;
            }
        }
    }

    /// <summary>やり直す操作の説明。なければ null</summary>
    public string? RedoDescription
    {
        get
        {
            lock (_gate)
            {
                return _redo.TryPeek(out var entry) ? entry.Description : null;
            }
        }
    }

    /// <summary>最後に保存してから変更されたか（元に戻して保存時の状態に戻れば false）</summary>
    public bool IsModified
    {
        get
        {
            lock (_gate)
            {
                return _state != _savedState;
            }
        }
    }

    /// <summary>今の状態を保存済みにする</summary>
    public void MarkSaved()
    {
        lock (_gate)
        {
            _savedState = _state;
        }
    }

    /// <summary>今の状態を、保存していない状態にする（自動保存から復元したときなど）</summary>
    public void MarkUnsaved()
    {
        lock (_gate)
        {
            _savedState = -1;
        }
    }

    /// <summary>操作を適用する。やり直しの履歴は消える</summary>
    /// <exception cref="EditException">操作を適用できなかった（プロジェクトは変わらない）</exception>
    public void Execute(IOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        DocumentChange change;
        lock (_gate)
        {
            var previous = _current;
            var result = operation.Apply(previous);
            _undo.Push(new HistoryEntry(operation.Description, result.Inverse, _state));
            _redo.Clear();
            _state = _nextState++;
            Volatile.Write(ref _current, result.Project);
            change = new DocumentChange(ChangeKind.Execute, operation.Description, previous, result.Project, result.Dirty);
        }

        Changed?.Invoke(this, change);
    }

    /// <summary>最後の操作を元に戻す。戻せるものがなければ false</summary>
    public bool Undo() => Step(_undo, _redo, ChangeKind.Undo);

    /// <summary>元に戻した操作をやり直す。やり直せるものがなければ false</summary>
    public bool Redo() => Step(_redo, _undo, ChangeKind.Redo);

    private bool Step(Stack<HistoryEntry> from, Stack<HistoryEntry> to, ChangeKind kind)
    {
        DocumentChange change;
        lock (_gate)
        {
            if (!from.TryPop(out var entry))
            {
                return false;
            }

            var previous = _current;
            var result = entry.Operation.Apply(previous);
            to.Push(new HistoryEntry(entry.Description, result.Inverse, _state));
            _state = entry.StateBefore;
            Volatile.Write(ref _current, result.Project);
            change = new DocumentChange(kind, entry.Description, previous, result.Project, result.Dirty);
        }

        Changed?.Invoke(this, change);
        return true;
    }

    // Operation を適用すると、StateBefore の状態に戻る
    private sealed record HistoryEntry(string Description, IOperation Operation, long StateBefore);
}
