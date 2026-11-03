using NakkuntMovieMaker.Core.Collections;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;

namespace NakkuntMovieMaker.Core.Tests.Model;

public class ModelTests
{
    [Fact]
    public void NewProjectHasOneSceneWithEmptyLayers()
    {
        var project = Project.CreateNew("テスト", SceneFormat.Landscape1080p30);

        Assert.Equal(Project.CurrentSchemaVersion, project.SchemaVersion);
        Assert.Single(project.Scenes);
        Assert.Equal(1920, project.Root.Format.Width);
        Assert.Equal(new Rational(30, 1), project.Root.Fps);
        Assert.Equal(10, project.Root.Layers.Count);
        Assert.Empty(project.Root.Items);
        Assert.Equal(0, project.Root.Duration);
    }

    [Fact]
    public void FormatPresetsHaveExpectedSizes()
    {
        Assert.Equal((1080, 1920), (SceneFormat.Vertical1080x1920p30.Width, SceneFormat.Vertical1080x1920p30.Height));
        Assert.Equal((1080, 1080), (SceneFormat.Square1080p30.Width, SceneFormat.Square1080p30.Height));
        Assert.Equal(new Rational(60, 1), SceneFormat.Landscape1080p60.Fps);
    }

    [Fact]
    public void IdsAreUniqueVersion7Guids()
    {
        var ids = Enumerable.Range(0, 1000).Select(_ => ItemId.New()).ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.Equal(7, id.Value.Version));
    }

    [Fact]
    public void ItemRangeIsHalfOpen()
    {
        var item = new TextItem(ItemId.New(), LayerId.New(), 10, 5, "テスト");

        Assert.Equal(15, item.End);
        Assert.False(item.Contains(9));
        Assert.True(item.Contains(10));
        Assert.True(item.Contains(14));
        Assert.False(item.Contains(15));
        Assert.Equal(3, item.ToLocalFrame(13));
    }

    [Fact]
    public void SceneFindsItemsByLayerAndFrame()
    {
        var scene = Scene.CreateNew("シーン", SceneFormat.Landscape1080p30, 3);
        var bottom = scene.Layers[0].Id;
        var top = scene.Layers[2].Id;
        var background = new ShapeItem(ItemId.New(), bottom, 0, 100,
            new ShapeStyle(ShapeKind.Background, 1920, 1080, new SolidPaint(RgbaColor.Black)));
        var title = new TextItem(ItemId.New(), top, 30, 60, "タイトル");
        var later = new TextItem(ItemId.New(), top, 100, 10, "次");
        scene = scene with
        {
            Items = scene.Items.Add(background.Id, background).Add(title.Id, title).Add(later.Id, later),
        };

        Assert.Equal(110, scene.Duration);
        Assert.Equal([title, later], scene.ItemsOnLayer(top));
        Assert.Equal([background, title], scene.ItemsAt(50));
        Assert.Equal([later], scene.ItemsAt(100));
        Assert.Equal(2, scene.LayerIndex(top));
        Assert.Equal(-1, scene.LayerIndex(LayerId.New()));
    }

    [Fact]
    public void RecordsCompareByValue()
    {
        var layer = LayerId.New();
        var id = ItemId.New();

        var a = new TextItem(id, layer, 0, 10, "同じ") { Transform = Transform2D.Identity with { X = 5.0 } };
        var b = new TextItem(id, layer, 0, 10, "同じ") { Transform = Transform2D.Identity with { X = 5.0 } };

        Assert.Equal(a, b);
        Assert.NotEqual(a, b with { Text = "違う" });
    }

    [Theory]
    [InlineData("#FF8000", 255, 128, 0, 255)]
    [InlineData("#10203040", 16, 32, 48, 64)]
    public void ColorsParseAndFormat(string text, byte r, byte g, byte b, byte a)
    {
        var color = RgbaColor.Parse(text);

        Assert.Equal(new RgbaColor(r, g, b, a), color);
        Assert.Equal(text, color.ToString());
    }

    [Theory]
    [InlineData("FF8000")]
    [InlineData("#FF80")]
    [InlineData("#GG0000")]
    public void InvalidColorsAreRejected(string text)
    {
        Assert.False(RgbaColor.TryParse(text, out _));
    }

    [Fact]
    public void EquatableArrayComparesContents()
    {
        EquatableArray<int> a = [1, 2, 3];
        EquatableArray<int> b = [1, 2, 3];

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, [1, 2]);
        Assert.Equal(EquatableArray<int>.Empty, default);
    }
}
