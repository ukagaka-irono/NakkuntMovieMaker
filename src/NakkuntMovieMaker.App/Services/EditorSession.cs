using System.Collections.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using NakkuntMovieMaker.ColorManagement;
using NakkuntMovieMaker.Core.Assets;
using NakkuntMovieMaker.Core.Editing;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Serialization;
using NakkuntMovieMaker.Engine;
using NakkuntMovieMaker.Engine.Playback;
using NakkuntMovieMaker.Engine.Tachie;
using NakkuntMovieMaker.Media;
using NakkuntMovieMaker.Rendering;
using NakkuntMovieMaker.Rendering.Color;
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
    public IRenderBackend Backend => _workspace!.Backend;

    /// <summary>描画の方式の名前（「Vulkan（GPU の名前）」・「Skia（CPU）」）。プロジェクトを開いていなければ null</summary>
    public string? RendererName => _workspace?.Backend.Info.Name;

    /// <summary>望んだ描画の方式を使えずに CPU に切り替えた理由（切り替えていなければ null）</summary>
    public string? RendererProblem => _workspace?.RendererProblem;

    /// <summary>描画の方式が変わった（GPU が使えなくなって CPU に切り替えたなど。描画のスレッドから呼ばれることがある）</summary>
    public event EventHandler? RendererChanged;

    /// <summary>プレビューの色（プロジェクトの色の設定や、プレビューの表示の色空間）が変わった。プレビューは描き直す</summary>
    public event EventHandler? PreviewColorChanged;

    /// <summary>設定のプレビューの表示の色空間を変えたあとに呼ぶ（プレビューを新しい変換で描き直す）</summary>
    public void RefreshPreviewColor()
    {
        if (_workspace is null)
        {
            return;
        }

        Frames.SetColor(PreviewColor(Project, _settings));
        PreviewColorChanged?.Invoke(this, EventArgs.Empty);
    }

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
    public void NewProject(string name, SceneFormat format, ProjectColorSettings? color = null)
    {
        var project = Project.CreateNew(name, format) with { Color = color ?? ProjectColorSettings.Default };
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

        // 画面が素材を読む前に（Changed より先に）、素材の読み出しを新しいプロジェクトにする
        Media.Update(change.Current);
        var scene = change.Current.GetScene(SceneId);
        Playback.SetScene(scene);
        if (change.Previous.Color != change.Current.Color)
        {
            Frames.SetColor(PreviewColor(change.Current, _settings));
        }

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

    // プレビューの色：プロジェクトの色の設定と、表示の変換（利用者ごとの設定）
    private static FrameColor PreviewColor(Project project, AppSettings settings) =>
        FrameColor.From(project.Color).WithOutput(PreviewTransform(project, settings));

    // プレビューの表示の変換（利用者ごとの設定）。変換できなければ、変換せずに表示する
    private static ColorLut? PreviewTransform(Project project, AppSettings settings)
    {
        if (settings.PreviewColorSpace is not { } destination || !Ocio.TryLoad(out _))
        {
            return null;
        }

        try
        {
            return ColorTransforms.Shared.Output(project.Color.OcioConfig, destination);
        }
        catch (OcioException)
        {
            return null;
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
            // 設定の描画の方式で作る。GPU の準備の途中で止まったら、次の起動で CPU を使えるように、設定のファイルの隣に目印を置く
            var marker = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(session._settingsPath)) ?? ".", "gpu-starting");
            var settings = session.Settings;
            var options = new RendererOptions(
                RendererOptions.ParsePreference(settings.Renderer) ?? RendererPreference.Auto,
                settings.RendererDevice,
                marker,
                settings.ExperimentalVulkanVideo,
                settings.GpuMemoryLimitMegabytes);
            var selection = RenderBackendFactory.Create(options, layouts);
            Backend = selection.Backend;
            RendererProblem = selection.FallbackReason;
            if (Backend is FallbackBackend fallback)
            {
                // 描いている途中で GPU が使えなくなったら、CPU に切り替えたことを知らせる
                fallback.FellBack += reason =>
                {
                    RendererProblem = reason;
                    session.RendererChanged?.Invoke(session, EventArgs.Empty);
                };
            }

            Measurer = new ContentMeasurer(layouts);
            // プレビューでは、プロジェクトの設定に合う動画にプロキシを使う（書き出しは別の素材の読み出しで、元の素材を読む）
            Proxies = new ProxyQueue(Cache);
            Media = new ProjectMediaSource(project, folder, Cache, proxies: Proxies);
            var timeline = new TimelineContext(project.Characters, new TachieLoader(folder), new ProjectLipSync(Media))
            {
                Color = PreviewColor(project, session.Settings),
            };
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

        public IRenderBackend Backend { get; }

        /// <summary>望んだ描画の方式を使えなかった理由</summary>
        public string? RendererProblem { get; private set; }

        public ContentMeasurer Measurer { get; }

        public ProjectMediaSource Media { get; }

        /// <summary>プレビューで使うプロキシを作る仕事（後ろで 1 つずつ作る）</summary>
        public ProxyQueue Proxies { get; }

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
            Proxies.Dispose();
            Backend.Dispose();
        }
    }
}
