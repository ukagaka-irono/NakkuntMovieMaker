using NakkuntMovieMaker.App.Services;
using NakkuntMovieMaker.App.ViewModels;

namespace NakkuntMovieMaker.App.Tests;

/// <summary>ダイアログの代わり。答えを先に積んでおき、開かれたダイアログを記録する</summary>
public sealed class FakeDialogs : IDialogService
{
    public List<string> Errors { get; } = [];

    public List<string> Messages { get; } = [];

    public Queue<string?> FilesToOpen { get; } = [];

    public Queue<string?> FilesToSave { get; } = [];

    public Queue<string?> Folders { get; } = [];

    public Queue<bool> Confirmations { get; } = [];

    public Queue<UnsavedChangesChoice> UnsavedAnswers { get; } = [];

    public Queue<string?> Prompts { get; } = [];

    /// <summary>プロジェクトの設定のダイアログで行うこと。false を返すとキャンセル</summary>
    public Func<ProjectSettingsViewModel, bool>? ProjectSettings { get; set; }

    /// <summary>ShowDialogAsync で開かれたダイアログで行うこと（ビューモデルを受け取り、結果を返す）</summary>
    public Func<object, object?>? Dialog { get; set; }

    public Task ShowErrorAsync(string title, string message)
    {
        Errors.Add($"{title}: {message}");
        return Task.CompletedTask;
    }

    public Task ShowMessageAsync(string title, string message)
    {
        Messages.Add($"{title}: {message}");
        return Task.CompletedTask;
    }

    public Task<bool> ConfirmAsync(string title, string message, string okText) =>
        Task.FromResult(Confirmations.Count > 0 && Confirmations.Dequeue());

    public Task<UnsavedChangesChoice> AskUnsavedChangesAsync(string projectName) =>
        Task.FromResult(UnsavedAnswers.Count > 0 ? UnsavedAnswers.Dequeue() : UnsavedChangesChoice.Cancel);

    public Task<string?> PromptAsync(string title, string label, string initial) =>
        Task.FromResult(Prompts.Count > 0 ? Prompts.Dequeue() : null);

    public Task<string?> PickOpenFileAsync(string title, IReadOnlyList<FileKind> kinds) =>
        Task.FromResult(FilesToOpen.Count > 0 ? FilesToOpen.Dequeue() : null);

    public Task<IReadOnlyList<string>> PickOpenFilesAsync(string title, IReadOnlyList<FileKind> kinds) =>
        Task.FromResult<IReadOnlyList<string>>(FilesToOpen.Count > 0 && FilesToOpen.Dequeue() is { } path ? [path] : []);

    public Task<string?> PickSaveFileAsync(string title, string suggestedName, IReadOnlyList<FileKind> kinds) =>
        Task.FromResult(FilesToSave.Count > 0 ? FilesToSave.Dequeue() : null);

    public Task<string?> PickFolderAsync(string title) =>
        Task.FromResult(Folders.Count > 0 ? Folders.Dequeue() : null);

    public Task<bool> ShowProjectSettingsAsync(ProjectSettingsViewModel settings) =>
        Task.FromResult(ProjectSettings?.Invoke(settings) ?? false);

    public Task<T?> ShowDialogAsync<T>(object view, object viewModel) =>
        Task.FromResult(Dialog?.Invoke(viewModel) is T result ? result : default);
}
