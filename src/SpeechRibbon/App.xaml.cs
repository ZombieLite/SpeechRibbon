using Microsoft.Win32;
using System.IO;
using System.Text;
using System.Windows;

namespace SpeechRibbon;

public partial class App : Application
{
    private RuntimeWorkspace? _startupWorkspace;

    private async void Application_Startup(object sender, StartupEventArgs e)
    {
        try
        {
            WindowsTheme.Apply(Resources);
            SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
            RuntimeWorkspace.CleanupStaleRuns();
            if (e.Args.Length > 0 && string.Equals(e.Args[0], "--diagnostics", StringComparison.OrdinalIgnoreCase))
            {
                var dialog = new SaveFileDialog
                {
                    Title = "Сохранить диагностику",
                    Filter = "Текст (*.txt)|*.txt",
                    AddExtension = true,
                    OverwritePrompt = true,
                    FileName = "speechribbon-diagnostics.txt"
                };
                if (dialog.ShowDialog() != true) { Shutdown(1); return; }
                File.WriteAllText(dialog.FileName, DiagnosticsReport.Create("MANUAL"), new UTF8Encoding(false));
                Shutdown(0);
                return;
            }
            if (e.Args.Length > 0 && string.Equals(e.Args[0], "--extract-third-party", StringComparison.OrdinalIgnoreCase))
            {
                var target = e.Args.Length > 1 ? e.Args[1] : null;
                var interactive = string.IsNullOrWhiteSpace(target);
                if (string.IsNullOrWhiteSpace(target))
                {
                    var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Куда извлечь сторонние материалы" };
                    if (dialog.ShowDialog() != true) { Shutdown(1); return; }
                    target = dialog.FolderName;
                }
                await ThirdPartyExtractor.ExtractAsync(target!, CancellationToken.None);
                if (interactive) MessageBox.Show($"Материалы извлечены в:\n{target}", "SpeechRibbon", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown(0);
                return;
            }

            _startupWorkspace = await RuntimeWorkspace.CreateAsync(CancellationToken.None);
            var window = new MainWindow(_startupWorkspace);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ErrorPresenter.For(ex), "SpeechRibbon не запущен", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
        }
    }

    private void Application_Exit(object sender, ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
        _startupWorkspace?.Dispose();
    }

    private void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) =>
        Dispatcher.BeginInvoke(() => WindowsTheme.Apply(Resources));
}
