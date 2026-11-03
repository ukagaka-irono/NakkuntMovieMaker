using System.Text.Json;
using Avalonia.Threading;
using NakkuntMovieMaker.Core.Serialization;

namespace NakkuntMovieMaker.App.Services;

/// <summary>
/// 復元できる自動保存。AutosaveEntry は自動保存したプロジェクト、ProjectFolder は素材を探すフォルダ（元のプロジェクトのフォルダ）、
/// OriginalEntry は元のプロジェクトのファイル（保存していないプロジェクトなら null）
/// </summary>
public sealed record RecoveryEntry(string ManifestPath, string AutosaveEntry, string ProjectFolder, string? OriginalEntry, string ProjectName, DateTimeOffset SavedAt);

/// <summary>
/// 自動保存と、異常終了したときの復元。保存していない変更があれば、決まった間隔でプロジェクトのフォルダの .nmm/autosave に書き、
/// 復元の一覧（利用者ごとのフォルダ）に記録する。保存したとき・閉じたとき・別のプロジェクトを開いたときは消す。
/// 起動したときに一覧に残っていれば、前回は正しく終わらなかったので、復元するか聞く
/// </summary>
public sealed class AutosaveService : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly EditorSession _session;
    private readonly string _recoveryFolder;
    private readonly ProjectStore _store = new();
    private readonly DispatcherTimer _timer;
    private string? _manifest;
    private string? _autosaveFolder;

    public AutosaveService(EditorSession session, string recoveryFolder)
    {
        _session = session;
        _recoveryFolder = recoveryFolder;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(Math.Max(1, session.Settings.AutosaveMinutes)) };
        _timer.Tick += (_, _) => SaveNow();
        session.Saved += (_, _) => Clear();
        session.ProjectOpening += (_, _) => Clear();
    }

    /// <summary>利用者ごとの復元の一覧の場所</summary>
    public static string DefaultRecoveryFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NakkuntMovieMaker", "recovery");

    /// <summary>決まった間隔で自動保存を始める（設定で 0 分なら何もしない）</summary>
    public void Start()
    {
        if (_session.Settings.AutosaveMinutes > 0)
        {
            _timer.Interval = TimeSpan.FromMinutes(_session.Settings.AutosaveMinutes);
            _timer.Start();
        }
    }

    /// <summary>保存していない変更があれば、今すぐ自動保存する。保存したら true</summary>
    public bool SaveNow()
    {
        if (_session.IsClosed || !_session.Document.IsModified)
        {
            return false;
        }

        try
        {
            var folder = Path.Combine(_session.ProjectFolder, ".nmm", "autosave");
            var entry = Path.Combine(folder, "autosave" + ProjectStore.EntryExtension);
            _store.Save(_session.Project, entry);
            _autosaveFolder = folder;

            Directory.CreateDirectory(_recoveryFolder);
            _manifest ??= Path.Combine(_recoveryFolder, Guid.NewGuid().ToString("N") + ".json");
            var record = new Manifest(entry, _session.ProjectFolder, _session.EntryPath, _session.Project.Name, DateTimeOffset.Now);
            var temporary = _manifest + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(record, Json));
            File.Move(temporary, _manifest, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 自動保存できなくても編集は続ける
            return false;
        }
    }

    /// <summary>今のプロジェクトの自動保存と、復元の記録を消す</summary>
    public void Clear()
    {
        TryDelete(() => File.Delete(_manifest ?? ""));
        TryDelete(() =>
        {
            if (_autosaveFolder is not null && Directory.Exists(_autosaveFolder))
            {
                Directory.Delete(_autosaveFolder, recursive: true);
            }
        });
        _manifest = null;
        _autosaveFolder = null;
    }

    /// <summary>復元できる自動保存（新しい順）。読めない記録は消す</summary>
    public static IReadOnlyList<RecoveryEntry> Find(string recoveryFolder)
    {
        if (!Directory.Exists(recoveryFolder))
        {
            return [];
        }

        var entries = new List<RecoveryEntry>();
        foreach (var path in Directory.EnumerateFiles(recoveryFolder, "*.json"))
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), Json);
                if (manifest is not null && File.Exists(manifest.AutosaveEntry))
                {
                    entries.Add(new RecoveryEntry(path, manifest.AutosaveEntry, manifest.ProjectFolder, manifest.OriginalEntry, manifest.ProjectName, manifest.SavedAt));
                    continue;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
            }

            TryDelete(() => File.Delete(path));
        }

        return [.. entries.OrderByDescending(entry => entry.SavedAt)];
    }

    /// <summary>
    /// 復元しない自動保存を消す。保存していないプロジェクトの作業フォルダ（一時フォルダ）も消す
    /// </summary>
    public static void Discard(RecoveryEntry entry)
    {
        TryDelete(() => File.Delete(entry.ManifestPath));
        TryDelete(() =>
        {
            var folder = Path.GetDirectoryName(entry.AutosaveEntry)!;
            var target = entry.OriginalEntry is null ? entry.ProjectFolder : folder;
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
        });
    }

    /// <summary>復元したあと、その記録を引き継ぐ（次の自動保存は同じ記録を書き換え、保存したら消す）</summary>
    public void Adopt(RecoveryEntry entry)
    {
        _manifest = entry.ManifestPath;
        _autosaveFolder = Path.GetDirectoryName(entry.AutosaveEntry);
    }

    public void Dispose()
    {
        _timer.Stop();
        Clear();
    }

    private static void TryDelete(Action delete)
    {
        try
        {
            delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
    }

    private sealed record Manifest(string AutosaveEntry, string ProjectFolder, string? OriginalEntry, string ProjectName, DateTimeOffset SavedAt);
}
