using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NakkuntMovieMaker.App.Services;
using NakkuntMovieMaker.Core.Assets;
using NakkuntMovieMaker.Core.Editing;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;
using NakkuntMovieMaker.Media;

namespace NakkuntMovieMaker.App.ViewModels;

/// <summary>素材の一覧の 1 行</summary>
public sealed partial class AssetEntry(Asset asset, string fullPath) : ObservableObject
{
    public Asset Asset { get; set; } = asset;

    public AssetId Id => Asset.Id;

    public string FullPath { get; set; } = fullPath;

    public string Name => Path.GetFileName(FullPath);

    public string KindLabel => Asset.Kind switch
    {
        AssetKind.Video => "動画",
        AssetKind.Image => "画像",
        _ => "音声",
    };

    public bool IsAudio => Asset.Kind == AssetKind.Audio;

    /// <summary>入力の色空間を指定していれば、その表示（自動なら空）</summary>
    public string ColorSpaceLabel => Asset.ColorSpace is { } space ? $"入力の色空間：{space}" : "";

    public bool HasColorSpace => Asset.ColorSpace is not null;

    /// <summary>入力の色空間で変換できない理由（変換せずに描いている）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasColorError))]
    public partial string? ColorError { get; set; }

    public bool HasColorError => ColorError is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail))]
    public partial Bitmap? Thumbnail { get; set; }

    public bool HasThumbnail => Thumbnail is not null;

    /// <summary>大きさ・長さなど</summary>
    [ObservableProperty]
    public partial string Details { get; set; } = "";

    [ObservableProperty]
    public partial bool IsMissing { get; set; }

    /// <summary>タイムラインで使っている数</summary>
    [ObservableProperty]
    public partial int UseCount { get; set; }
}

/// <summary>
/// 素材の一覧（メディアプール）。ファイルの追加（ドラッグ＆ドロップを含む）、サムネイル、リンク切れの表示と場所の指定し直し、
/// タイムラインへの配置、使っていない素材の削除、動画と画像の入力の色空間の指定
/// </summary>
public sealed partial class MediaPoolViewModel : ObservableObject
{
    private const int ThumbnailWidth = 96;
    private const int ThumbnailHeight = 54;

    private readonly EditorSession _session;
    private readonly IDialogService _dialogs;
    private readonly Dictionary<(AssetId, string), (Bitmap? Thumbnail, string Details)> _previews = [];

    public MediaPoolViewModel(EditorSession session, IDialogService dialogs)
    {
        _session = session;
        _dialogs = dialogs;
        session.Changed += (_, _) => Refresh();
        session.ProjectOpened += (_, _) =>
        {
            _previews.Clear();
            Refresh();
        };
        Refresh();
    }

    public ObservableCollection<AssetEntry> Assets { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddToTimelineCommand), nameof(RelinkCommand), nameof(RemoveCommand), nameof(SetInputColorSpaceCommand))]
    public partial AssetEntry? Selected { get; set; }

    /// <summary>入力の色空間の候補（プロジェクトの OpenColorIO の構成の色空間。Name が null なら自動）</summary>
    public ObservableCollection<ColorSpaceChoice> InputColorSpaces { get; } = [];

    // 候補を作った構成（構成が変わったら作り直す）
    private string? _inputColorSpacesConfig;

    /// <summary>リンク切れの素材の数</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMissing))]
    public partial int MissingCount { get; set; }

    public bool HasMissing => MissingCount > 0;

    public bool IsEmpty => Assets.Count == 0;

    /// <summary>入力の色空間で変換できるかを確かめ終えるまで待つ（テスト用）</summary>
    public Task ColorErrorsReady { get; private set; } = Task.CompletedTask;

    /// <summary>サムネイルを作り終えるまで待つ（テスト用）</summary>
    public Task PreviewsReady { get; private set; } = Task.CompletedTask;

    /// <summary>ファイルを素材に加える（ドラッグ＆ドロップからも呼ぶ）</summary>
    public async Task<IReadOnlyList<Asset>> ImportAsync(IEnumerable<string> paths)
    {
        var failures = new List<string>();
        var (operation, assets) = MediaItems.Import(_session.Project, _session.ProjectFolder, paths, failures);
        if (operation is not null && _session.Execute(operation) is { } error)
        {
            failures.Add(error);
        }

        if (failures.Count > 0)
        {
            await _dialogs.ShowErrorAsync("追加できないファイルがあります", string.Join("\n", failures));
        }

        return assets;
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        var paths = await _dialogs.PickOpenFilesAsync("素材の追加", [FileKinds.Media, FileKinds.All]);
        if (paths.Count > 0)
        {
            await ImportAsync(paths);
        }
    }

    /// <summary>選んだ素材を、再生位置の空いているレイヤーに置く</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task AddToTimelineAsync()
    {
        if (Selected is not { } entry)
        {
            return;
        }

        var (operation, item) = MediaItems.Place(_session.Project, _session.SceneId, _session.ProjectFolder, entry.Asset, _session.CurrentFrame, 0);
        if (_session.Execute(operation) is { } error)
        {
            await _dialogs.ShowErrorAsync("タイムラインに置けません", error);
            return;
        }

        _session.Selection = [item.Id];
    }

    /// <summary>素材のファイルを指定し直す</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RelinkAsync()
    {
        if (Selected is not { } entry || await _dialogs.PickOpenFileAsync($"「{entry.Name}」の場所", [FileKinds.Media, FileKinds.All]) is not { } path)
        {
            return;
        }

        if (_session.Execute(AssetLinks.Relink(_session.Project, entry.Id, path, _session.ProjectFolder)) is { } error)
        {
            await _dialogs.ShowErrorAsync("場所を指定し直せません", error);
        }
    }

    /// <summary>リンク切れの素材を、選んだフォルダの中から同じ名前で探して指定し直す</summary>
    [RelayCommand]
    private async Task SearchMissingAsync()
    {
        if (await _dialogs.PickFolderAsync("リンク切れの素材を探すフォルダ") is not { } folder)
        {
            return;
        }

        var found = AssetLinks.FindByFileName(_session.Project, _session.ProjectFolder, folder);
        if (found.Count > 0)
        {
            var operations = found.Select(pair => AssetLinks.Relink(_session.Project, pair.Key, pair.Value, _session.ProjectFolder));
            _session.Execute(Operations.Combine("素材の再指定", operations));
        }

        await _dialogs.ShowMessageAsync("リンク切れの素材を探す", $"{found.Count} 個の素材を見つけました（残り {MissingCount} 個）");
    }

    /// <summary>使っていない素材を一覧から外す（ファイルは消さない）</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RemoveAsync()
    {
        if (Selected is not { } entry)
        {
            return;
        }

        if (entry.UseCount > 0)
        {
            await _dialogs.ShowErrorAsync("素材を外せません", $"「{entry.Name}」はタイムラインで {entry.UseCount} 個使っています。先にアイテムを消してください");
            return;
        }

        _session.Execute(new SetAsset(entry.Id, null));
    }

    private bool HasSelection() => Selected is not null;

    /// <summary>選んだ素材（動画・画像）の入力の色空間を指定する（Name が null なら、動画の色のタグなどから自動で決める）</summary>
    [RelayCommand(CanExecute = nameof(HasPicture))]
    private void SetInputColorSpace(ColorSpaceChoice choice)
    {
        if (Selected is { } entry && entry.Asset.ColorSpace != choice.Name)
        {
            _session.Execute(new SetAsset(entry.Id, entry.Asset with { ColorSpace = choice.Name }));
        }
    }

    private bool HasPicture() => Selected is { IsAudio: false };

    // 候補を、プロジェクトの構成の色空間にする（構成が変わったときだけ作り直す）
    private void RefreshInputColorSpaces()
    {
        var config = _session.Project.Color.OcioConfig;
        if (InputColorSpaces.Count > 0 && config == _inputColorSpacesConfig)
        {
            return;
        }

        _inputColorSpacesConfig = config;
        InputColorSpaces.Clear();
        foreach (var choice in ColorChoices.ColorSpaces(config, "自動（動画の色の情報から決める）"))
        {
            InputColorSpaces.Add(choice);
        }
    }

    // 入力の色空間で変換できるかを裏で確かめる（変換の表を作るので時間がかかることがある）
    private async Task LoadColorErrorsAsync(IReadOnlyList<AssetEntry> entries)
    {
        var media = _session.Media;
        foreach (var entry in entries)
        {
            try
            {
                var error = await Task.Run(() => media.InputColorError(entry.Id));
                entry.ColorError = error;
            }
            catch (ObjectDisposedException)
            {
                return; // 別のプロジェクトを開いた
            }
        }
    }

    // プロジェクトに合わせて一覧を作り直す（サムネイルは素材とパスが同じなら使い回す）
    private void Refresh()
    {
        var project = _session.Project;
        var uses = project.Scenes.Values
            .SelectMany(scene => scene.Items.Values)
            .Select(item => item switch
            {
                VideoItem video => video.Asset,
                ImageItem image => image.Asset,
                AudioItem audio => audio.Asset,
                _ => (AssetId?)null,
            })
            .OfType<AssetId>()
            .GroupBy(id => id)
            .ToDictionary(group => group.Key, group => group.Count());

        var selected = Selected?.Id;
        Assets.Clear();
        var pending = new List<AssetEntry>();
        foreach (var asset in project.Assets.Values.OrderBy(asset => Path.GetFileName(asset.Path), StringComparer.CurrentCultureIgnoreCase))
        {
            var fullPath = AssetLinks.Resolve(asset, _session.ProjectFolder);
            var entry = new AssetEntry(asset, fullPath)
            {
                IsMissing = !File.Exists(fullPath),
                UseCount = uses.GetValueOrDefault(asset.Id),
            };
            if (entry.IsMissing)
            {
                entry.Details = "見つかりません";
            }
            else if (_previews.TryGetValue((asset.Id, fullPath), out var preview))
            {
                (entry.Thumbnail, entry.Details) = preview;
            }
            else
            {
                pending.Add(entry);
            }

            Assets.Add(entry);
        }

        Selected = Assets.FirstOrDefault(entry => entry.Id == selected);
        MissingCount = Assets.Count(entry => entry.IsMissing);
        RefreshInputColorSpaces();
        ColorErrorsReady = LoadColorErrorsAsync([.. Assets.Where(entry => !entry.IsAudio && !entry.IsMissing)]);
        OnPropertyChanged(nameof(IsEmpty));
        if (pending.Count > 0)
        {
            PreviewsReady = LoadPreviewsAsync(pending);
        }
    }

    // サムネイルと情報を裏で作る
    private async Task LoadPreviewsAsync(IReadOnlyList<AssetEntry> entries)
    {
        foreach (var entry in entries)
        {
            var (image, details) = await Task.Run(() => LoadPreview(entry.Asset.Kind, entry.FullPath));
            var thumbnail = image is null ? null : Bitmaps.Create(image);
            _previews[(entry.Id, entry.FullPath)] = (thumbnail, details);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                entry.Thumbnail = thumbnail;
                entry.Details = details;
            });
        }
    }

    private static (BgraImage? Image, string Details) LoadPreview(AssetKind kind, string path)
    {
        try
        {
            var info = MediaProbe.Probe(path);
            var length = info.Duration is { } duration ? FormatDuration(duration) : "";
            switch (kind)
            {
                case AssetKind.Video when info.Video is { } video:
                    var time = info.Duration is { } total && total.ToDouble() > 2 ? new Rational(1, 1) : Rational.Zero;
                    return (ThumbnailExtractor.Extract(path, time, ThumbnailWidth, ThumbnailHeight), $"{video.Width}×{video.Height}・{length}");
                case AssetKind.Image when info.Video is { } image:
                    return (ThumbnailExtractor.Extract(path, Rational.Zero, ThumbnailWidth, ThumbnailHeight), $"{image.Width}×{image.Height}");
                default:
                    return (null, info.Audio is { } audio ? $"{audio.SampleRate / 1000.0:0.#}kHz・{length}" : length);
            }
        }
        catch (Exception ex) when (ex is MediaException or InvalidOperationException or IOException)
        {
            return (null, "読めません");
        }
    }

    private static string FormatDuration(Rational seconds)
    {
        var time = TimeSpan.FromSeconds(seconds.ToDouble());
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss\.f");
    }
}
