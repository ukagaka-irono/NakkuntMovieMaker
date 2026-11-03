using CommunityToolkit.Mvvm.ComponentModel;

namespace NakkuntMovieMaker.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Title { get; set; } = "NakkuntMovieMaker";
}
