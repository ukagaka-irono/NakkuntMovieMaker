using System.Collections.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using NakkuntMovieMaker.Core.Assets;
using NakkuntMovieMaker.Core.Editing;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Serialization;
using NakkuntMovieMaker.Engine;
using NakkuntMovieMaker.Engine.Playback;
using NakkuntMovieMaker.Engine.Tachie;
using NakkuntMovieMaker.Media;
using NakkuntMovieMaker.Rendering.Skia;
using NakkuntMovieMaker.Text;
using NakkuntMovieMaker.Voice;

namespace NakkuntMovieMaker.App.Services;

/// <summary>
/// 開いているプロジェクトと、編集の状態（再生位置・選択・範囲）。画面の各部はこれを共有する。
/// 保存していない新しいプロジェクトは、一時フォルダを作業フォルダにして、保存するときに声や録音を保存先へ移す。
/// 編集は UI のスレッドで行う
/// </summary>
public sealed partial class EditorSession : ObservableObject, IDisposable
{
    private readonly AppSettings _settings;
    private readonly string _settingsPath;
    private readonly Func<VoiceEngineRegistry>? _voiceEngines;
    private readonly ProjectStore _store = new();
    private Workspace? _workspace;
    private string? _untitledFolder;

    /// <param name="settingsPath">閉じるときに設定を書く場所（省略すると利用者の設定ファイル）</param>
    /// <param name="voiceEngines">音声合成エンジンの一覧を作る（テスト用。省略すると組み込みのエンジン）</param>
    /// <param name="openPath">最初に開くプロジェクト（開けなければ新しいプロジェクトにする）</param>
    public EditorSession(
        AppSettings settings, bool disableAudioDevice = false, string? settingsPath = null, Func<VoiceEngineRegistry>? voiceEngines = null, string? openPath = null)
    {
        _settings = settings;
        _voiceEngines = voiceEngines;
        _settingsPath = settingsPath ?? AppSettings.DefaultPath;
        DisableAudioDevice = disableAudioDevice;
        if (openPath is not null && TryLoad(openPath) is { } opened)
        {
            Document = new ProjectDocument(opened);
            EntryPath = Path.GetFullPath(openPath);
            ProjectFolder = Path.GetDirectoryName(EntryPath)!;
            settings.AddRecent(EntryPath);
        }
        else
        {
            Document = new ProjectDocument(Project.CreateNew("新しいプロジェクト", SceneFormat.Landscape1080p30));
            ProjectFolder = CreateUntitledFolder();
            _untitledFolder = ProjectFolder;
        }

        SceneId = Document.Current.RootScene;
        Attach();
    }

    /// <summary>別のプロジェクトを開いた（または新しく作った）。画面は表示を作り直す</summary>
    public event EventHandler? ProjectOpened;

    /// <summary>別のプロジェクトに替わる直前（今のプロジェクトの後始末に使う）</summary>
    public event EventHandler? ProjectOpening;

    /// <summary>プロジェクトを保存した</summary>
    public event EventHandler? Saved;

    /// <summary>プロジェクトが編集された（元に戻す・やり直しを含む）</summary>
    public event EventHandler<DocumentChange>? Changed;

    /// <summary>音のデバイスを使わない（テスト用）</summary>
    public bool DisableAudioDevice { get; }

    public AppSettings Settings => _settings;

    /// <summary>設定ファイルの場所</summary>
    public string SettingsPath => _settingsPath;

    public ProjectDocument Document { get; private set; }

    public Project Project => Document.Current;

    public SceneId SceneId { get; private set; }

    public Scene Scene => Project.GetScene(SceneId);

    /// <summary>プロジェクトのフォルダ（保存していなければ一時フォルダ）</summary>
    public string ProjectFolder { get; private set; }

    /// <summary>プロジェクトのファイル（.nmmproj）。保存していなければ null</summary>
    public string? EntryPath { get; private set; }

    public bool IsUntitled => EntryPath is null;

    /// <summary>閉じた（Dispose した）後なら true</summary>
    public bool IsClosed => _workspace is null;

    public ProjectMediaSource Media => _workspace!.Media;

    public MediaCache Cache => _workspace!.Cache;

    public FrameScheduler Frames => _workspace!.Frames;

    /// <summary>プレビューの描画（素材の大きさを測るのにも使う）</summary>
    public SkiaCpuBackend Backend => _workspace!.Backend;

    /// <summary>プレビュー上でアイテムの大きさを測る（描画と文字の配置のキャッシュを共有する）</summary>
    public ContentMeasurer Measurer => _workspace!.Measurer;

    public PlaybackController Playback => _workspace!.Playback;

    public VoiceService Voices => _workspace!.Voices;

    public VoiceEngineRegistry VoiceEngines => _workspace!.Engines;

    /// <summary>再生ヘッドの位置（フレーム）</summary>
    [ObservableProperty]
    public partial long CurrentFrame { get; set; }

    /// <summary>選んでいるアイテム</summary>
    [ObservableProperty]
    public partial ImmutableHashSet<ItemId> Selection { get; set; } = [];

    /// <summary>範囲の始まり（I キー）。指定していなければ null</summary>
    [ObservableProperty]
    public partial long? RangeStart { get; set; }

    /// <summary>範囲の終わり（O キー。このフレームは含めない）</summary>
    [ObservableProperty]
    public partial long? RangeEnd { get; set; }

    /// <summary>ウィンドウのタイトル</summary>
    public string Title => $"{(Document.IsModified ? "● " : "")}{Project.Name}{(IsUntitled ? "（未保存）" : "")} - NakkuntMovieMaker";

    /// <summary>選んでいるアイテムのうち、まだシーンにあるもの</summary>
    public IReadOnlyList<Item> SelectedItems => [.. Selection.Where(Scene.Items.ContainsKey).Select(Scene.GetItem)];

    /// <summary>新しいプロジェクトにする（保存はしない）</summary>
    public void NewProject(string name, SceneFormat format)
    {
        var project = Project.CreateNew(name, format);
        Replace(new ProjectDocument(project), CreateUntitledFolder(), null);
    }

    /// <summary>プロジェクトを開く</summary>
    /// <exception cref="ProjectFormatException">プロジェクトとして読めない</exception>
    public void Open(string entryPath)
    {
        var full = Path.GetFullPath(entryPath);
        var project = _store.Load(full);
        Replace(new ProjectDocument(project), Path.GetDirectoryName(full)!, full);
        _settings.AddRecent(full);
    }

    /// <summary>今の場所に保存する。まだ保存していなければ何もしないで false を返す（名前を付けて保存する）</summary>
    public bool Save()
    {
        if (EntryPath is null)
        {
            return false;
        }

        _store.Save(Project, EntryPath);
        Document.MarkSaved();
        OnPropertyChanged(nameof(Title));
        Saved?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// 名前を付けて保存する。フォルダが変わるときは、プロジェクトのフォルダの中にあるもの（声・録音・相対パスの素材と立ち絵）を
    /// 新しいフォルダに写す。外にある素材は絶対パスのまま
    /// </summary>
    public void SaveAs(string entryPath)
    {
        var full = Path.GetFullPath(entryPath);
        var folder = Path.GetDirectoryName(full)!;
        var moved = !string.Equals(Path.GetFullPath(folder), Path.GetFullPath(ProjectFolder), StringComparison.OrdinalIgnoreCase);
        if (moved)
        {
            CopyProjectFiles(ProjectFolder, folder);
        }

        var name = Path.GetFileNameWithoutExtension(full);
        if (name != Project.Name)
        {
            Document.Execute(new RenameProject(name));
        }

        _store.Save(Project, full);
        Document.MarkSaved();
        EntryPath = full;
        _settings.AddRecent(full);
        if (moved)
        {
            var old = _untitledFolder;
            ProjectFolder = folder;
            _untitledFolder = null;
            Rebuild();
            DeleteUntitled(old);
        }

        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(IsUntitled));
        Saved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 自動保存から復元する。素材と声は元のプロジェクトのフォルダ（保存していなかったプロジェクトなら作業フォルダ）から読み、
    /// 保存先は元のプロジェクトのファイル（なければ未保存）にする。復元した内容は保存していない変更として扱う
    /// </summary>
    /// <exception cref="ProjectFormatException">プロジェクトとして読めない</exception>
    public void OpenRecovered(string autosaveEntry, string projectFolder, string? originalEntry)
    {
        var project = _store.Load(autosaveEntry);
        var document = new ProjectDocument(project);
        document.MarkUnsaved();
        Replace(document, projectFolder, originalEntry);
    }

    /// <summary>操作を適用する。適用できなければ、その理由を返す</summary>
    public string? Execute(IOperation operation)
    {
        try
        {
            Document.Execute(operation);
            return null;
        }
        catch (EditException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>編集の操作を作って適用する。作れない・適用できなければ、その理由を返す</summary>
    public string? Execute(Func<Project, SceneId, IOperation> build)
    {
        try
        {
            Document.Execute(build(Project, SceneId));
            return null;
        }
        catch (EditException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>再生位置を動かす（0 より前には行かない）。再生中なら再生もその位置から続ける</summary>
    public void Seek(long frame)
    {
        frame = Math.Max(0, frame);
        CurrentFrame = frame;
        Playback.Seek(frame);
    }

    public void Dispose()
    {
        _workspace?.Dispose();
        _workspace = null;
        DeleteUntitled(_untitledFolder);
        _settings.Save(_settingsPath);
    }

    private Project? TryLoad(string path)
    {
        try
        {
            return _store.Load(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ProjectFormatException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Replace(ProjectDocument document, string folder, string? entryPath)
    {
        ProjectOpening?.Invoke(this, EventArgs.Empty);
        var previousUntitled = _untitledFolder;
        Document.Changed -= OnDocumentChanged;
        Document = document;
        ProjectFolder = folder;
        EntryPath = entryPath;
        // 保存していないプロジェクトは、フォルダが作業フォルダ（閉じたら消す）
        _untitledFolder = entryPath is null ? folder : null;

        SceneId = document.Current.RootScene;
        Selection = [];
        RangeStart = null;
        RangeEnd = null;
        CurrentFrame = 0;
        Attach();
        if (previousUntitled is not null && previousUntitled != _untitledFolder)
        {
            DeleteUntitled(previousUntitled);
        }

        ProjectOpened?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(IsUntitled));
    }

    private void Attach()
    {
        Document.Changed += OnDocumentChanged;
        Rebuild();
    }

    // 素材・描画・再生・音声合成を、今のフォルダとプロジェクトで作り直す
    private void Rebuild()
    {
        _workspace?.Dispose();
        _workspace = new Workspace(this);
    }

    private void OnDocumentChanged(object? sender, DocumentChange change)
    {
        if (!change.Current.Scenes.ContainsKey(SceneId))
        {
            SceneId = change.Current.RootScene;
        }

        var scene = change.Current.GetScene(SceneId);
        Playback.SetScene(scene);
        Selection = Selection.Where(scene.Items.ContainsKey).ToImmutableHashSet();
        Changed?.Invoke(this, change);
        OnPropertyChanged(nameof(Title));
    }

    private string CreateUntitledFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "NakkuntMovieMaker", "untitled", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void DeleteUntitled(string? folder)
    {
        if (folder is null)
        {
            return;
        }

        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // フォルダの中にあるもの（声・録音・相対パスの素材と立ち絵）を写す
    private void CopyProjectFiles(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var directory in new[] { "voices", "recordings" })
        {
            CopyDirectory(Path.Combine(from, directory), Path.Combine(to, directory));
        }

        var relative = Project.Assets.Values.Select(asset => asset.Path)
            .Concat(Project.Characters.Values.Select(character => character.Tachie?.Path).OfType<string>())
            .Where(path => !Path.IsPathRooted(path.Replace('/', Path.DirectorySeparatorChar)));
        foreach (var path in relative)
        {
            var source = Path.GetFullPath(Path.Combine(from, path));
            var target = Path.GetFullPath(Path.Combine(to, path));
            if (File.Exists(source) && !File.Exists(target))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target);
            }
            else if (Directory.Exists(source))
            {
                CopyDirectory(source, target);
            }
        }
    }

    private static void CopyDirectory(string from, string to)
    {
        if (!Directory.Exists(from))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            if (!File.Exists(target))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
        }
    }

    // 1 つのプロジェクトのフォルダに結びついた、素材・描画・再生・音声合成
    private sealed class Workspace : IDisposable
    {
        private readonly IDisposable _connection;

        public Workspace(EditorSession session)
        {
            var project = session.Project;
            var folder = session.ProjectFolder;
            Cache = new MediaCache(Path.Combine(folder, ".nmm", "cache"));
            var layouts = new TextLayoutCache();
            Backend = new SkiaCpuBackend(layouts: layouts);
            Measurer = new ContentMeasurer(layouts);
            Media = new ProjectMediaSource(project, folder, Cache);
            var timeline = new TimelineContext(project.Characters, new TachieLoader(folder), new ProjectLipSync(Media));
            Frames = new FrameScheduler(session.Scene, Backend, Media, timeline: timeline) { Scale = session.Settings.PreviewScale };
            _connection = Frames.Connect(session.Document, session.SceneId);
            Playback = new PlaybackController(
                session.Scene,
                Media,
                Frames,
                new PlaybackOptions(DisableAudioDevice: session.DisableAudioDevice, OutputDevice: session.Settings.OutputDevice));
            Engines = session._voiceEngines?.Invoke() ?? Engine.VoiceEngines.CreateDefault();
            Voices = new VoiceService(Engines, new VoiceCache(folder));
        }

        public MediaCache Cache { get; }

        public SkiaCpuBackend Backend { get; }

        public ContentMeasurer Measurer { get; }

        public ProjectMediaSource Media { get; }

        public FrameScheduler Frames { get; }

        public PlaybackController Playback { get; }

        public VoiceEngineRegistry Engines { get; }

        public VoiceService Voices { get; }

        public void Dispose()
        {
            _connection.Dispose();
            Playback.Dispose();
            Frames.Dispose();
            Voices.Dispose();
            Engines.Dispose();
            Media.Dispose();
        }
    }
}
