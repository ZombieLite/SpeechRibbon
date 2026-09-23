using System.IO;
using System.Reflection;
using System.Net;
using System.Net.Http;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Controls;
using SpeechRibbon;

internal static class AiUiTests
{
    public static void Run(Action<bool, string> check)
    {
        Exception? failure = null;
        var thread = new Thread(() => {
            var folder = Path.Combine(Path.GetTempPath(), "speechribbon-ui-" + Guid.NewGuid().ToString("N"));
            var previous = Environment.GetEnvironmentVariable("SPEECHRIBBON_BUNDLE_PATH");
            Directory.CreateDirectory(folder);
            try
            {
                Environment.SetEnvironmentVariable("SPEECHRIBBON_BUNDLE_PATH", Path.Combine(folder, "SpeechRibbon.exe"));
                var app = new App(); app.InitializeComponent();
                using var workspace = RuntimeWorkspace.CreateAsync(default).GetAwaiter().GetResult();
                var window = new MainWindow(workspace);
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                T Control<T>(string name) where T : class => (T)window.FindName(name);
                void Set(string name, object value) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
                void Call(string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window,
                    name.EndsWith("_Click") ? new object[] { window, new RoutedEventArgs() } : null);
                check(Control<RadioButton>("AiTab").Visibility == Visibility.Collapsed, "UI hides AI before configuration");
                Call("OpenConnection_Click");
                Control<TextBox>("ConnectionUrl").Text = "https://example.invalid/v1";
                Control<PasswordBox>("ConnectionKey").Password = "synthetic-key";
                Control<TextBox>("ConnectionModel").Text = "my-model";
                check(Control<PasswordBox>("ConnectionKey").PasswordChar == '*', "UI masks key");
                Call("CloseConnection_Click");
                check(!File.Exists(AiSettingsStore.DefaultPath), "UI cancel does not save settings");
                Call("OpenConnection_Click");
                check(Control<TextBox>("ConnectionModel").Text == "", "UI cancelled model is discarded");
                Control<TextBox>("ConnectionUrl").Text = "https://example.invalid/v1";
                Control<PasswordBox>("ConnectionKey").Password = "synthetic-key";
                Control<TextBox>("ConnectionModel").Text = "my-model";
                Call("SaveConnection_Click");
                check(new AiSettingsStore(AiSettingsStore.DefaultPath).Load().Model == "my-model", "UI saves beside outer executable");
                Control<RadioButton>("AiTab").IsChecked = true;
                check(Control<Grid>("AiContent").Visibility == Visibility.Collapsed, "UI no prompt/send without transcript");
                var doc = new TranscriptDocument(); doc.Segments.Add(new TranscriptSegment { Text = "Тестовый текст" });
                Set("_document", doc); Call("RenderAi");
                check(Control<Button>("SendPromptButton").IsEnabled && !Control<Button>("CopyButton").IsEnabled, "UI enables sending but not empty export");
                Set("_aiAnswer", "Сохранённый ответ"); Set("_editorOpen", false); Call("RenderAi");
                check(Control<Grid>("PromptEditor").Visibility == Visibility.Collapsed && Control<ScrollViewer>("AnswerScroller").Visibility == Visibility.Visible, "UI answer uses editor area");
                Call("EditPrompt_Click"); Control<TextBox>("PromptBox").Text = "Новый промпт";
                Call("CollapsePrompt_Click");
                check(Control<TextBlock>("AnswerText").Text == "Сохранённый ответ" && Control<TextBox>("PromptBox").Text == "Новый промпт", "UI returning preserves prompt and answer");
                window.Width = window.MinWidth; window.Height = window.MinHeight;
                var root = (FrameworkElement)window.Content;
                root.Measure(new Size(window.MinWidth, window.MinHeight)); root.Arrange(new Rect(0, 0, window.MinWidth, window.MinHeight)); root.UpdateLayout();
                check(Control<ScrollViewer>("AnswerScroller").ActualHeight > 160, "UI answer remains readable at minimum size");
                Call("EditPrompt_Click"); root.UpdateLayout();
                var send = Control<Button>("SendPromptButton");
                check(send.TransformToAncestor(root).Transform(new Point(0, send.ActualHeight)).Y <= root.ActualHeight, "UI send button stays within window");
                Call("OpenConnection_Click");
                var status = Control<TextBox>("ConnectionStatus");
                status.Text = "HTTP 422. " + new string('я', 1200);
                root.UpdateLayout();
                var save = Control<Button>("SaveConnectionButton");
                check(status.IsReadOnly && save.TransformToAncestor(root).Transform(new Point(0, save.ActualHeight)).Y <= root.ActualHeight,
                    "UI long copyable error keeps settings buttons visible");
                Call("CloseConnection_Click");
                Call("InvalidateAiDocument");
                check(!Control<Button>("CopyButton").IsEnabled, "UI document change clears old AI answer");
                var oldClient = (AiClient)typeof(MainWindow).GetField("_aiClient", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                oldClient.Dispose();
                var handler = new DelayedHandler(); var client = new AiClient(handler); Set("_aiClient", client);
                Call("SendPrompt_Click");
                check(handler.Calls == 1 && !Control<Button>("SendPromptButton").IsEnabled, "UI one active request");
                Call("SendPrompt_Click"); check(handler.Calls == 1, "UI duplicate send rejected");
                check(Control<Button>("SettingsButton").IsEnabled, "UI settings stay accessible during request");
                Call("InvalidateAiDocument"); Set("_document", new TranscriptDocument());
                handler.Response.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"Late answer\"}}]}") });
                Pump();
                check(Control<TextBlock>("AnswerText").Text == "", "UI late answer does not attach to another document");
                Set("_document", doc); Set("_aiAnswer", "Previous answer"); Call("RenderAi");
                handler.Response = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Call("SendPrompt_Click"); Call("CancelAi_Click");
                handler.Response.SetCanceled(); Pump();
                check(Control<TextBlock>("AnswerText").Text == "Previous answer" && Control<TextBox>("PromptBox").Text == "Новый промпт", "UI cancellation preserves previous answer and prompt");
                window.Close();
            }
            catch (Exception e) { failure = e; }
            finally { Environment.SetEnvironmentVariable("SPEECHRIBBON_BUNDLE_PATH", previous); Directory.Delete(folder, true); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        check(failure is null, "UI runtime scenario: " + failure?.GetBaseException().Message);
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    private sealed class DelayedHandler : HttpMessageHandler
    {
        public int Calls;
        public TaskCompletionSource<HttpResponseMessage> Response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Calls++; return Response.Task; }
    }
}
