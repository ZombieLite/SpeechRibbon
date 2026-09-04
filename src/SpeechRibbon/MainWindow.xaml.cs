using Microsoft.Win32;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SpeechRibbon;

public partial class MainWindow : Window
{
    private readonly RuntimeWorkspace _workspace;
    private readonly TranscriptionPipeline _pipeline;
    private CancellationTokenSource? _workCancellation;
    private MediaInfo? _media;
    private TranscriptDocument? _document;
    private bool _hasUnsavedResult;
    private bool _isBusy;

    public MainWindow(RuntimeWorkspace workspace)
    {
        InitializeComponent();
        _workspace = workspace;
        _pipeline = new TranscriptionPipeline(workspace);
        VersionText.Text = RuntimeWorkspace.Version;
        FilePathBox.AddHandler(DragDrop.PreviewDragOverEvent, new DragEventHandler(FileArea_DragOver), true);
        FilePathBox.AddHandler(DragDrop.PreviewDropEvent, new DragEventHandler(FileArea_Drop), true);
    }

    private async void ChooseFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите аудио или видео",
            Filter = "Поддерживаемые медиа|*.wav;*.mp3;*.flac;*.m4a;*.aac;*.ogg;*.oga;*.opus;*.wma;*.mp4;*.m4v;*.mov;*.mkv;*.webm;*.avi;*.wmv|Все файлы|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true) await LoadFileAsync(dialog.FileName);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_isBusy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (!_isBusy && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files) await LoadFileAsync(files[0]);
        e.Handled = true;
    }

    private void FileArea_DragOver(object sender, DragEventArgs e) => Window_DragOver(sender, e);
    private async void FileArea_Drop(object sender, DragEventArgs e)
    {
        if (!_isBusy && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files) await LoadFileAsync(files[0]);
        e.Handled = true;
    }

    private async Task LoadFileAsync(string path)
    {
        SetBusy(true, "Проверка файла", "Читаю контейнер и аудиодорожки…");
        try
        {
            _media = await _pipeline.InspectAsync(path, CancellationToken.None);
            FilePathBox.Text = path;
            FileInfoText.Text = $"{_media.Duration:hh\\:mm\\:ss} · {new FileInfo(path).Length / 1_048_576d:F1} МБ · аудиодорожек: {_media.AudioTracks.Count}";
            TrackBox.ItemsSource = _media.AudioTracks;
            TrackBox.SelectedIndex = 0;
            TrackPanel.Visibility = _media.AudioTracks.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            TrackBox.IsEnabled = _media.AudioTracks.Count > 1;
            StartButton.IsEnabled = true;
            StatusTitle.Text = "Файл готов";
            StatusDetail.Text = "Выберите режим и нажмите «Начать».";
        }
        catch (Exception ex)
        {
            _media = null;
            StartButton.IsEnabled = false;
            MessageBox.Show(this, ErrorPresenter.For(ex), "Не удалось открыть файл", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusTitle.Text = "Файл не принят";
            StatusDetail.Text = ErrorPresenter.For(ex);
        }
        finally { SetBusy(false); }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_media is null || TrackBox.SelectedItem is not AudioTrack track) return;
        _workCancellation = new CancellationTokenSource();
        SetBusy(true, "Подготовка аудио", "Проверяю ресурсы и декодирую выбранную дорожку…");
        CancelButton.IsEnabled = true;
        WorkProgress.Value = 0;
        var progress = new Progress<WorkProgress>(p =>
        {
            StatusTitle.Text = p.Phase switch
            {
                WorkPhase.Preparing => "Подготовка аудио",
                WorkPhase.Translating => "Перевод на русский",
                _ => "Распознавание"
            };
            StatusDetail.Text = $"{p.Message} · обработано {p.Processed:hh\\:mm\\:ss} · прошло {p.Elapsed:hh\\:mm\\:ss}";
            WorkProgress.Value = Math.Clamp(p.Percent, 0, 100);
        });
        try
        {
            var outputMode = TranslateRussianMode.IsChecked == true
                ? OutputMode.TranslateRussian
                : TranslateEnglishMode.IsChecked == true ? OutputMode.TranslateEnglish : OutputMode.Transcribe;
            _document = await _pipeline.RunAsync(_media, track, "auto", outputMode, progress, _workCancellation.Token);
            SegmentsGrid.ItemsSource = _document.Segments;
            EmptyResultText.Visibility = _document.Segments.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            ResultHeading.Text = $"Результат · язык: {_document.DetectedLanguage}" + (_document.HasUncertainOverlap ? " · есть неуверенное наложение" : "");
            CopyButton.IsEnabled = SaveButton.IsEnabled = _document.Segments.Count > 0;
            _hasUnsavedResult = _document.Segments.Count > 0;
            StatusTitle.Text = "Готово";
            StatusDetail.Text = $"Сегментов: {_document.Segments.Count}. Имена говорящих можно изменить прямо в таблице.";
            WorkProgress.Value = 100;
        }
        catch (OperationCanceledException)
        {
            StatusTitle.Text = "Обработка отменена";
            StatusDetail.Text = "Дочерние процессы остановлены; временные данные очищаются.";
            WorkProgress.Value = 0;
        }
        catch (Exception ex)
        {
            StatusTitle.Text = "Обработка не завершена";
            StatusDetail.Text = ErrorPresenter.For(ex);
            MessageBox.Show(this, ErrorPresenter.For(ex), "SpeechRibbon", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _workCancellation.Dispose();
            _workCancellation = null;
            CancelButton.IsEnabled = false;
            SetBusy(false);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _workCancellation?.Cancel();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null) return;
        Clipboard.SetText(TranscriptExporter.ToText(_document));
        StatusDetail.Text = "Результат скопирован в буфер обмена.";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null) return;
        var dialog = new SaveFileDialog { Title = "Сохранить результат", Filter = "Текст (*.txt)|*.txt|SubRip (*.srt)|*.srt|WebVTT (*.vtt)|*.vtt", AddExtension = true, OverwritePrompt = true, FileName = "transcript.txt" };
        if (dialog.ShowDialog(this) != true) return;
        var text = Path.GetExtension(dialog.FileName).ToLowerInvariant() switch { ".srt" => TranscriptExporter.ToSrt(_document), ".vtt" => TranscriptExporter.ToVtt(_document), _ => TranscriptExporter.ToText(_document) };
        File.WriteAllText(dialog.FileName, text, new UTF8Encoding(false));
        _hasUnsavedResult = false;
        StatusDetail.Text = $"Сохранено: {dialog.FileName}";
    }

    private void SetBusy(bool busy, string? title = null, string? detail = null)
    {
        _isBusy = busy;
        ChooseFileButton.IsEnabled = !busy;
        StartButton.IsEnabled = !busy && _media is not null;
        TrackBox.IsEnabled = !busy && (_media?.AudioTracks.Count ?? 0) > 1;
        TranscribeMode.IsEnabled = TranslateRussianMode.IsEnabled = TranslateEnglishMode.IsEnabled = !busy;
        if (title is not null) StatusTitle.Text = title;
        if (detail is not null) StatusDetail.Text = detail;
    }

    private void SegmentsGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || e.Column.DisplayIndex != 1 || e.Row.Item is not TranscriptSegment segment) return;
        if (e.EditingElement is TextBox editor) segment.Speaker = SpeakerNames.Normalize(editor.Text);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) ToggleMaximize();
        else if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "Восстановить" : "Развернуть";
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_workCancellation is not null)
        {
            if (MessageBox.Show(this, "Обработка ещё идёт. Отменить её и закрыть программу?", "SpeechRibbon", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) { e.Cancel = true; return; }
            _workCancellation.Cancel();
        }
        if (_hasUnsavedResult && MessageBox.Show(this, "Результат не сохранён. Закрыть программу?", "SpeechRibbon", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) e.Cancel = true;
    }
}
