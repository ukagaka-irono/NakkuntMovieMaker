using NakkuntMovieMaker.App.Services;
using NakkuntMovieMaker.App.ViewModels;
using NakkuntMovieMaker.Core.Editing;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Engine.Export;
using NakkuntMovieMaker.Media;

namespace NakkuntMovieMaker.App.Tests;

/// <summary>書き出しダイアログ：プリセット・範囲・音声だけ・字幕・上書きの確認</summary>
public sealed class ExportDialogTests : IDisposable
{
    private readonly TestEnvironment _environment = new();
    private readonly FakeDialogs _dialogs = new();

    public void Dispose() => _environment.Dispose();

    // 160×90・30fps・2 秒（図形）と、0.5〜1 秒のセリフ
    private EditorSession CreateSession()
    {
        var session = _environment.CreateSession();
        session.Execute(EditCommands.ChangeSceneSettings(session.Project, session.SceneId, "メイン", new SceneFormat(160, 90, 30, 48000)));
        var shape = new ShapeItem(ItemId.New(), session.Scene.Layers[0].Id, 0, 60, new ShapeStyle(ShapeKind.Rectangle, 160, 90, new SolidPaint(RgbaColor.White)));
        var character = new Character(CharacterId.New(), "霊夢", new VoiceSpec("test", "a", VoiceParams.Default), TextStyle.Default);
        session.Execute(new SetCharacter(character.Id, character));
        session.Execute(EditCommands.AddItem(session.Project, session.SceneId, shape));
        session.Execute(EditCommands.AddItem(session.Project, session.SceneId, new VoiceItem(ItemId.New(), session.Scene.Layers[1].Id, 15, 15, character.Id, "こんにちは")));
        return session;
    }

    [Fact]
    public Task ExportsTheWholeSceneWithSubtitles() => Ui.Run(async () =>
    {
        Fixtures.RequireFfmpeg();
        using var session = CreateSession();
        var export = new ExportViewModel(session, _dialogs);

        Assert.Equal(ExportPresets.SceneSize.Id, export.Preset.Id);
        Assert.Equal((160m, 90m), (export.Width, export.Height));
        Assert.False(export.HasRange);
        Assert.Contains("libopenh264", export.EncoderList);
        export.OutputPath = Path.Combine(_environment.Folder, "out", "動画.mp4");
        export.SoftwareOnly = true;
        Assert.Equal("libopenh264", export.EncoderList);
        export.WriteSubtitles = true;

        await export.StartCommand.ExecuteAsync(null);

        Assert.Equal(export.OutputPath, export.Result);
        Assert.Equal(1, export.Progress);
        Assert.Contains("libopenh264", export.Status);
        var video = MediaProbe.Probe(export.OutputPath).Video!;
        Assert.Equal((160, 90, 60L), (video.Width, video.Height, video.FrameCount));
        Assert.Contains("00:00:00,500 --> 00:00:01,000", await File.ReadAllTextAsync(Path.Combine(_environment.Folder, "out", "動画.srt"), TestContext.Current.CancellationToken));
    });

    // PATH にある ffmpeg（なければ null）
    internal static string? FindFfmpegOnPath() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => Path.Combine(folder, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"))
            .FirstOrDefault(File.Exists);

    [Fact]
    public Task ExportsWithTheExternalFfmpeg() => Ui.Run(async () =>
    {
        Fixtures.RequireFfmpeg();
        var ffmpeg = FindFfmpegOnPath();
        Assert.SkipWhen(ffmpeg is null || !ExternalFfmpeg.Probe(ffmpeg).Supports(VideoCodecKind.H264), "PATH に x264 を含む ffmpeg がないためスキップします");
        using var session = CreateSession();

        // 設定で指定していなければ選べない
        Assert.False(new ExportViewModel(session, _dialogs).HasExternalFfmpeg);

        session.Settings.ExternalFfmpegPath = ffmpeg;
        var export = new ExportViewModel(session, _dialogs) { OutputPath = Path.Combine(_environment.Folder, "x264.mp4") };
        Assert.True(export.HasExternalFfmpeg);
        export.UseExternalFfmpeg = true;
        await export.ExternalFfmpegCheck;
        Assert.StartsWith("libx264（ffmpeg ", export.EncoderList);

        await export.StartCommand.ExecuteAsync(null);

        Assert.Empty(_dialogs.Errors);
        Assert.Contains("libx264", export.Status);
        var video = MediaProbe.Probe(export.OutputPath).Video!;
        Assert.Equal(("h264", 60L), (video.Codec, video.FrameCount));
    });

    [Fact]
    public Task ReportsAMissingExternalFfmpeg() => Ui.Run(async () =>
    {
        using var session = CreateSession();
        session.Settings.ExternalFfmpegPath = Path.Combine(_environment.Folder, "ない", "ffmpeg.exe");
        var export = new ExportViewModel(session, _dialogs) { OutputPath = Path.Combine(_environment.Folder, "x264.mp4"), UseExternalFfmpeg = true };

        await export.ExternalFfmpegCheck;
        Assert.Contains("実行できません", export.EncoderList);
        await export.StartCommand.ExecuteAsync(null);

        Assert.Contains("実行できません", Assert.Single(_dialogs.Errors));
        Assert.False(File.Exists(export.OutputPath));
    });

    [Fact]
    public Task ExportsTheSelectedRangeWithAPreset() => Ui.Run(async () =>
    {
        Fixtures.RequireFfmpeg();
        using var session = CreateSession();
        session.RangeStart = 10;
        session.RangeEnd = 40;
        var export = new ExportViewModel(session, _dialogs);
        Assert.True(export.SelectedRange);

        export.Preset = ExportPresets.All.Single(preset => preset.Id == ExportPresets.Light720p.Id);
        Assert.Equal((1280m, 720m, 5000m, 128m), (export.Width, export.Height, export.VideoBitrate, export.AudioBitrate));
        export.Width = 128;
        export.Height = 72;
        export.SoftwareOnly = true;
        export.OutputPath = Path.Combine(_environment.Folder, "range.mp4");
        await export.StartCommand.ExecuteAsync(null);

        var video = MediaProbe.Probe(export.OutputPath).Video!;
        Assert.Equal((128, 72, 30L), (video.Width, video.Height, video.FrameCount));

        // 奇数の大きさは書き出さない
        export.Width = 127;
        Assert.NotNull(export.SizeError);
        await export.StartCommand.ExecuteAsync(null);
        Assert.Single(_dialogs.Errors);
    });

    [Fact]
    public Task ExportsAudioOnlyAndAsksBeforeOverwriting() => Ui.Run(async () =>
    {
        Fixtures.RequireFfmpeg();
        using var session = CreateSession();
        var export = new ExportViewModel(session, _dialogs) { OutputPath = Path.Combine(_environment.Folder, "音声.wav") };
        Assert.True(export.IsAudioOnly);

        await export.StartCommand.ExecuteAsync(null);
        var wav = Audio.WavReader.Read(export.OutputPath);
        Assert.Equal((2, 96000L), (wav.Channels, wav.FrameCount));

        // 上書きを断ると書き出さない
        File.SetLastWriteTimeUtc(export.OutputPath, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        _dialogs.Confirmations.Enqueue(false);
        await export.StartCommand.ExecuteAsync(null);
        Assert.Equal(2000, File.GetLastWriteTimeUtc(export.OutputPath).Year);
    });
}
