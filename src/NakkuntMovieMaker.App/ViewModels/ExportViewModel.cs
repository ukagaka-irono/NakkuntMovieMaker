using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NakkuntMovieMaker.App.Services;
using NakkuntMovieMaker.Engine.Export;
using NakkuntMovieMaker.Engine.Subtitles;
using NakkuntMovieMaker.Media;

namespace NakkuntMovieMaker.App.ViewModels;

/// <summary>書き出す範囲の選択肢</summary>
public enum ExportRange
{
    Whole,
    Selection,
}

/// <summary>ラウドネスの選択肢（null ならそろえない）</summary>
public sealed record LoudnessChoice(double? Target, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// 書き出しダイアログ。プリセット、出力の大きさとコーデックとビットレート、範囲（全体か、タイムラインで指定した範囲）、
/// エンコーダー（ハードウェアを使うか）、ラウドネス、字幕（SRT）。拡張子が .wav なら音声だけを書き出す。
/// 書き出し中は進み具合を出し、キャンセルできる
/// </summary>
public sealed partial class ExportViewModel : ObservableObject
{
    private readonly EditorSession _session;
    private readonly IDialogService _dialogs;
    private CancellationTokenSource? _cancellation;

    public ExportViewModel(EditorSession session, IDialogService dialogs)
    {
        _session = session;
        _dialogs = dialogs;
        var format = session.Scene.Format;
        Preset = Presets.FirstOrDefault(preset => preset.Width == format.Width && preset.Height == format.Height) ?? Presets.Single(preset => preset.Id == ExportPresets.SceneSize.Id);
        var folder = session.EntryPath is { } entry ? Path.GetDirectoryName(entry)! : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        OutputPath = Path.Combine(folder, $"{session.Project.Name}.mp4");
        HasRange = session.RangeStart is not null || session.RangeEnd is not null;
        Range = HasRange ? ExportRange.Selection : ExportRange.Whole;
    }

    public static IReadOnlyList<ExportPreset> Presets => ExportPresets.All;

    public static IReadOnlyList<LoudnessChoice> LoudnessChoices { get; } =
    [
        new(null, "そろえない"),
        new(-14, "-14 LUFS（YouTube など）"),
        new(-16, "-16 LUFS（ポッドキャスト）"),
        new(-23, "-23 LUFS（放送）"),
    ];

    [ObservableProperty]
    public partial ExportPreset Preset { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAudioOnly))]
    public partial string OutputPath { get; set; }

    [ObservableProperty]
    public partial bool Hevc { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeError))]
    public partial decimal Width { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeError))]
    public partial decimal Height { get; set; }

    [ObservableProperty]
    public partial decimal VideoBitrate { get; set; }

    [ObservableProperty]
    public partial decimal AudioBitrate { get; set; }

    /// <summary>ハードウェアエンコーダーを使わない</summary>
    [ObservableProperty]
    public partial bool SoftwareOnly { get; set; }

    [ObservableProperty]
    public partial ExportRange Range { get; set; }

    public bool HasRange { get; }

    public bool WholeScene
    {
        get => Range == ExportRange.Whole;
        set
        {
            if (value)
            {
                Range = ExportRange.Whole;
            }
        }
    }

    public bool SelectedRange
    {
        get => Range == ExportRange.Selection;
        set
        {
            if (value)
            {
                Range = ExportRange.Selection;
            }
        }
    }

    [ObservableProperty]
    public partial LoudnessChoice Loudness { get; set; } = LoudnessChoices[0];

    /// <summary>セリフの字幕を SRT にも書き出す（動画と同じ名前で）</summary>
    [ObservableProperty]
    public partial bool WriteSubtitles { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(CancelCommand))]
    public partial bool IsExporting { get; private set; }

    public bool CanEdit => !IsExporting;

    /// <summary>進み具合（0〜1）</summary>
    [ObservableProperty]
    public partial double Progress { get; private set; }

    [ObservableProperty]
    public partial string Status { get; private set; } = "";

    /// <summary>書き出し終えたファイル（終わっていなければ null）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDone))]
    public partial string? Result { get; private set; }

    public bool IsDone => Result is not null;

    public bool IsAudioOnly => Path.GetExtension(OutputPath).Equals(".wav", StringComparison.OrdinalIgnoreCase);

    /// <summary>この PC で使える映像のエンコーダー（上から順に試す）</summary>
    public string EncoderList
    {
        get
        {
            try
            {
                var codec = Hevc ? VideoCodecKind.Hevc : VideoCodecKind.H264;
                var encoders = MediaWriter.AvailableEncoders(codec, SoftwareOnly ? EncoderPreference.Software : EncoderPreference.Auto);
                return encoders.Count == 0 ? "使えるエンコーダーがありません" : string.Join("、", encoders);
            }
            catch (Exception ex) when (ex is MediaException or DllNotFoundException or InvalidOperationException)
            {
                return $"FFmpeg を読み込めません（{ex.Message}）";
            }
        }
    }

    public string? SizeError => Width % 2 != 0 || Height % 2 != 0 ? "幅と高さは偶数にしてください" : null;

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var kinds = IsAudioOnly ? new[] { FileKinds.Wav, FileKinds.Mp4 } : [FileKinds.Mp4, FileKinds.Wav];
        if (await _dialogs.PickSaveFileAsync("書き出し先", Path.GetFileName(OutputPath), kinds) is { } path)
        {
            OutputPath = path;
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task StartAsync()
    {
        if (SizeError is { } error)
        {
            await _dialogs.ShowErrorAsync("書き出せません", error);
            return;
        }

        if (File.Exists(OutputPath) && !await _dialogs.ConfirmAsync("書き出し", $"{Path.GetFileName(OutputPath)} はすでにあります。上書きしますか？", "上書き"))
        {
            return;
        }

        long? from = null;
        long? to = null;
        if (Range == ExportRange.Selection)
        {
            from = _session.RangeStart;
            to = _session.RangeEnd;
        }

        var path = OutputPath;
        _cancellation = new CancellationTokenSource();
        IsExporting = true;
        Result = null;
        Progress = 0;
        Status = "準備しています…";
        var watch = Stopwatch.StartNew();
        using var pipeline = new ExportPipeline(_session.Project, _session.SceneId, _session.ProjectFolder);
        try
        {
            if (IsAudioOnly)
            {
                await pipeline.ExportWavAsync(path, from, to, Loudness.Target, cancellationToken: _cancellation.Token);
            }
            else
            {
                var options = new ExportOptions(
                    path,
                    Hevc ? VideoCodecKind.Hevc : VideoCodecKind.H264,
                    (int)Width,
                    (int)Height,
                    (int)VideoBitrate,
                    (int)AudioBitrate,
                    from,
                    to,
                    SoftwareOnly ? EncoderPreference.Software : EncoderPreference.Auto,
                    Loudness.Target);
                var progress = new Progress<ExportProgress>(report =>
                {
                    Progress = report.Fraction;
                    var speed = report.Elapsed.TotalSeconds > 0 ? report.Frame / report.Elapsed.TotalSeconds : 0;
                    Status = $"{report.Frame} / {report.TotalFrames} フレーム（{speed:0.0} fps）";
                });
                var result = await pipeline.ExportVideoAsync(options, progress, _cancellation.Token);
                Status = $"エンコーダー {result.VideoEncoder}、";
            }

            if (WriteSubtitles)
            {
                Srt.WriteFile(Path.ChangeExtension(path, ".srt"), SubtitleFiles.Export(_session.Scene, from: from, to: to));
            }

            Progress = 1;
            Status += $"{watch.Elapsed.TotalSeconds:0.0} 秒で書き出しました";
            Result = path;
        }
        catch (OperationCanceledException)
        {
            Status = "書き出しを中止しました";
            Progress = 0;
        }
        catch (Exception ex) when (ex is MediaException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            Status = "";
            await _dialogs.ShowErrorAsync("書き出せません", ex.Message);
        }
        finally
        {
            IsExporting = false;
            _cancellation.Dispose();
            _cancellation = null;
        }
    }

    [RelayCommand(CanExecute = nameof(IsExporting))]
    private void Cancel() => _cancellation?.Cancel();

    partial void OnPresetChanged(ExportPreset value)
    {
        var format = _session.Scene.Format;
        Width = value.Width ?? format.Width + format.Width % 2;
        Height = value.Height ?? format.Height + format.Height % 2;
        Hevc = value.Codec == VideoCodecKind.Hevc;
        VideoBitrate = value.VideoBitrateKbps;
        AudioBitrate = value.AudioBitrateKbps;
    }

    partial void OnHevcChanged(bool value) => OnPropertyChanged(nameof(EncoderList));

    partial void OnSoftwareOnlyChanged(bool value) => OnPropertyChanged(nameof(EncoderList));

    partial void OnRangeChanged(ExportRange value)
    {
        OnPropertyChanged(nameof(WholeScene));
        OnPropertyChanged(nameof(SelectedRange));
    }
}
