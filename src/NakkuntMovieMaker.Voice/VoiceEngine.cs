using System.Collections.Immutable;
using NakkuntMovieMaker.Core.Model;

namespace NakkuntMovieMaker.Voice;

/// <summary>合成した声（モノラル float32、-1〜1）</summary>
public sealed record VoiceAudio(float[] Samples, int SampleRate)
{
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Samples.Length / SampleRate);
}

/// <summary>エンジンの中の声。Id は保存するときの名前、Name は画面に出す名前</summary>
public sealed record VoiceInfo(string Id, string Name);

/// <summary>
/// 合成の依頼。Reading は読み（エンジンの音声記号列）で、null ならエンジンが本文から作る
/// </summary>
public sealed record VoiceRequest(string Text, string? Reading, string VoiceId, VoiceParams Params);

/// <summary>
/// エンジンを使えるか。使えないときは Message に理由を、HelpUrl に導入の案内（公式ページなど）を入れる
/// </summary>
public sealed record VoiceEngineStatus(bool Available, string? Message = null, string? HelpUrl = null)
{
    public static readonly VoiceEngineStatus Ready = new(true);
}

/// <summary>音声合成に失敗した、またはエンジンを使えない。Message は利用者に表示できる文</summary>
public sealed class VoiceEngineException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>
/// 文章を、エンジンが読む音声記号列に変える（AqKanji2Koe のように、漢字かな交じり文から読みを作るもの）
/// </summary>
public interface IPhoneticConverter
{
    string Convert(string text);
}

/// <summary>
/// 音声合成エンジン。合成（Synthesize）と読みの作成（MakeReading）は、VoiceService がエンジンごとに 1 本のスレッドで順番に呼ぶので、
/// スレッドセーフでなくてよい
/// </summary>
public interface IVoiceEngine : IDisposable
{
    /// <summary>種類の ID（"sapi"、"aquestalk1" など）。キャラクターの設定に保存する</summary>
    string Id { get; }

    /// <summary>画面に出す名前</summary>
    string Name { get; }

    /// <summary>合成の結果に関わるエンジンの版。キャッシュのキーに入れ、エンジンを入れ替えたら作り直す</summary>
    string Version { get; }

    VoiceEngineStatus Status { get; }

    IReadOnlyList<VoiceInfo> Voices { get; }

    /// <summary>エンジンを呼ぶスレッドを STA にする必要があるか（SAPI など COM を使うエンジン）</summary>
    bool RequiresSingleThreadedApartment => false;

    /// <summary>本文から読みを作る。読みを使わないエンジンは null</summary>
    string? MakeReading(string text);

    /// <exception cref="VoiceEngineException">合成できなかった</exception>
    VoiceAudio Synthesize(VoiceRequest request, CancellationToken cancellationToken);
}

/// <summary>使える音声合成エンジンの一覧</summary>
public sealed class VoiceEngineRegistry : IDisposable
{
    private readonly ImmutableDictionary<string, IVoiceEngine> _engines;

    public VoiceEngineRegistry(IEnumerable<IVoiceEngine> engines)
    {
        _engines = engines.ToImmutableDictionary(engine => engine.Id, StringComparer.Ordinal);
    }

    public IEnumerable<IVoiceEngine> All => _engines.Values.OrderBy(engine => engine.Id, StringComparer.Ordinal);

    public IVoiceEngine? Find(string id) => _engines.GetValueOrDefault(id);

    public void Dispose()
    {
        foreach (var engine in _engines.Values)
        {
            engine.Dispose();
        }
    }
}
