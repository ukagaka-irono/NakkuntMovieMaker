using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NakkuntMovieMaker.App.Services;

namespace NakkuntMovieMaker.App.ViewModels;

/// <summary>音声合成エンジンの一覧（使えるかどうかと理由）と、ColorfulTalk の読み（記号列）の書き方</summary>
public sealed partial class VoiceEnginesViewModel : ObservableObject
{
    public VoiceEnginesViewModel(EditorSession session)
    {
        foreach (var engine in session.VoiceEngines.All)
        {
            var status = engine.Status;
            Engines.Add(new EngineChoice(engine.Id, engine.Name, status.Available, status.Message, status.HelpUrl));
        }
    }

    public ObservableCollection<EngineChoice> Engines { get; } = [];

    /// <summary>ColorfulTalk の読みの書き方</summary>
    public static string KoeGuide =>
        string.Join("\n",
        [
            "ColorfulTalk は漢字を読めません。漢字や英数字を含むセリフは、「読み」にカナとアクセント記号で書いてください。",
            "・' の直前のモーラの後で声が下がる（アクセント核）。' のない句は平板型",
            "・/ はアクセント句の区切り、、は短い間、。は文末、？は疑問の文末",
            "・助詞は発音どおり「わ・え・お」と書く（こんにちわ）",
            "例: きょ'うわ/い'い/てんきです。",
        ]);

    public static string ColorfulTalkUrl => "https://github.com/ukagaka-irono/colorful-talk";
}
