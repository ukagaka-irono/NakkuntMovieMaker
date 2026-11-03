using Avalonia.Input;
using CommunityToolkit.Mvvm.Input;
using NakkuntMovieMaker.App.Services;

namespace NakkuntMovieMaker.App.Tests;

public sealed class ShortcutRegistryTests
{
    [Fact]
    public void DefaultsHaveNoDuplicateKeys()
    {
        var gestures = ShortcutRegistry.Defaults.Select(command => command.DefaultGesture).OfType<KeyGesture>().ToList();

        Assert.Equal(gestures.Count, gestures.Distinct().Count());
        Assert.Equal(ShortcutRegistry.Defaults.Count, ShortcutRegistry.Defaults.Select(command => command.Id).Distinct().Count());
    }

    [Fact]
    public void AssigningATakenKeyRemovesItFromTheOtherCommand()
    {
        var registry = new ShortcutRegistry();

        var previous = registry.Assign("file.export", KeyGesture.Parse("Ctrl+S"));

        Assert.Equal("file.save", previous);
        Assert.Null(registry.GestureOf("file.save"));
        Assert.Equal(KeyGesture.Parse("Ctrl+S"), registry.GestureOf("file.export"));
    }

    [Fact]
    public void SavesOnlyChangesAndLoadsThemBack()
    {
        var registry = new ShortcutRegistry();
        registry.Assign("edit.split", KeyGesture.Parse("Ctrl+K"));
        registry.Assign("play.toggle", null);

        var overrides = registry.Overrides();

        Assert.Equal(new Dictionary<string, string> { ["edit.split"] = "Ctrl+K", ["play.toggle"] = "" }, overrides);
        var loaded = new ShortcutRegistry();
        loaded.Load(overrides.Append(new("unknown", "F1")).Append(new("file.new", "???")).ToDictionary());
        Assert.Equal(KeyGesture.Parse("Ctrl+K"), loaded.GestureOf("edit.split"));
        Assert.Null(loaded.GestureOf("play.toggle"));
        Assert.Equal(KeyGesture.Parse("Ctrl+N"), loaded.GestureOf("file.new"));

        loaded.ResetAll();
        Assert.Empty(loaded.Overrides());
    }

    [Fact]
    public void LeavesPlainKeysToTextBoxes()
    {
        var registry = new ShortcutRegistry();
        var played = 0;
        var undone = 0;
        var saved = 0;
        registry.Bind("play.toggle", new RelayCommand(() => played++));
        registry.Bind("edit.undo", new RelayCommand(() => undone++));
        registry.Bind("file.save", new RelayCommand(() => saved++));

        Assert.True(registry.TryHandle(Key.Space, KeyModifiers.None, textInputFocused: false));
        Assert.False(registry.TryHandle(Key.Space, KeyModifiers.None, textInputFocused: true));
        Assert.False(registry.TryHandle(Key.Z, KeyModifiers.Control, textInputFocused: true)); // 入力欄の元に戻す
        Assert.True(registry.TryHandle(Key.S, KeyModifiers.Control, textInputFocused: true));
        Assert.False(registry.TryHandle(Key.F12, KeyModifiers.None, textInputFocused: false));

        Assert.Equal((1, 0, 1), (played, undone, saved));
    }
}
