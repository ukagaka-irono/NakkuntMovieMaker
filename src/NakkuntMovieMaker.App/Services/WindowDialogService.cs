using Avalonia.Controls;
using Avalonia.Platform.Storage;
using NakkuntMovieMaker.App.ViewModels;
using NakkuntMovieMaker.App.Views.Dialogs;

namespace NakkuntMovieMaker.App.Services;

/// <summary>メインウィンドウを親にしてダイアログを開く</summary>
public sealed class WindowDialogService(Window owner) : IDialogService
{
    public async Task ShowErrorAsync(string title, string message) =>
        await new MessageDialog(title, message, ["OK"]).ShowDialog<int?>(owner);

    public async Task ShowMessageAsync(string title, string message) =>
        await new MessageDialog(title, message, ["OK"]).ShowDialog<int?>(owner);

    public async Task<bool> ConfirmAsync(string title, string message, string okText) =>
        await new MessageDialog(title, message, [okText, "キャンセル"]).ShowDialog<int?>(owner) == 0;

    public async Task<UnsavedChangesChoice> AskUnsavedChangesAsync(string projectName)
    {
        var answer = await new MessageDialog("保存の確認", $"「{projectName}」の変更を保存しますか？", ["保存する", "保存しない", "キャンセル"]).ShowDialog<int?>(owner);
        return answer switch
        {
            0 => UnsavedChangesChoice.Save,
            1 => UnsavedChangesChoice.Discard,
            _ => UnsavedChangesChoice.Cancel,
        };
    }

    public async Task<string?> PromptAsync(string title, string label, string initial, bool multiline = false) =>
        await new TextInputDialog(title, label, initial, multiline).ShowDialog<string?>(owner);

    public async Task<string?> PickOpenFileAsync(string title, IReadOnlyList<FileKind> kinds) =>
        (await PickOpenAsync(title, kinds, allowMultiple: false)).FirstOrDefault();

    public Task<IReadOnlyList<string>> PickOpenFilesAsync(string title, IReadOnlyList<FileKind> kinds) =>
        PickOpenAsync(title, kinds, allowMultiple: true);

    public async Task<string?> PickSaveFileAsync(string title, string suggestedName, IReadOnlyList<FileKind> kinds)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            FileTypeChoices = [.. kinds.Select(ToFilter)],
            DefaultExtension = kinds.FirstOrDefault()?.Patterns.FirstOrDefault()?.TrimStart('*'),
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<bool> ShowProjectSettingsAsync(ProjectSettingsViewModel settings) =>
        await new ProjectSettingsDialog { DataContext = settings }.ShowDialog<bool?>(owner) == true;

    public async Task<T?> ShowDialogAsync<T>(object view, object viewModel)
    {
        var window = (Window)view;
        window.DataContext = viewModel;
        return await window.ShowDialog<T?>(owner);
    }

    private async Task<IReadOnlyList<string>> PickOpenAsync(string title, IReadOnlyList<FileKind> kinds, bool allowMultiple)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = allowMultiple,
            FileTypeFilter = [.. kinds.Select(ToFilter)],
        });
        return [.. files.Select(file => file.TryGetLocalPath()).OfType<string>()];
    }

    private static FilePickerFileType ToFilter(FileKind kind) => new(kind.Name) { Patterns = [.. kind.Patterns] };
}
