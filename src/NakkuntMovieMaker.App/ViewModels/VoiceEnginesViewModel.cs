using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NakkuntMovieMaker.App.Services;
using NakkuntMovieMaker.Voice.AquesTalk;

namespace NakkuntMovieMaker.App.ViewModels;

/// <summary>
/// 音声合成エンジンの一覧と、AquesTalk の導入の案内。AquesTalk は同梱しないので、利用者が A-quest から入手して決まったフォルダに置く。
/// 置き場所は変えられ、変えたらエンジンを読み込み直す
/// </summary>
public sealed partial class VoiceEnginesViewModel : ObservableObject
{
    private readonly EditorSession _session;
    private readonly IDialogService _dialogs;

    public VoiceEnginesViewModel(EditorSession session, IDialogService dialogs)
    {
        _session = session;
        _dialogs = dialogs;
        Refresh();
    }

    public ObservableCollection<EngineChoice> Engines { get; } = [];

    /// <summary>AquesTalk を置くフォルダ</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Layout))]
    public partial string Folder { get; private set; } = "";

    /// <summary>AqKanji2Koe はあるが読み込めなかったときの理由</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConverterError))]
    public partial string? ConverterError { get; private set; }

    public bool HasConverterError => ConverterError is not null;

    /// <summary>フォルダの中の置き方</summary>
    public string Layout =>
        string.Join("\n",
        [
            $"{Path.Combine(Folder, "aquestalk1", AquesTalkOptions.Rid)}　… AquesTalk1（声ごとのフォルダ）",
            $"{Path.Combine(Folder, "aquestalk10", AquesTalkOptions.Rid)}　… AquesTalk10",
            $"{Path.Combine(Folder, "aquestalk2", AquesTalkOptions.Rid)}　… AquesTalk2（phont フォルダに声の素片）",
            $"{Path.Combine(Folder, "aqkanji2koe", AquesTalkOptions.Rid)}　… AqKanji2Koe（aq_dic フォルダに辞書。漢字の読みに使う）",
        ]);

    public static string LicenseNotice =>
        "AquesTalk・AqKanji2Koe は株式会社アクエストの製品で、このソフトには含まれていません。使う場合は、A-quest の公式ページから入手し、" +
        "その使用許諾に従ってください。営利目的で使う（収益化した動画に使うなど）場合は、利用者自身が使用ライセンスを購入する必要があります。";

    public static string OfficialUrl => "https://www.a-quest.com/";

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (await _dialogs.PickFolderAsync("AquesTalk を置いたフォルダ") is { } folder)
        {
            _session.Settings.AquesTalkFolder = folder;
            Reload();
        }
    }

    [RelayCommand]
    private void ResetFolder()
    {
        _session.Settings.AquesTalkFolder = null;
        Reload();
    }

    /// <summary>フォルダに置いたあとで読み込み直す</summary>
    [RelayCommand]
    private void Reload()
    {
        _session.ReloadVoiceEngines();
        Refresh();
    }

    private void Refresh()
    {
        Folder = _session.Settings.AquesTalkFolder is { Length: > 0 } folder ? folder : AquesTalkOptions.Default.Folder;
        ConverterError = _session.VoiceConverterError;
        Engines.Clear();
        foreach (var engine in _session.VoiceEngines.All)
        {
            var status = engine.Status;
            Engines.Add(new EngineChoice(engine.Id, engine.Name, status.Available, status.Message, status.HelpUrl));
        }
    }
}
