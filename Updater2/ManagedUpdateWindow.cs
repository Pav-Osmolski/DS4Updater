using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DS4Updater;

// Setup owns installed-file/process coordination and its own completion UI.
// This window never enters the legacy ZIP-copy/kill/relaunch lifetime.
internal sealed class ManagedUpdateWindow : Window
{
    private readonly ManagedUpdateRequest request;
    private readonly Func<string, string> validateRegisteredRoot;
    private readonly CancellationTokenSource cancellation = new();
    private readonly TextBlock status;
    private readonly Button closeButton;
    private readonly ProgressBar progress;
    private bool busy = true, launching;

    internal ManagedUpdateWindow(ManagedUpdateRequest request, Func<string, string> validateRegisteredRoot = null)
    {
        this.request = request;
        this.validateRegisteredRoot = validateRegisteredRoot;
        Title = "Update DS4Windows";
        Width = 560;
        Height = 340;
        MinWidth = 420;
        MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(11, 20, 30));
        Foreground = Brushes.WhiteSmoke;
        var panel = new StackPanel { Margin = new Thickness(28) };
        panel.Children.Add(new TextBlock { Text = "Update DS4Windows", FontSize = 25, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = request.ReleaseTag ?? "Checking your update channel",
            Foreground = Brushes.LightSkyBlue, Margin = new Thickness(0, 6, 0, 18) });
        status = new TextBlock { Text = "Preparing a verified update…", TextWrapping = TextWrapping.Wrap, MinHeight = 52 };
        panel.Children.Add(status);
        progress = new ProgressBar { IsIndeterminate = true, Height = 5, Margin = new Thickness(0, 16, 0, 16) };
        panel.Children.Add(progress);
        panel.Children.Add(new TextBlock { Text = "Setup will update your existing installation. Your profiles and settings stay in place.",
            Foreground = Brushes.LightSlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20) });
        closeButton = new Button { Content = "Cancel", Padding = new Thickness(12, 7, 12, 7), HorizontalAlignment = HorizontalAlignment.Right };
        closeButton.Click += (_, _) => { if (busy) Cancel(); else Close(); };
        panel.Children.Add(closeButton);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Loaded += RunUpdate;
        Closing += OnClosing;
        Closed += (_, _) => cancellation.Dispose();
    }

    private async void RunUpdate(object sender, RoutedEventArgs args)
    {
        Loaded -= RunUpdate;
        cancellation.CancelAfter(TimeSpan.FromMinutes(15));
        bool setupOpened = false;
        var reporting = new Progress<PortableUpdateProgress>(update =>
        {
            status.Text = update.Message;
            if (update.Applying) { launching = true; closeButton.IsEnabled = false; }
        });
        try
        {
            using var operations = new ManagedUpdateOperations(validateRegisteredRoot);
            PortableReleaseIdentity release = await Task.Run(() => ManagedUpdateCoordinator.ExecuteAsync(
                request, operations, reporting, cancellation.Token));
            setupOpened = release != null;
            status.Text = setupOpened ? "Setup opened. Follow its instructions to complete the update." :
                "Your DS4Windows installation is up to date on its update channel.";
        }
        catch (OperationCanceledException) { status.Text = "Update preparation canceled. Your installed files have not been changed."; }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        { status.Text = "Setup was canceled. Your installed files have not been changed."; }
        catch (Exception error) { status.Text = "Setup could not be opened safely. Your installed files have not been changed.\n\n" + error.Message; }
        finally
        {
            busy = false;
            launching = false;
            progress.IsIndeterminate = false;
            closeButton.IsEnabled = true;
            closeButton.Content = "Close";
        }
        // Release the installed updater image before MSI replaces it. Opening
        // Setup is not an installation-success signal and never relaunches DS4.
        if (setupOpened) Close();
    }

    private void Cancel()
    {
        if (launching) return;
        cancellation.Cancel();
        closeButton.IsEnabled = false;
        status.Text = "Canceling safely. Please wait…";
    }

    private void OnClosing(object sender, CancelEventArgs args)
    {
        if (!busy) return;
        args.Cancel = true;
        Cancel();
    }
}
