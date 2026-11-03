using Avalonia.Data.Converters;
using Avalonia.Media;

namespace NakkuntMovieMaker.App.ViewModels;

/// <summary>設定のダイアログで使う変換</summary>
public static class SettingsDialogConverters
{
    /// <summary>true なら太字（最初の割り当てから変えたショートカットを目立たせる）</summary>
    public static readonly IValueConverter BoldWhenTrue =
        new FuncValueConverter<bool, FontWeight>(value => value ? FontWeight.Bold : FontWeight.Normal);
}
