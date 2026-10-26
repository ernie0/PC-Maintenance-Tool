using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using PcMaintenance.App.ViewModels;

namespace PcMaintenance.App.Views;

/// <summary>
/// Follows a one-click scan or fix in a small window: the step being worked on, what it is doing, and each result as it
/// comes in. Closing it does not stop the run; when the run ends it shows the outcome for a moment and closes.
/// </summary>
public partial class MaintenanceProgressWindow : Window
{
    private static MaintenanceProgressWindow? _open;
    private readonly MaintenanceViewModel _viewModel;
    private readonly DispatcherTimer _closeTimer = new() { Interval = TimeSpan.FromSeconds(1.6) };

    private MaintenanceProgressWindow(MaintenanceViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _closeTimer.Tick += (_, _) => Close();
        viewModel.PropertyChanged += OnRunChanged;
        Closed += (_, _) =>
        {
            viewModel.PropertyChanged -= OnRunChanged;
            _closeTimer.Stop();
            _open = null;
        };
    }

    /// <summary>Shows the window for a run that just started; one still showing the previous result is kept open instead.</summary>
    public static void ShowFor(MaintenanceViewModel viewModel, Window owner)
    {
        if (_open is { } window)
        {
            window._closeTimer.Stop();
            window.Activate();
            return;
        }
        _open = new MaintenanceProgressWindow(viewModel) { Owner = owner };
        _open.Show();
    }

    private void OnRunChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MaintenanceViewModel.IsRunFinished) && _viewModel.IsRunFinished) _closeTimer.Start();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
