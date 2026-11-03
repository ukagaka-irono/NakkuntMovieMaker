using System.Collections.Immutable;
using NakkuntMovieMaker.Core.Animation;
using NakkuntMovieMaker.Core.Editing;
using NakkuntMovieMaker.Core.Model;
using NakkuntMovieMaker.Core.Time;

namespace NakkuntMovieMaker.Core.Tests.Editing;

public class EditCommandsTests
{
    private readonly TestProject _t = new();

    private Scene After(IOperation operation) => _t.Scene(_t.Apply(operation));

    [Fact]
    public void MoveChangesStartAndLayer()
    {
        var scene = After(EditCommands.MoveItems(_t.Project, _t.SceneId, [_t.Shape.Id], 5, 1));

        var moved = scene.GetItem(_t.Shape.Id);
        Assert.Equal(45, moved.Start);
        Assert.Equal(_t.Layers[3], moved.Layer);
    }

    [Fact]
    public void MovingAGroupMemberMovesTheWholeGroup()
    {
        var scene = After(EditCommands.MoveItems(_t.Project, _t.SceneId, [_t.TextA.Id], 3));

        Assert.Equal(3, scene.GetItem(_t.TextA.Id).Start);
        Assert.Equal(63, scene.GetItem(_t.TextB.Id).Start);
    }

    [Fact]
    public void MovesThatOverlapOrLeaveTheTimelineAreRejected()
    {
        Assert.Throws<EditException>(() => EditCommands.MoveItems(_t.Project, _t.SceneId, [_t.Shape.Id], 0, -2)); // 動画と重なる
        Assert.Throws<EditException>(() => EditCommands.MoveItems(_t.Project, _t.SceneId, [_t.Shape.Id], -41));
        Assert.Throws<EditException>(() => EditCommands.MoveItems(_t.Project, _t.SceneId, [_t.Shape.Id], 0, 10));
    }

    [Fact]
    public void LockedLayersCannotBeEdited()
    {
        Assert.Throws<EditException>(() => EditCommands.MoveItems(_t.Project, _t.SceneId, [_t.Voice.Id], 100));
        Assert.Throws<EditException>(() => EditCommands.MoveItems(_t.Project, _t.SceneId, [_t.Shape.Id], 100, 2)); // 移動先がロック中
        Assert.Throws<EditException>(() => EditCommands.DeleteItems(_t.Project, _t.SceneId, [_t.Voice.Id]));
    }

    [Fact]
    public void TrimmingTheStartKeepsContentInPlace()
    {
        var trimmed = (VideoItem)After(EditCommands.TrimStart(_t.Project, _t.SceneId, _t.Video.Id, 30)).GetItem(_t.Video.Id);

        Assert.Equal((30L, 60L), (trimmed.Start, trimmed.Length));
        // 30 フレーム（1 秒）削ったので、素材の使い始めは 2 秒 → 3 秒
        Assert.Equal(new Rational(3, 1), trimmed.Playback.SourceOffset);
        // キーフレームもずらして、シーン上の同じフレームで同じ値になる
        Assert.Equal(_t.Video.Opacity.Evaluate(45), trimmed.Opacity.Evaluate(15), 9);
    }

    [Fact]
    public void TrimmingReversedMediaMovesTheSourceAtTheOtherEnd()
    {
        var reversed = _t.Video with { Playback = _t.Video.Playback with { Reverse = true, Speed = 2 } };
        var project = _t.Apply(EditCommands.UpdateItem(_t.Project, _t.SceneId, reversed));

        var startTrimmed = (VideoItem)_t.Scene(EditCommands.TrimStart(project, _t.SceneId, reversed.Id, 30).Apply(project).Project).GetItem(reversed.Id);
        var endTrimmed = (VideoItem)_t.Scene(EditCommands.TrimEnd(project, _t.SceneId, reversed.Id, 60).Apply(project).Project).GetItem(reversed.Id);

        Assert.Equal(new Rational(2, 1), startTrimmed.Playback.SourceOffset);
        // 終わりを 30 フレーム削る = 2 倍速で 2 秒分、素材の使い始めが進む
        Assert.Equal(new Rational(4, 1), endTrimmed.Playback.SourceOffset);
    }

    [Fact]
    public void ExtendingBeforeTheSourceStartIsRejected()
    {
        var atZero = _t.Video with { Start = 100, Layer = _t.Layers[3], Playback = MediaPlayback.Normal };
        var project = _t.Apply(EditCommands.UpdateItem(_t.Project, _t.SceneId, atZero));

        Assert.Throws<EditException>(() => EditCommands.TrimStart(project, _t.SceneId, atZero.Id, 90));
    }

    [Fact]
    public void SplitMakesTwoContinuousItems()
    {
        var scene = After(EditCommands.Split(_t.Project, _t.SceneId, [_t.Video.Id, _t.Image.Id], 45));

        var left = (VideoItem)scene.GetItem(_t.Video.Id);
        var right = (VideoItem)scene.ItemsOnLayer(_t.Layers[0]).Single(item => item.Start == 45);

        Assert.Equal((0L, 45L), (left.Start, left.Length));
        Assert.Equal((45L, 45L), (right.Start, right.Length));
        Assert.NotEqual(left.Id, right.Id);
        Assert.Equal(new Rational(2, 1) + new Rational(45, 30), right.Playback.SourceOffset);
        Assert.Equal(left.Opacity.Evaluate(44), right.Opacity.Evaluate(-1), 9);
        Assert.Equal(_t.Video.Opacity.Evaluate(50), right.Opacity.Evaluate(5), 9);
        Assert.Equal(_t.Image, scene.GetItem(_t.Image.Id)); // 45 フレームにかかっていない画像はそのまま
    }

    [Fact]
    public void SplitKeepsTheTransitionOnTheLeftPartOnly()
    {
        var withTransition = _t.Video with { Transition = new TransitionInstance("nmm.crossfade", 10) };
        var item = ItemTiming.Split(withTransition, 45, ItemId.New(), 30);

        Assert.Equal(withTransition.Transition, item.Left.Transition);
        Assert.Null(item.Right.Transition);
        Assert.NotEqual(_t.Video, withTransition); // トランジションも比較に含める
    }

    [Fact]
    public void SplitRejectsVoicesAndEmptySelections()
    {
        var unlocked = _t.Apply(EditCommands.UpdateLayer(_t.Project, _t.SceneId, _t.Scene(_t.Project).Layers[4] with { Locked = false }));

        Assert.Throws<EditException>(() => EditCommands.Split(unlocked, _t.SceneId, [_t.Voice.Id], 30));
        Assert.Throws<EditException>(() => EditCommands.Split(_t.Project, _t.SceneId, [_t.Shape.Id], 0));
    }

    [Fact]
    public void RippleDeleteClosesTheGapOnTheSameLayer()
    {
        var scene = After(EditCommands.RippleDelete(_t.Project, _t.SceneId, [_t.Video.Id]));

        Assert.False(scene.Items.ContainsKey(_t.Video.Id));
        Assert.Equal(10, scene.GetItem(_t.Image.Id).Start);
        Assert.Equal(40, scene.GetItem(_t.Shape.Id).Start); // ほかのレイヤーは動かない
    }

    [Fact]
    public void RippleDeleteAcrossAllLayersNeedsAClearGap()
    {
        Assert.Throws<EditException>(() => EditCommands.RippleDelete(_t.Project, _t.SceneId, [_t.Shape.Id], allLayers: true));

        var image = _t.Image.Id;
        var scene = After(EditCommands.RippleDelete(_t.Project, _t.SceneId, [image], allLayers: true));
        Assert.False(scene.Items.ContainsKey(image));
    }

    [Fact]
    public void RippleDeleteOfAGroupClosesAllGaps()
    {
        var scene = After(EditCommands.RippleDelete(_t.Project, _t.SceneId, [_t.TextA.Id]));

        Assert.False(scene.Items.ContainsKey(_t.TextA.Id));
        Assert.False(scene.Items.ContainsKey(_t.TextB.Id));
    }

    [Fact]
    public void RippleTrimPushesOrPullsFollowingItems()
    {
        var shorter = After(EditCommands.RippleTrimEnd(_t.Project, _t.SceneId, _t.Video.Id, 60));
        var longer = After(EditCommands.RippleTrimEnd(_t.Project, _t.SceneId, _t.Video.Id, 120));

        Assert.Equal(70, shorter.GetItem(_t.Image.Id).Start);
        Assert.Equal(130, longer.GetItem(_t.Image.Id).Start);
    }

    [Fact]
    public void RollMovesTheBoundaryBetweenAdjacentItems()
    {
        var adjacent = _t.Apply(EditCommands.MoveItems(_t.Project, _t.SceneId, [_t.Image.Id], -10));

        var scene = _t.Scene(EditCommands.RollEdit(adjacent, _t.SceneId, _t.Video.Id, _t.Image.Id, 80).Apply(adjacent).Project);

        Assert.Equal(80, scene.GetItem(_t.Video.Id).End);
        Assert.Equal((80L, 70L), (scene.GetItem(_t.Image.Id).Start, scene.GetItem(_t.Image.Id).Length));
        Assert.Throws<EditException>(() => EditCommands.RollEdit(_t.Project, _t.SceneId, _t.Video.Id, _t.Image.Id, 95));
    }

    [Fact]
    public void GroupAndUngroup()
    {
        var grouped = After(EditCommands.GroupItems(_t.Project, _t.SceneId, [_t.Video.Id, _t.Shape.Id]));
        var ungrouped = After(EditCommands.UngroupItems(_t.Project, _t.SceneId, [_t.TextA.Id]));

        Assert.NotNull(grouped.GetItem(_t.Video.Id).Group);
        Assert.Equal(grouped.GetItem(_t.Video.Id).Group, grouped.GetItem(_t.Shape.Id).Group);
        Assert.Null(ungrouped.GetItem(_t.TextA.Id).Group);
        Assert.Null(ungrouped.GetItem(_t.TextB.Id).Group);
        Assert.Throws<EditException>(() => EditCommands.GroupItems(_t.Project, _t.SceneId, [_t.Shape.Id]));
    }

    [Fact]
    public void PasteCreatesNewIdsAndNewGroups()
    {
        var copied = EditCommands.Copy(_t.Project, _t.SceneId, [_t.TextA.Id]);
        var scene = After(EditCommands.Paste(_t.Project, _t.SceneId, copied, 200, 3));

        var pasted = scene.ItemsOnLayer(_t.Layers[3]).Cast<TextItem>().ToList();
        Assert.Equal(["A", "B"], pasted.Select(item => item.Text));
        Assert.Equal([200L, 260L], pasted.Select(item => item.Start));
        Assert.DoesNotContain(pasted, item => item.Id == _t.TextA.Id || item.Id == _t.TextB.Id);
        Assert.NotNull(pasted[0].Group);
        Assert.Equal(pasted[0].Group, pasted[1].Group);
        Assert.NotEqual(_t.TextA.Group, pasted[0].Group);
    }

    [Fact]
    public void PasteIntoMissingOrLockedLayersIsRejected()
    {
        var copied = EditCommands.Copy(_t.Project, _t.SceneId, [_t.Video.Id, _t.Shape.Id]);

        Assert.Throws<EditException>(() => EditCommands.Paste(_t.Project, _t.SceneId, copied, 200, 4));
        Assert.Throws<EditException>(() => EditCommands.Paste(_t.Project, _t.SceneId, copied, 200, 2)); // 2 つ上がロック中
    }

    [Fact]
    public void DuplicatePlacesCopiesRightAfterTheSelection()
    {
        var scene = After(EditCommands.Duplicate(_t.Project, _t.SceneId, [_t.Shape.Id]));

        var copies = scene.ItemsOnLayer(_t.Layers[2]).ToList();
        Assert.Equal([40L, 50L], copies.Select(item => item.Start));
    }

    [Fact]
    public void RemovingALayerRemovesItsItems()
    {
        var scene = After(EditCommands.RemoveLayer(_t.Project, _t.SceneId, _t.Layers[1]));

        Assert.Equal(4, scene.Layers.Count);
        Assert.False(scene.Items.ContainsKey(_t.TextA.Id));
        Assert.Throws<EditException>(() => EditCommands.RemoveLayer(_t.Project, _t.SceneId, _t.Layers[4]));
    }

    [Fact]
    public void UpdateItemChecksOverlap()
    {
        Assert.Throws<EditException>(() => EditCommands.UpdateItem(_t.Project, _t.SceneId, _t.Shape with { Layer = _t.Layers[0] }));

        var scene = After(EditCommands.UpdateItem(_t.Project, _t.SceneId, _t.Shape with { Opacity = 0.5 }));
        Assert.Equal(0.5, ((ShapeItem)scene.GetItem(_t.Shape.Id)).Opacity.Evaluate(0));
    }

    [Fact]
    public void ExistingOverlapsOnUntouchedLayersDoNotBlockEdits()
    {
        var overlapping = new TextItem(ItemId.New(), _t.Layers[3], 0, 50, "重なり1");
        var overlapping2 = new TextItem(ItemId.New(), _t.Layers[3], 10, 50, "重なり2");
        var project = new AddItem(_t.SceneId, overlapping2).Apply(new AddItem(_t.SceneId, overlapping).Apply(_t.Project).Project).Project;

        var scene = _t.Scene(EditCommands.MoveItems(project, _t.SceneId, [_t.Shape.Id], 1).Apply(project).Project);

        Assert.Equal(41, scene.GetItem(_t.Shape.Id).Start);
    }

    [Fact]
    public void EffectKeyframesShiftWithTrim()
    {
        var effect = new EffectInstance(EffectId.New(), "nmm.blur",
            ImmutableDictionary<string, ParameterValue>.Empty.Add("radius",
                new NumberParameter(Animatable.FromKeys(new Keyframe<double>(0, 0), new Keyframe<double>(40, 10)))));
        var project = _t.Apply(EditCommands.UpdateItem(_t.Project, _t.SceneId, _t.Shape with { Effects = [effect] }));

        var trimmed = _t.Scene(EditCommands.TrimStart(project, _t.SceneId, _t.Shape.Id, 45).Apply(project).Project).GetItem(_t.Shape.Id);

        var radius = (NumberParameter)trimmed.Effects[0].Parameters["radius"];
        Assert.Equal([-5L, 35L], radius.Value.Keys.Select(key => key.Frame));
    }

    [Fact]
    public void ChangingTheFrameRateKeepsTimes()
    {
        var format = SceneFormat.Landscape1080p60 with { Width = 1280, Height = 720 };
        var marker = new Marker(MarkerId.New(), 15, "目印", RgbaColor.White);
        var project = new SetMarker(_t.SceneId, marker.Id, marker).Apply(_t.Project).Project;

        var operation = EditCommands.ChangeSceneSettings(project, _t.SceneId, "新しい名前", format);
        var scene = operation.Apply(project).Project.GetScene(_t.SceneId);

        Assert.Equal(("新しい名前", format), (scene.Name, scene.Format));
        Assert.Equal((80L, 2 * _t.Shape.Length), (scene.GetItem(_t.Shape.Id).Start, scene.GetItem(_t.Shape.Id).Length));
        Assert.Equal(30, scene.Markers[0].Frame);
        Assert.Equal(project, operation.Apply(project).Inverse.Apply(operation.Apply(project).Project).Project);

        // フレームレートが同じなら、位置は変わらない
        var resized = EditCommands.ChangeSceneSettings(project, _t.SceneId, "メイン", SceneFormat.Landscape1080p30 with { Width = 640 }).Apply(project).Project;
        Assert.Equal(40, resized.GetScene(_t.SceneId).GetItem(_t.Shape.Id).Start);
    }

    [Fact]
    public void RenamesTheProject()
    {
        var result = new RenameProject("別の名前").Apply(_t.Project);

        Assert.Equal("別の名前", result.Project.Name);
        Assert.Equal(_t.Project, result.Inverse.Apply(result.Project).Project);
    }
}
