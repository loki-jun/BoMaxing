using Avalonia.Controls;

namespace BoMaxing.Studio;

public partial class HmiRuntimeWindow : Window
{
    public HmiRuntimeWindow()
    {
        InitializeComponent();
    }

    public HmiRuntimeWindow(StudioViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
