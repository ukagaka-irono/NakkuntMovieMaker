using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NakkuntMovieMaker.App.Services;
using NakkuntMovieMaker.Audio;
using NakkuntMovieMaker.Core.Editing;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;
using NakkuntMovieMaker.Engine;
using NakkuntMovieMaker.Engine.Playback;

namespace NakkuntMovieMaker.App.ViewModels;

/// <summary>入力デバイスの選択肢（Name が null なら既定のデバイス）</summary>
public sealed record InputDeviceChoice(string? Name, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// 録音。タイムラインへの録音（再生位置から。再生しながら録ることもできる）と、セリフごとの収録（選んだセリフの声を録り、次のセリフへ進む）。
/// 入力デバイスの選択、入力レベル、入出力の遅れの測定。録った音はプロジェクトの recordings フォルダに WAV で残す
/// </summary>
public sealed partial class RecordingViewModel : ObservableObject, IDisposable
{
    private const int SampleRate = 48000;

    private readonly EditorSession _session;
    private readonly IDialogService _dialogs;
    private readonly PreviewViewModel _preview;
    private readonly Func<string?, IAudioInput> _openInput;
    private readonly Stopwatch _elapsed = new();
    private readonly DispatcherTimer _timer;
    private Recorder? _recorder;
    private IAudioInput? _input;
    private ItemId? _line;
    private long _startFrame;

    /// <param name="openInput">入力デバイスを開く（テストでは差し替える）。null なら miniaudio で開く</param>
    public RecordingViewModel(EditorSession session, IDialogService dialogs, PreviewViewModel preview, Func<string?, IAudioInput>? openInput = null)
    {
        _session = session;
        _dialogs = dialogs;
        _preview = preview;
        _openInput = openInput ?? (device => AudioInput.Open(SampleRate, 1, SampleRate * 4, deviceName: device));
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) => UpdateLevel());
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(EditorSession.Selection))
            {
                OnPropertyChanged(nameof(LineLabel));
                RecordLineCommand.NotifyCanExecuteChanged();
            }
        };
        RefreshDevices();
    }

    public ObservableCollection<InputDeviceChoice> Devices { get; } = [];

    [ObservableProperty]
    public partial InputDeviceChoice? Device { get; set; }

    /// <summary>再生しながら録る（ほかの音を聴きながら合わせる）</summary>
    [ObservableProperty]
    public partial bool RecordWhilePlaying { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RecordLineCommand), nameof(RetakeCommand))]
    public partial bool IsRecording { get; private set; }

    [ObservableProperty]
    public partial StereoLevel Level { get; private set; } = StereoLevel.Silent;

    [ObservableProperty]
    public partial string Elapsed { get; private set; } = "0:00.0";

    [ObservableProperty]
    public partial string Status { get; private set; } = "";

    /// <summary>最後に録ったセリフ（録り直し用）</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetakeCommand))]
    public partial ItemId? LastLine { get; private set; }

    /// <summary>入出力の遅れの補正（ミリ秒）</summary>
    public string LatencyText => _session.Settings.RecordingLatencyMilliseconds is { } ms ? $"遅れの補正：{ms:0} ms" : "遅れの補正：未測定";

    /// <summary>セリフごとの収録で録るセリフ</summary>
    public string LineLabel => SelectedLine() is { } line ? $"「{line.Text}」" : "（セリフを選んでください）";

    [RelayCommand]
    private void RefreshDevices()
    {
        Devices.Clear();
        Devices.Add(new InputDeviceChoice(null, "既定のデバイス"));
        try
        {
            foreach (var device in AudioDevices.List(capture: true))
            {
                Devices.Add(new InputDeviceChoice(device.Name, device.Name));
            }
        }
        catch (Exception ex) when (ex is AudioDeviceException or DllNotFoundException or EntryPointNotFoundException)
        {
            Status = $"入力デバイスを調べられません：{ex.Message}";
        }

        Device = Devices.FirstOrDefault(choice => choice.Name == _session.Settings.InputDevice) ?? Devices[0];
    }

    /// <summary>タイムラインへの録音を始める・止める（Ctrl+R）</summary>
    [RelayCommand]
    private async Task ToggleRecordAsync()
    {
        if (IsRecording)
        {
            await StopAsync();
        }
        else
        {
            await StartAsync(null);
        }
    }

    /// <summary>選んだセリフの声を録る（もう一度押すと止めて、次のセリフを選ぶ）</summary>
    [RelayCommand(CanExecute = nameof(CanRecordLine))]
    private async Task RecordLineAsync()
    {
        if (IsRecording)
        {
            await StopAsync();
        }
        else if (SelectedLine() is { } line)
        {
            _session.Seek(line.Start);
            await StartAsync(line.Id);
        }
    }

    /// <summary>最後に録ったセリフを録り直す</summary>
    [RelayCommand(CanExecute = nameof(CanRetake))]
    private async Task RetakeAsync()
    {
        if (LastLine is { } id && _session.Scene.Items.TryGetValue(id, out var line))
        {
            _session.Selection = [id];
            _session.Seek(line.Start);
            await StartAsync(id);
        }
    }

    /// <summary>入出力の遅れを測る（出力からクリックを鳴らし、入力で録る）</summary>
    [RelayCommand]
    private async Task MeasureLatencyAsync()
    {
        if (!await _dialogs.ConfirmAsync(
                "入出力の遅れを測る",
                "スピーカーからクリック音を 2 秒ほど鳴らし、マイクで録って遅れを測ります。マイクに音が届くようにして（ヘッドホンなら、マイクの近くに置いて）、測るを押してください",
                "測る"))
        {
            return;
        }

        Status = "測っています…";
        var output = _session.Settings.OutputDevice;
        var input = Device?.Name;
        try
        {
            var frames = await Task.Run(() => LatencyCalibration.Measure(output, input));
            if (frames is { } value)
            {
                _session.Settings.RecordingLatencyMilliseconds = value * 1000.0 / SampleRate;
                Status = "測りました";
            }
            else
            {
                Status = "クリック音を録れませんでした。音量を上げて、もう一度測ってください";
            }
        }
        catch (AudioDeviceException ex)
        {
            Status = ex.Message;
        }

        OnPropertyChanged(nameof(LatencyText));
    }

    public void Dispose()
    {
        _timer.Stop();
        _recorder?.Dispose();
        _input?.Dispose();
    }

    partial void OnDeviceChanged(InputDeviceChoice? value) => _session.Settings.InputDevice = value?.Name;

    private bool CanRecordLine() => IsRecording ? _line is not null : SelectedLine() is not null;

    private bool CanRetake() => !IsRecording && LastLine is { } id && _session.Scene.Items.ContainsKey(id);

    private VoiceItem? SelectedLine() => _session.SelectedItems.OfType<VoiceItem>().FirstOrDefault();

    private async Task StartAsync(ItemId? line)
    {
        try
        {
            _input = _openInput(Device?.Name);
        }
        catch (Exception ex) when (ex is AudioDeviceException or DllNotFoundException or EntryPointNotFoundException)
        {
            await _dialogs.ShowErrorAsync("録音を始められません", ex.Message);
            return;
        }

        var path = Recording.NewPath(_session.ProjectFolder, DateTimeOffset.Now);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var format = _session.Scene.Format;
        _startFrame = _session.CurrentFrame;
        var startSample = TimeMath.FrameToSample(_startFrame, format.Fps, format.SampleRate);
        var latency = (int)Math.Round((_session.Settings.RecordingLatencyMilliseconds ?? 0) * SampleRate / 1000);
        Func<long>? clock = null;
        if (RecordWhilePlaying && line is null)
        {
            // 再生に合わせて録る：鳴っている位置から、録った音を置く位置を見積もる
            if (!_preview.IsPlaying)
            {
                _preview.TogglePlayCommand.Execute(null);
            }

            clock = () => _session.Playback.CurrentSample;
        }

        _recorder = Recorder.Start(_input, path, startSample, clock, latency);
        _line = line;
        _elapsed.Restart();
        _timer.Start();
        IsRecording = true;
        Status = line is null ? "タイムラインに録音しています" : "セリフを録音しています";
    }

    private async Task StopAsync()
    {
        if (_recorder is not { } recorder)
        {
            return;
        }

        _timer.Stop();
        if (_preview.IsPlaying)
        {
            _preview.StopCommand.Execute(null);
        }

        RecordingResult result;
        try
        {
            result = recorder.Stop();
        }
        catch (IOException ex)
        {
            await _dialogs.ShowErrorAsync("録音を保存できません", ex.Message);
            return;
        }
        finally
        {
            recorder.Dispose();
            _input?.Dispose();
            _recorder = null;
            _input = null;
            IsRecording = false;
            Level = StereoLevel.Silent;
        }

        if (result.Frames == 0)
        {
            Status = "音が録れませんでした";
            File.Delete(result.Path);
            return;
        }

        if (_line is { } line)
        {
            ApplyLine(line, result);
        }
        else
        {
            AddToTimeline(result);
        }

        if (result.OverrunFrames > 0)
        {
            Status += $"（{result.OverrunFrames * 1000 / result.SampleRate} ミリ秒ぶん取りこぼしました）";
        }
    }

    // セリフの声を録った音にして、次のセリフを選ぶ
    private async void ApplyLine(ItemId line, RecordingResult result)
    {
        if (!_session.Scene.Items.ContainsKey(line))
        {
            return;
        }

        try
        {
            if (_session.Execute(VoiceEditing.UseRecording(_session.Project, _session.SceneId, line, result, _session.ProjectFolder)) is { } error)
            {
                await _dialogs.ShowErrorAsync("録音を使えません", error);
                return;
            }
        }
        catch (EditException ex)
        {
            await _dialogs.ShowErrorAsync("録音を使えません", ex.Message);
            return;
        }

        LastLine = line;
        var current = _session.Scene.GetItem(line);
        var next = _session.Scene.Items.Values.OfType<VoiceItem>()
            .Where(item => item.Start > current.Start || (item.Start == current.Start && _session.Scene.LayerIndex(item.Layer) > _session.Scene.LayerIndex(current.Layer)))
            .OrderBy(item => item.Start)
            .ThenBy(item => _session.Scene.LayerIndex(item.Layer))
            .FirstOrDefault();
        if (next is not null)
        {
            _session.Selection = [next.Id];
            _session.Seek(next.Start);
            Status = "録りました。次のセリフを選びました";
        }
        else
        {
            Status = "録りました。最後のセリフです";
        }
    }

    // 録った音を、録り始めた位置の空いているレイヤーに置く
    private async void AddToTimeline(RecordingResult result)
    {
        var project = _session.Project;
        var sceneId = _session.SceneId;
        var (start, length, _) = Recording.Timing(result, project.GetScene(sceneId).Format);
        var preferred = _session.SelectedItems.FirstOrDefault() is { } selected ? project.GetScene(sceneId).LayerIndex(selected.Layer) : 0;
        var (layer, addLayer) = MediaItems.FindFreeLayer(project, sceneId, start, length, preferred);
        var operations = new List<IOperation>();
        if (addLayer is not null)
        {
            operations.Add(addLayer);
            project = addLayer.Apply(project).Project;
        }

        var (operation, item) = Recording.AddToTimeline(project, sceneId, layer, result, _session.ProjectFolder);
        operations.Add(operation);
        if (_session.Execute(Operations.Combine("録音", operations)) is { } error)
        {
            await _dialogs.ShowErrorAsync("録音を置けません", error);
            return;
        }

        _session.Selection = [item];
        Status = $"録りました（{(double)result.Frames / result.SampleRate:0.0} 秒）";
    }

    private void UpdateLevel()
    {
        if (_recorder is not { } recorder)
        {
            return;
        }

        var level = recorder.Level;
        Level = new StereoLevel(level, level);
        Elapsed = TimeSpan.FromSeconds((double)recorder.Frames / SampleRate).ToString(@"m\:ss\.f");
    }
}
