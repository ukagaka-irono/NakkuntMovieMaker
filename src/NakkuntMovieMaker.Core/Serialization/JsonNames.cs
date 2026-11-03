namespace NakkuntMovieMaker.Core.Serialization;

/// <summary>保存形式で使う種類の名前</summary>
public static class ItemTypeIds
{
    public const string Video = "nmm.video";
    public const string Image = "nmm.image";
    public const string Audio = "nmm.audio";
    public const string Text = "nmm.text";
    public const string Shape = "nmm.shape";
    public const string Voice = "nmm.voice";
    public const string Tachie = "nmm.tachie";
}

/// <summary>列挙値と保存形式の名前（camelCase）の変換</summary>
internal static class EnumNames
{
    public static string ToName<T>(T value)
        where T : struct, Enum
    {
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    public static bool TryParse<T>(string text, out T value)
        where T : struct, Enum
    {
        foreach (var candidate in Enum.GetValues<T>())
        {
            if (ToName(candidate) == text)
            {
                value = candidate;
                return true;
            }
        }

        value = default;
        return false;
    }
}
