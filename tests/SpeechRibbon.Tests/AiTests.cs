using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using SpeechRibbon;

internal static class AiTests
{
    public static async Task Run(Action<bool, string> check)
    {
        await Loopback(check);
        await TlsLoopback(check);
        var settings = new AiSettings { BaseUrl = "https://example.invalid/v1", ApiKey = "synthetic-secret", Model = "test-model" };
        // Reproduce a provider whose omitted output budget consumes the whole context.
        var contextHandler = new Handler(async (request, token) => {
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var budget = payload.RootElement.TryGetProperty("max_tokens", out var value) ? value.GetInt32() : 262144;
            return budget + 100 > 262144
                ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":{\"message\":\"Output reservation leaves no room for input\"}}") }
                : Json("{\"choices\":[{\"message\":{\"content\":\"Accepted\"}}]}");
        });
        using (var oldRequest = new HttpClient(contextHandler, disposeHandler: false))
        using (var response = await oldRequest.PostAsync(settings.Endpoint(), new StringContent("{\"model\":\"test-model\",\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}")))
            check(response.StatusCode == HttpStatusCode.BadRequest, "AI regression reproduces full-context default rejection");
        using (var fixedClient = new AiClient(contextHandler))
        {
            check(await fixedClient.CompleteAsync(settings, "", "", default, true) == "Accepted", "AI probe succeeds against full-context default provider");
            check(await fixedClient.CompleteAsync(settings, "Summarize", "Short transcript", default) == "Accepted", "AI processing succeeds against full-context default provider");
        }
        check(settings.Endpoint().AbsoluteUri == "https://example.invalid/v1/chat/completions", "AI base URL preserves v1 without duplication");
        settings.BaseUrl = "https://example.invalid";
        check(settings.Endpoint().AbsolutePath == "/v1/chat/completions", "AI root URL defaults to v1");
        settings.BaseUrl = "https://example.invalid/custom/v1/";
        check(settings.Endpoint().AbsolutePath == "/custom/v1/chat/completions", "AI custom API prefix preserved");
        using (var transport = AiClient.CreateHandler())
            check(!transport.AllowAutoRedirect, "AI certificate bypass does not enable redirects");
        foreach (var sample in new[] {
            (400, "{\"error\":{\"message\":\"Unknown model test-model\"}}", "Unknown model test-model"),
            (422, "{\"detail\":[{\"msg\":\"Field required: messages\",\"input\":\"PRIVATE_SOURCE\"}]}", "Field required: messages"),
            (400, "{\"error\":{\"message\":\"synthetic-secret PRIVATE_SOURCE PRIVATE_PROMPT\"}}", "[скрыто]"),
            (502, "<html>PRIVATE_SOURCE</html>", "JSON"),
            (400, "{\"error\":{\"message\":\"" + new string('z', 17000) + "\"}}", "JSON")
        })
        {
            using var client = new AiClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)sample.Item1) { Content = new StringContent(sample.Item2) })));
            try { await client.CompleteAsync(settings, "PRIVATE_PROMPT", "PRIVATE_SOURCE", default); check(false, "AI error expected"); }
            catch (AiConnectionException e) {
                check(e.Message.Contains("HTTP " + sample.Item1) && e.Message.Contains(sample.Item3), "AI HTTP status and structured server detail");
                check(!e.Message.Contains(settings.ApiKey) && !e.Message.Contains("PRIVATE_SOURCE") && !e.Message.Contains("PRIVATE_PROMPT"), "AI error details redact request data");
            }
        }
        using (var client = new AiClient(new Handler((_, _) => throw new HttpRequestException(HttpRequestError.SecureConnectionError, "private certificate detail"))))
        {
            try { await client.CompleteAsync(settings, "", "", default); check(false, "AI TLS error expected"); }
            catch (AiConnectionException e) { check(e.Message.Contains("TLS") && !e.Message.Contains("private"), "AI TLS failure distinguished from HTTP refusal"); }
        }
        var longText = "Начало " + new string('я', 100000) + " КОНЕЦ";
        using (var client = new AiClient(new Handler(async (request, token) => {
            check(request.Method == HttpMethod.Post && request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == settings.ApiKey, "AI authorization and method");
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var input = json.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            check(input.EndsWith(longText) && input.StartsWith("Задание"), "AI full transcript and prompt sent without truncation");
            check(json.RootElement.GetProperty("model").GetString() == "test-model" && !json.RootElement.GetProperty("stream").GetBoolean(), "AI configured model and nonstream response");
            check(json.RootElement.GetProperty("max_tokens").GetInt32() == 8192, "AI output has an explicit budget while full input is preserved");
            return Json("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"Сводка\"}}]}");
        }))) check(await client.CompleteAsync(settings, "Задание", longText, default) == "Сводка", "AI parses successful response");
        foreach (var code in new[] { 400, 401, 403, 404, 413, 429, 500, 302 })
        {
            using var client = new AiClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent(settings.ApiKey) })));
            try { await client.CompleteAsync(settings, "x", "y", default); check(false, "AI rejects HTTP " + code); }
            catch (AiConnectionException e) { check(!e.Message.Contains(settings.ApiKey), "AI sanitized error " + code); }
        }
        foreach (var body in new[] { "not json", "{}", "{\"choices\":[]}", "{\"choices\":[{\"message\":{\"content\":\"\"}}]}", "{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"partial\"}}]}" })
        {
            using var client = new AiClient(new Handler((_, _) => Task.FromResult(Json(body))));
            try { await client.CompleteAsync(settings, "x", "y", default); check(false, "AI rejects invalid or partial response"); }
            catch (AiConnectionException) { check(true, "AI invalid response handled"); }
        }
        using (var cancel = new CancellationTokenSource())
        using (var client = new AiClient(new Handler(async (_, token) => { cancel.Cancel(); await Task.Delay(10000, token); return Json("{}"); })))
        {
            try { await client.CompleteAsync(settings, "x", "y", cancel.Token); check(false, "AI cancellation"); }
            catch (OperationCanceledException) { check(true, "AI cancellation reaches transport"); }
        }
        using (var client = new AiClient(new Handler(async (r, t) => {
            var input = await r.Content!.ReadAsStringAsync(t);
            check(!input.Contains("PRIVATE_TRANSCRIPT"), "AI connection test does not send transcript");
            using var json = JsonDocument.Parse(input);
            check(json.RootElement.GetProperty("max_tokens").GetInt32() == 512, "AI probe reserves only a small output budget");
            return Json("{\"choices\":[{\"message\":{\"content\":\"готово\"}}]}");
        }))) await client.CompleteAsync(settings, "private prompt", "PRIVATE_TRANSCRIPT", default, true);
        var folder = Path.Combine(Path.GetTempPath(), "speechribbon-ai-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try {
            var path = Path.Combine(folder, "settings.json"); var store = new AiSettingsStore(path);
            check(!store.Load().IsComplete, "AI absent settings are empty");
            store.Save(settings); var loaded = store.Load();
            check(loaded.Model == settings.Model && loaded.ApiKey == settings.ApiKey && loaded.BaseUrl == settings.BaseUrl, "AI settings survive reload");
            check(Directory.GetFiles(folder).Length == 1, "AI atomic save leaves no temporary secret file");
            File.WriteAllText(path, "broken");
            try { store.Load(); check(false, "AI corrupt settings"); } catch (AiConnectionException) { check(true, "AI corrupt settings handled"); }
        } finally { Directory.Delete(folder, true); }
    }
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private static async Task TlsLoopback(Action<bool, string> check)
    {
        using var key = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=wrong-host.invalid", key, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(-1));
        using var certificate = new System.Security.Cryptography.X509Certificates.X509Certificate2(generated.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx));
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var server = Task.Run(async () => {
            using var socket = await listener.AcceptTcpClientAsync(deadline.Token);
            await using var stream = new System.Net.Security.SslStream(socket.GetStream());
            await stream.AuthenticateAsServerAsync(new System.Net.Security.SslServerAuthenticationOptions { ServerCertificate = certificate }, deadline.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            var first = await reader.ReadLineAsync(deadline.Token);
            int size = 0; string? line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(deadline.Token)))
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) size = int.Parse(line.Split(':')[1]);
            var body = new char[size]; await reader.ReadBlockAsync(body.AsMemory(), deadline.Token);
            var response = Encoding.UTF8.GetBytes("{\"choices\":[{\"message\":{\"content\":\"TLS OK\"}}]}");
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {response.Length}\r\nConnection: close\r\n\r\n"), deadline.Token);
            await stream.WriteAsync(response, deadline.Token);
            return first == "POST /v1/chat/completions HTTP/1.1";
        });
        using var client = new AiClient();
        var settings = new AiSettings { BaseUrl = "https://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port, ApiKey = "synthetic", Model = "user-model" };
        check(await client.CompleteAsync(settings, "", "", deadline.Token, true) == "TLS OK" && await server, "AI accepts untrusted expired wrong-host certificate without system changes");
    }
    private static async Task Loopback(Action<bool, string> check)
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Task.Run(async () => {
            using var socket = await listener.AcceptTcpClientAsync(deadline.Token);
            await using var stream = socket.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            var start = await reader.ReadLineAsync(deadline.Token);
            var headers = new List<string>(); string? line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(deadline.Token))) headers.Add(line);
            var size = int.Parse(headers.Single(x => x.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1]);
            var body = new char[size];
            if (await reader.ReadBlockAsync(body.AsMemory(), deadline.Token) != size) throw new EndOfStreamException();
            using var json = JsonDocument.Parse(new string(body));
            var valid = start == "POST /v1/chat/completions HTTP/1.1" &&
                headers.Contains("Authorization: Bearer loopback-test") &&
                json.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!.EndsWith("Full transcript tail");
            var response = Encoding.UTF8.GetBytes("{\"choices\":[{\"message\":{\"content\":\"Loopback OK\"}}]}");
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {response.Length}\r\nConnection: close\r\n\r\n"), deadline.Token);
            await stream.WriteAsync(response, deadline.Token);
            return valid;
        });
        using var client = new AiClient();
        var result = await client.CompleteAsync(new AiSettings { BaseUrl = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port, ApiKey = "loopback-test", Model = "local-test" }, "Summarize", "Full transcript tail", deadline.Token);
        check(result == "Loopback OK" && await server, "AI real HTTP loopback request and response");
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken); }
}
