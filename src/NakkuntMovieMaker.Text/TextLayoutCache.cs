using System.Collections.Concurrent;
using NakkuntMovieMaker.Core.Model;

namespace NakkuntMovieMaker.Text;

/// <summary>
/// 文字の配置を使い回す（配置は時間がかかるので、同じ文字とスタイルなら作り直さない）。
/// 描画（CPU・GPU）と、プレビューで大きさを測る処理とで共有する。増えすぎたら捨てる。複数のスレッドから使ってよい
/// </summary>
public sealed class TextLayoutCache(FontProvider? fonts = null, int capacity = 512)
{
    private readonly FontProvider _fonts = fonts ?? FontProvider.Default;
    private readonly ConcurrentDictionary<(string Text, TextStyle Style), TextLayout> _layouts = new();

    public TextLayout Get(string text, TextStyle style)
    {
        if (_layouts.Count >= capacity)
        {
            _layouts.Clear();
        }

        return _layouts.GetOrAdd((text, style), key => TextLayout.Create(key.Text, key.Style, _fonts));
    }
}
