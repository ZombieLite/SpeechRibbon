using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;

namespace SpeechRibbon;

public partial class MainWindow
{
    private readonly AiSettingsStore _settingsStore = new(AiSettingsStore.DefaultPath);
    private readonly AiClient _aiClient = new();
    private AiSettings _aiSettings = new();
    private CancellationTokenSource? _aiCancellation, _probeCancellation;
    private string _aiAnswer = "", _sentPrompt = "", _settingsLoadError = "";
    private bool _aiReady, _editorOpen = true;
    private int _aiGeneration;

    private void InitializeAi()
    {
        try { _aiSettings = _settingsStore.Load(); } catch (Exception e) { _settingsLoadError = e.Message; }
        PromptBox.Text = AiDefaults.Prompt;
        _aiReady = true; RenderAi();
    }
    private void OpenConnection_Click(object sender, RoutedEventArgs e)
    {
        ConnectionUrl.Text = _aiSettings.BaseUrl;
        ConnectionKey.Password = _aiSettings.ApiKey;
        ConnectionModel.Text = _aiSettings.Model;
        ConnectionStatus.Text = _settingsLoadError;
        ConnectionOverlay.Visibility = Visibility.Visible;
    }
    private AiSettings ReadSettings() => new() { BaseUrl = ConnectionUrl.Text.Trim(), ApiKey = ConnectionKey.Password.Trim(), Model = ConnectionModel.Text.Trim() };
    private void CloseConnection_Click(object sender, RoutedEventArgs e)
    {
        _probeCancellation?.Cancel(); ConnectionOverlay.Visibility = Visibility.Collapsed;
        ConnectionKey.Clear();
    }
    private void SaveConnection_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = ReadSettings(); _settingsStore.Save(settings);
            InvalidateAiRequest(); _aiSettings = settings; _settingsLoadError = "";
            CloseConnection_Click(sender, e); RenderAi();
        }
        catch (AiConnectionException error) { ConnectionStatus.Text = error.Message; }
    }
    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        if (_probeCancellation is not null) return;
        var cancellation = _probeCancellation = new CancellationTokenSource();
        TestConnectionButton.IsEnabled = SaveConnectionButton.IsEnabled = false;
        ConnectionStatus.Text = "Проверка подключения…";
        try
        {
            await _aiClient.CompleteAsync(ReadSettings(), "", "", cancellation.Token, probe: true);
            if (!cancellation.IsCancellationRequested) ConnectionStatus.Text = "Подключение работает. Модель ответила.";
        }
        catch (OperationCanceledException) { ConnectionStatus.Text = "Проверка отменена."; }
        catch (AiConnectionException error) { ConnectionStatus.Text = error.Message; }
        catch { ConnectionStatus.Text = "Не удалось проверить подключение. Проверь настройки и повтори."; }
        finally { cancellation.Dispose(); _probeCancellation = null; TestConnectionButton.IsEnabled = SaveConnectionButton.IsEnabled = true; }
    }
    private void InvalidateAiRequest()
    {
        ++_aiGeneration; _aiCancellation?.Cancel();
    }
    private void InvalidateAiDocument()
    {
        InvalidateAiRequest(); _aiAnswer = _sentPrompt = ""; _editorOpen = true;
        if (_aiReady) { AiStatusText.Text = ""; RenderAi(); }
    }
    private async void SendPrompt_Click(object sender, RoutedEventArgs e)
    {
        if (_aiCancellation is not null || _isBusy || _document?.Segments.Count is not > 0 || string.IsNullOrWhiteSpace(PromptBox.Text) || !_aiSettings.IsComplete) return;
        var generation = ++_aiGeneration; var document = _document; var prompt = PromptBox.Text;
        var transcript = TranscriptExporter.ToText(document);
        var cancellation = _aiCancellation = new CancellationTokenSource();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var waitTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        void UpdateWaitStatus()
        {
            if (generation != _aiGeneration || cancellation.IsCancellationRequested) return;
            AiStatusText.Text = $"Ожидаю ответ — {elapsed.Elapsed:mm\\:ss}. Запрос можно отменить.";
        }
        waitTimer.Tick += (_, _) => UpdateWaitStatus();
        UpdateWaitStatus(); waitTimer.Start(); RenderAi();
        try
        {
            var answer = await _aiClient.CompleteAsync(_aiSettings, prompt, transcript, cancellation.Token);
            if (generation != _aiGeneration || !ReferenceEquals(document, _document) || cancellation.IsCancellationRequested) return;
            _aiAnswer = answer; _sentPrompt = prompt; _editorOpen = false; _hasUnsavedResult = true; AiStatusText.Text = "";
        }
        catch (OperationCanceledException) { if (generation == _aiGeneration) AiStatusText.Text = "Запрос отменён. Промпт и предыдущий ответ сохранены."; }
        catch (AiConnectionException error) { if (generation == _aiGeneration) AiStatusText.Text = error.Message; }
        catch { if (generation == _aiGeneration) AiStatusText.Text = "Не удалось получить ответ. Промпт и предыдущий результат сохранены."; }
        finally { waitTimer.Stop(); elapsed.Stop(); cancellation.Dispose(); _aiCancellation = null; RenderAi(); }
    }
    private void CancelAi_Click(object sender, RoutedEventArgs e) => _aiCancellation?.Cancel();
    private void ResultTab_Changed(object sender, RoutedEventArgs e) => RenderAi();
    private void Prompt_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => RenderAi();
    private void EditPrompt_Click(object sender, RoutedEventArgs e) { _editorOpen = true; RenderAi(); }
    private void CollapsePrompt_Click(object sender, RoutedEventArgs e) { _editorOpen = false; RenderAi(); }
    private void RenderAi()
    {
        if (!_aiReady) return;
        AiTab.Visibility = _aiSettings.IsComplete ? Visibility.Visible : Visibility.Collapsed;
        if (!_aiSettings.IsComplete && AiTab.IsChecked == true) TranscriptTab.IsChecked = true;
        bool ai = AiTab.IsChecked == true, transcript = _document?.Segments.Count > 0 && !_isBusy, answer = _aiAnswer.Length > 0, waiting = _aiCancellation is not null;
        StartButton.Visibility = CancelButton.Visibility = ai ? Visibility.Collapsed : Visibility.Visible;
        AiEmptyText.Text = _isBusy ? "Выполняется транскрибация. Дождись завершения." : "Сначала получите транскрибацию";
        TranscriptPanel.Visibility = ai ? Visibility.Collapsed : Visibility.Visible;
        AiPanel.Visibility = ai ? Visibility.Visible : Visibility.Collapsed;
        ResultHeading.Visibility = !ai && _document?.Segments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AiEmptyText.Visibility = transcript ? Visibility.Collapsed : Visibility.Visible;
        AiContent.Visibility = transcript ? Visibility.Visible : Visibility.Collapsed;
        PromptEditor.Visibility = PromptHeading.Visibility = _editorOpen ? Visibility.Visible : Visibility.Collapsed;
        PromptRow.Height = _editorOpen ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        AnswerRow.Height = answer && !_editorOpen ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        AnswerScroller.Visibility = AnswerPanel.Visibility = answer && !_editorOpen ? Visibility.Visible : Visibility.Collapsed;
        AnswerFormatting.Render(AnswerText, _aiAnswer);
        SendPromptButton.Visibility = ai && _editorOpen ? Visibility.Visible : Visibility.Collapsed;
        SendPromptButton.IsEnabled = transcript && !waiting && !string.IsNullOrWhiteSpace(PromptBox.Text);
        PromptBox.IsEnabled = !waiting;
        EditPromptButton.Visibility = !_editorOpen && answer ? Visibility.Visible : Visibility.Collapsed;
        CollapsePromptButton.Visibility = _editorOpen && answer ? Visibility.Visible : Visibility.Collapsed;
        CancelAiButton.Visibility = ai && waiting ? Visibility.Visible : Visibility.Collapsed;
        AiStatusPanel.Visibility = AiStatusText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        AnswerLabel.Text = answer && (_sentPrompt != PromptBox.Text || waiting) ? "Предыдущий ответ" : "";
        AnswerLabel.Visibility = AnswerLabel.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        CopyButton.IsEnabled = SaveButton.IsEnabled = ai ? answer : _document?.Segments.Count > 0;
    }
    private void ExportAi(bool save)
    {
        if (_aiAnswer.Length == 0) return;
        try
        {
            if (!save) { Clipboard.SetText(_aiAnswer); AiStatusText.Text = "Ответ скопирован."; }
            else
            {
                var dialog = new SaveFileDialog { Title = "Сохранить ответ ИИ", Filter = "Текст (*.txt)|*.txt", FileName = "ai-result.txt", AddExtension = true, OverwritePrompt = true };
                if (dialog.ShowDialog(this) != true) return;
                File.WriteAllText(dialog.FileName, _aiAnswer, new UTF8Encoding(false));
                AiStatusText.Text = "Ответ сохранён.";
            }
        }
        catch { AiStatusText.Text = "Не удалось скопировать или сохранить ответ. Попробуй ещё раз."; }
        RenderAi();
    }
}
