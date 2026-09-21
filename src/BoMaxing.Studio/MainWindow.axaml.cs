using Avalonia.Controls;
using Avalonia;
using Avalonia.Input;

namespace BoMaxing.Studio;

public partial class MainWindow : Window
{
    private WorkflowNodeViewModel? _draggedNode;
    private IPointer? _dragPointer;
    private Vector _dragOffset;

    public MainWindow()
    {
        InitializeComponent();
        var viewModel = new StudioViewModel();
        DataContext = viewModel;
        viewModel.OpenHmiRuntimeRequested += OpenHmiRuntime;
    }

    private void OpenHmiRuntime(object? sender, EventArgs e)
    {
        if (DataContext is StudioViewModel viewModel)
        {
            new HmiRuntimeWindow(viewModel).Show(this);
        }
    }

    private void NodePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control ||
            control.DataContext is not WorkflowNodeViewModel node ||
            DataContext is not StudioViewModel viewModel)
        {
            return;
        }

        var point = e.GetPosition(WorkflowCanvas);
        _draggedNode = node;
        _dragPointer = e.Pointer;
        _dragOffset = point - new Point(node.X, node.Y);
        e.Pointer.Capture(control);
        viewModel.BeginNodeMove();
        viewModel.SelectNodeFromView(node);
        e.Handled = true;
    }

    private void NodePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_draggedNode is null || !ReferenceEquals(_dragPointer, e.Pointer) ||
            DataContext is not StudioViewModel viewModel)
        {
            return;
        }

        var point = e.GetPosition(WorkflowCanvas);
        viewModel.MoveNode(
            _draggedNode,
            point.X - _dragOffset.X,
            point.Y - _dragOffset.Y);
        e.Handled = true;
    }

    private void NodePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(_dragPointer, e.Pointer))
        {
            return;
        }

        e.Pointer.Capture(null);
        _draggedNode = null;
        _dragPointer = null;
        if (DataContext is StudioViewModel viewModel)
        {
            viewModel.EndNodeMove();
        }
        e.Handled = true;
    }
}
