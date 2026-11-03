namespace NakkuntMovieMaker.Core.Model;

// モデルの ID。ミリ秒単位で作成順に並ぶ GUID v7 を使い、種類ごとに型を分けて取り違えを防ぐ

public readonly record struct SceneId(Guid Value)
{
    public static SceneId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct LayerId(Guid Value)
{
    public static LayerId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct ItemId(Guid Value)
{
    public static ItemId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

/// <summary>アイテムのグループ（まとめて移動する単位）</summary>
public readonly record struct GroupId(Guid Value)
{
    public static GroupId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct MarkerId(Guid Value)
{
    public static MarkerId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct CharacterId(Guid Value)
{
    public static CharacterId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct AssetId(Guid Value)
{
    public static AssetId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct EffectId(Guid Value)
{
    public static EffectId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}
