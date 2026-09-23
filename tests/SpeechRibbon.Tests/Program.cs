using SpeechRibbon;

var failures = new List<string>();

void Check(bool condition, string message)
{
    if (!condition) failures.Add(message);
}

Check(WhisperLanguages.All.Count == 100, $"Expected auto + 99 languages, got {WhisperLanguages.All.Count}.");
Check(WhisperLanguages.All.Any(x => x.Key == "ru") && WhisperLanguages.All.Any(x => x.Key == "en"), "Russian and English languages are required.");

var document = new TranscriptDocument { DetectedLanguage = "ru" };
document.Segments.Add(new TranscriptSegment { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(1.25), Speaker = "Анна", Text = "Привет." });
document.Segments.Add(new TranscriptSegment { Start = TimeSpan.FromSeconds(1.25), End = TimeSpan.FromSeconds(2.5), Speaker = "Speaker 2", Text = "Здравствуйте.", IsUncertainOverlap = true });
var text = TranscriptExporter.ToText(document);
var srt = TranscriptExporter.ToSrt(document);
var vtt = TranscriptExporter.ToVtt(document);
Check(text.Contains("Анна: Привет."), "Renamed speaker must be exported to TXT.");
Check(text.Contains("неразборчивое наложение"), "Uncertain overlap must be explicit in TXT.");
Check(!text.Contains(Environment.NewLine + Environment.NewLine), "TXT must not insert blank lines between adjacent speakers.");
Check(srt.Contains("00:00:01,250 --> 00:00:02,500"), "SRT timestamps must use comma milliseconds.");
Check(vtt.StartsWith("WEBVTT\n\n") && vtt.Contains("<v Анна>"), "WebVTT must contain header and speaker voice span.");

var editableSpeaker = new TranscriptSegment { Speaker = "Иван Петров " };
Check(editableSpeaker.Speaker == "Иван Петров ", "Editing must preserve an internal and trailing space while the user is typing.");
Check(SpeakerNames.Normalize(editableSpeaker.Speaker) == "Иван Петров", "Speaker name must be trimmed only when editing is committed or exported.");

var credibleRussian = new TranscriptDocument { DetectedLanguage = "ru" };
credibleRussian.Segments.Add(new TranscriptSegment { Text = "Добрый день, начинаем встречу." });
Check(TranscriptionPipeline.IsCrediblyRussian(credibleRussian), "Genuine Cyrillic Russian text may bypass the translation round trip.");

var misdetectedJapanese = new TranscriptDocument { DetectedLanguage = "ru" };
misdetectedJapanese.Segments.Add(new TranscriptSegment { Text = "Kakechigai no kisetsu wa meguru" });
misdetectedJapanese.Segments.Add(new TranscriptSegment { Text = "Yosoku dekinai shinji rarenai" });
Check(!TranscriptionPipeline.IsCrediblyRussian(misdetectedJapanese), "Japanese romanization mislabeled as ru must still enter the Russian translation path.");

var mojibakeRussian = new TranscriptDocument { DetectedLanguage = "ru" };
mojibakeRussian.Segments.Add(new TranscriptSegment { Text = "РљР°Рє Р±СѓРґС‚Рѕ С‚РµРєСЃС‚" });
Check(!TranscriptionPipeline.IsCrediblyRussian(mojibakeRussian), "Mojibake mislabeled as ru must not be accepted as a Russian result.");

var japaneseText = new TranscriptDocument { DetectedLanguage = "ja" };
japaneseText.Segments.Add(new TranscriptSegment { Text = "予測できない、信じられない。" });
Check(TranscriptionPipeline.HasSubstantialJapaneseText(japaneseText), "Japanese script must select the dedicated Japanese-to-English translation path.");

var shortRepeatedHallucination = new TranscriptDocument { DetectedLanguage = "pl" };
shortRepeatedHallucination.Segments.Add(new TranscriptSegment { Text = "Da, da, da, da, da, da, da." });
Check(TranscriptionPipeline.IsLikelyShortRepetitionHallucination(shortRepeatedHallucination, TimeSpan.FromSeconds(2.24)),
    "A short periodic Whisper output on non-speech must be rejected as a hallucination.");
var shortRealSpeech = new TranscriptDocument { DetectedLanguage = "ru" };
shortRealSpeech.Segments.Add(new TranscriptSegment { Text = "Добрый день, меня слышно?" });
Check(!TranscriptionPipeline.IsLikelyShortRepetitionHallucination(shortRealSpeech, TimeSpan.FromSeconds(2.5)),
    "Short real speech must not be rejected merely because it is quiet or brief.");
Check(!TranscriptionPipeline.IsLikelyShortRepetitionHallucination(shortRepeatedHallucination, TimeSpan.FromSeconds(4)),
    "The conservative repetition guard must be limited to very short media.");

var sparseVadSong = new TranscriptDocument { DetectedLanguage = "ja" };
Check(TranscriptionPipeline.ShouldRetryWithoutVad(sparseVadSong, TimeSpan.FromMinutes(3)),
    "A long track rejected almost entirely by VAD must receive a vocal fallback pass.");
Check(!TranscriptionPipeline.ShouldRetryWithoutVad(shortRealSpeech, TimeSpan.FromSeconds(2.5)),
    "Short media must not trigger the expensive vocal fallback.");
var credibleVocal = new TranscriptDocument { DetectedLanguage = "ja" };
credibleVocal.Segments.Add(new TranscriptSegment { Start = TimeSpan.FromSeconds(17), End = TimeSpan.FromSeconds(22), Text = "予測できない 信じられない" });
credibleVocal.Segments.Add(new TranscriptSegment { Start = TimeSpan.FromSeconds(22), End = TimeSpan.FromSeconds(27), Text = "駆け違いの 季節は巡る" });
credibleVocal.Segments.Add(new TranscriptSegment { Start = TimeSpan.FromSeconds(27), End = TimeSpan.FromSeconds(33), Text = "誠実な君は 世界の真ん中" });
Check(TranscriptionPipeline.IsCredibleVocalFallback(credibleVocal, TimeSpan.FromMinutes(3.4)),
    "Several varied sustained lyric segments must be accepted as a credible vocal fallback.");
var repeatedCredits = new TranscriptDocument { DetectedLanguage = "ru" };
for (var index = 0; index < 8; index++)
    repeatedCredits.Segments.Add(new TranscriptSegment { Start = TimeSpan.FromSeconds(index * 2), End = TimeSpan.FromSeconds(index * 2 + 2), Text = index % 2 == 0 ? "Редактор субтитров Н. Закомодина" : "Корректор В. Сухашвили" });
Check(!TranscriptionPipeline.IsCredibleVocalFallback(repeatedCredits, TimeSpan.FromMinutes(3)),
    "Alternating repeated subtitle credits must not be accepted as a vocal fallback.");

var translatorProcess = RuntimeWorkspace.CreateProcessStartInfo(Path.Combine(Path.GetTempPath(), "speechribbon-encoding-probe.exe"), true);
Check(translatorProcess.StandardInputEncoding?.CodePage == System.Text.Encoding.UTF8.CodePage,
    "Translator stdin must always use UTF-8, independent of the Windows system code page.");
Check(translatorProcess.StandardOutputEncoding?.CodePage == System.Text.Encoding.UTF8.CodePage,
    "Translator stdout must always be decoded as UTF-8 in a windowed build without a console.");
Check(translatorProcess.StandardErrorEncoding?.CodePage == System.Text.Encoding.UTF8.CodePage,
    "Internal process diagnostics must always be decoded as UTF-8.");
var probeProcess = RuntimeWorkspace.CreateProcessStartInfo(Path.Combine(Path.GetTempPath(), "speechribbon-encoding-probe.exe"), false);
Check(probeProcess.StandardInputEncoding is null,
    "An internal process without redirected stdin must not receive an unsupported StandardInputEncoding setting.");

var overlapFixture = Path.Combine(Path.GetTempPath(), $"speechribbon-overlap-{Guid.NewGuid():N}.wav");
try
{
    WriteOverlapFixture(overlapFixture);
    var overlapSegments = new List<TranscriptSegment>
    {
        new() { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(1), Text = "Первый" },
        new() { Start = TimeSpan.FromSeconds(1), End = TimeSpan.FromSeconds(2), Text = "Вместе" },
        new() { Start = TimeSpan.FromSeconds(2), End = TimeSpan.FromSeconds(3), Text = "Второй" }
    };
    SpeakerAnalyzer.AssignSpeakersAndOverlap(overlapFixture, overlapSegments);
    Check(overlapSegments[1].IsUncertainOverlap, "A strong two-band overlap must be marked uncertain.");
    Check(overlapSegments[1].Speaker == "Speaker 1 + Speaker 2", "Unresolved overlap must be shown for both speakers.");
}
finally { if (File.Exists(overlapFixture)) File.Delete(overlapFixture); }

var unsupported = ErrorPresenter.For(new SpeechRibbonException("UNSUPPORTED_INPUT", "Формат файла не входит в поддерживаемый список."));
Check(unsupported.Contains("Формат файла"), "Known failures must remain understandable.");

var bundleArgument = Array.FindIndex(args, value => value.Equals("--bundle", StringComparison.OrdinalIgnoreCase));
if (bundleArgument >= 0)
{
    Check(bundleArgument + 1 < args.Length, "--bundle must be followed by a package path.");
    if (bundleArgument + 1 < args.Length)
    {
        var packagePath = Path.GetFullPath(args[bundleArgument + 1]);
        Environment.SetEnvironmentVariable("SPEECHRIBBON_BUNDLE_PATH", packagePath);
        var productRoot = FindProductRoot();
        var assets = new[]
        {
            ("SpeechRibbon.Assets.whisper.zip", "whisper-bin-x64-v1.9.2.zip", Path.Combine(productRoot, "third_party", "bundled", "whisper-bin-x64-v1.9.2.zip")),
            ("SpeechRibbon.Assets.model.bin", "ggml-small-q8_0.bin", Path.Combine(productRoot, "third_party", "bundled", "ggml-small-q8_0.bin")),
            ("SpeechRibbon.Assets.vad.bin", "ggml-silero-v6.2.0.bin", Path.Combine(productRoot, "third_party", "bundled", "ggml-silero-v6.2.0.bin")),
            ("SpeechRibbon.Assets.ffmpeg.zip", "ffmpeg-9.0.1-speechribbon-decoder.zip", Path.Combine(productRoot, "third_party", "bundled", "ffmpeg-9.0.1-speechribbon-decoder.zip")),
            ("SpeechRibbon.Assets.translator.zip", "bergamot-enru.zip", Path.Combine(productRoot, "third_party", "bundled", "bergamot-enru.zip")),
            ("SpeechRibbon.Assets.translator.jaen.zip", "bergamot-jaen.zip", Path.Combine(productRoot, "third_party", "bundled", "bergamot-jaen.zip")),
            ("SpeechRibbon.Assets.sources.zip", "third-party-sources.zip", Path.Combine(productRoot, "third_party", "bundled", "third-party-sources.zip")),
            ("SpeechRibbon.Assets.notices.txt", "THIRD-PARTY-NOTICES.txt", Path.Combine(productRoot, "third_party", "THIRD-PARTY-NOTICES.txt"))
        };
        foreach (var asset in assets)
        {
            await using var expected = File.OpenRead(asset.Item3);
            await using var actual = AssetStore.Open(asset.Item1, asset.Item2);
            var expectedHash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(expected));
            var actualHash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(actual));
            Check(actualHash == expectedHash, $"Bundled asset differs from source: {asset.Item2}.");
        }
    }
}

if (args.Contains("--integration", StringComparer.OrdinalIgnoreCase))
{
    var productRoot = FindProductRoot();
    var input = Path.Combine(productRoot, "tools", "whisper-src", "whisper.cpp-1.9.2", "samples", "jfk.wav");
    var workspace = await RuntimeWorkspace.CreateAsync(CancellationToken.None);
    var temporaryRoot = workspace.Root;
    try
    {
        var pipeline = new TranscriptionPipeline(workspace);
        var media = await pipeline.InspectAsync(input, CancellationToken.None);
        Check(media.AudioTracks.Count == 1 && media.Duration.TotalSeconds is > 10 and < 12, "Custom FFmpeg must probe the fixture.");
        var result = await pipeline.RunAsync(media, media.AudioTracks[0], "en", OutputMode.Transcribe, new Progress<WorkProgress>(), CancellationToken.None);
        Check(result.Segments.Count > 0 && result.Segments[0].Text.Contains("country", StringComparison.OrdinalIgnoreCase), "Whisper small-q8_0 must transcribe the fixture.");
        Check(result.Segments.Max(segment => segment.End) > TimeSpan.FromSeconds(10), "Whisper JSON timestamps with comma milliseconds must be parsed across VAD-split segments.");
        var russian = await pipeline.RunAsync(media, media.AudioTracks[0], "en", OutputMode.TranslateRussian, new Progress<WorkProgress>(), CancellationToken.None);
        Check(russian.DetectedLanguage == "ru" && russian.Segments.Any(segment => segment.Text.Any(character => character is >= 'А' and <= 'я')),
            "English speech must be translated to Russian by the bundled offline model.");
    }
    finally { workspace.Dispose(); }
    Check(!Directory.Exists(temporaryRoot), "Owned temporary workspace must be deleted after disposal.");
}

var russianMediaArgument = Array.FindIndex(args, value => value.Equals("--translate-russian-media", StringComparison.OrdinalIgnoreCase));
if (russianMediaArgument >= 0)
{
    Check(russianMediaArgument + 1 < args.Length, "--translate-russian-media must be followed by a local media path.");
    if (russianMediaArgument + 1 < args.Length)
    {
        var input = Path.GetFullPath(args[russianMediaArgument + 1]);
        var sourceLanguageArgument = Array.FindIndex(args, value => value.Equals("--source-language", StringComparison.OrdinalIgnoreCase));
        var sourceLanguage = sourceLanguageArgument >= 0 && sourceLanguageArgument + 1 < args.Length ? args[sourceLanguageArgument + 1] : "auto";
        var workspace = await RuntimeWorkspace.CreateAsync(CancellationToken.None);
        var temporaryRoot = workspace.Root;
        try
        {
            var pipeline = new TranscriptionPipeline(workspace);
            var media = await pipeline.InspectAsync(input, CancellationToken.None);
            var result = await pipeline.RunAsync(media, media.AudioTracks[0], sourceLanguage, OutputMode.TranslateRussian, new Progress<WorkProgress>(), CancellationToken.None);
            var resultText = string.Join(' ', result.Segments.Select(segment => segment.Text));
            var letters = resultText.Count(char.IsLetter);
            var cyrillic = resultText.Count(character => character is >= '\u0400' and <= '\u052F');
            Check(result.DetectedLanguage == "ru", "Russian media translation must label the result as ru.");
            Check(letters >= 4 && cyrillic * 100 >= letters * 60, "Russian media translation must return predominantly Cyrillic text.");
            Check(!resultText.Contains("РљР", StringComparison.Ordinal) && !resultText.Contains("РµР", StringComparison.Ordinal), "Russian media translation must not return common UTF-8 mojibake markers.");
            Console.WriteLine($"MEDIA_TRANSLATION: segments={result.Segments.Count}; text={resultText}");
        }
        finally { workspace.Dispose(); }
        Check(!Directory.Exists(temporaryRoot), "Owned temporary workspace must be deleted after media translation.");
    }
}

var noSpeechMediaArgument = Array.FindIndex(args, value => value.Equals("--expect-no-speech-media", StringComparison.OrdinalIgnoreCase));
if (noSpeechMediaArgument >= 0)
{
    Check(noSpeechMediaArgument + 1 < args.Length, "--expect-no-speech-media must be followed by a local media path.");
    if (noSpeechMediaArgument + 1 < args.Length)
    {
        var input = Path.GetFullPath(args[noSpeechMediaArgument + 1]);
        var workspace = await RuntimeWorkspace.CreateAsync(CancellationToken.None);
        var temporaryRoot = workspace.Root;
        try
        {
            var pipeline = new TranscriptionPipeline(workspace);
            var media = await pipeline.InspectAsync(input, CancellationToken.None);
            try
            {
                await pipeline.RunAsync(media, media.AudioTracks[0], "auto", OutputMode.Transcribe, new Progress<WorkProgress>(), CancellationToken.None);
                failures.Add("Non-speech media must not produce a transcript.");
            }
            catch (SpeechRibbonException exception) when (exception.Code == "NO_SPEECH")
            {
                Console.WriteLine("NO_SPEECH_MEDIA: correctly rejected without invented text.");
            }
        }
        finally { workspace.Dispose(); }
        Check(!Directory.Exists(temporaryRoot), "No-speech validation must delete its owned workspace on disposal.");
    }
}

var quietTailMediaArgument = Array.FindIndex(args, value => value.Equals("--verify-quiet-tail-media", StringComparison.OrdinalIgnoreCase));
if (quietTailMediaArgument >= 0)
{
    Check(quietTailMediaArgument + 1 < args.Length, "--verify-quiet-tail-media must be followed by a local media path.");
    if (quietTailMediaArgument + 1 < args.Length)
    {
        var input = Path.GetFullPath(args[quietTailMediaArgument + 1]);
        var workspace = await RuntimeWorkspace.CreateAsync(CancellationToken.None);
        var temporaryRoot = workspace.Root;
        try
        {
            var pipeline = new TranscriptionPipeline(workspace);
            var media = await pipeline.InspectAsync(input, CancellationToken.None);
            var result = await pipeline.RunAsync(media, media.AudioTracks[0], "auto", OutputMode.Transcribe, new Progress<WorkProgress>(), CancellationToken.None);
            var lastEnd = result.Segments.Count == 0 ? TimeSpan.Zero : result.Segments.Max(segment => segment.End);
            Check(result.Segments.Count > 0, "The quiet recording must retain real speech segments.");
            Check(lastEnd >= TimeSpan.FromMinutes(28), $"Quiet speech was cut too early at {lastEnd}.");
            Check(lastEnd <= TimeSpan.FromMinutes(30), $"The silent tail produced text through {lastEnd}.");
            Console.WriteLine($"QUIET_TAIL_MEDIA: segments={result.Segments.Count}; lastEnd={lastEnd:c}; media={media.Duration:c}.");
        }
        finally { workspace.Dispose(); }
        Check(!Directory.Exists(temporaryRoot), "Quiet-tail validation must delete its owned workspace on disposal.");
    }
}

if (args.Contains("--performance", StringComparer.OrdinalIgnoreCase))
{
    var productRoot = FindProductRoot();
    var source = Path.Combine(productRoot, "tests", "fixtures", "jfk-16k.wav");
    var input = Path.Combine(Path.GetTempPath(), $"speechribbon-60s-{Guid.NewGuid():N}.wav");
    WriteRepeatedPcmFixture(source, input, 60);
    var workspace = await RuntimeWorkspace.CreateAsync(CancellationToken.None);
    var temporaryRoot = workspace.Root;
    try
    {
        var pipeline = new TranscriptionPipeline(workspace);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var media = await pipeline.InspectAsync(input, CancellationToken.None);
        var work = pipeline.RunAsync(media, media.AudioTracks[0], "en", OutputMode.Transcribe, new Progress<WorkProgress>(), CancellationToken.None);
        long peakWorkingSet = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64;
        while (!work.IsCompleted)
        {
            long workingSet = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64;
            foreach (var processName in new[] { "ffmpeg", "ffprobe", "whisper-cli" })
            {
                foreach (var process in System.Diagnostics.Process.GetProcessesByName(processName))
                {
                    try { workingSet += process.WorkingSet64; }
                    finally { process.Dispose(); }
                }
            }
            peakWorkingSet = Math.Max(peakWorkingSet, workingSet);
            await Task.Delay(50);
        }
        var result = await work;
        clock.Stop();
        Check(result.Segments.Count > 0, "The 60-second fixture must complete the full transcription path.");
        Console.WriteLine($"MEASURE: duration=60.0s elapsed={clock.Elapsed.TotalSeconds:F3}s speed={60 / clock.Elapsed.TotalSeconds:F2}x peakWorkingSetMiB={peakWorkingSet / 1048576d:F1}.");
    }
    finally
    {
        workspace.Dispose();
        if (File.Exists(input)) File.Delete(input);
    }
    Check(!Directory.Exists(temporaryRoot), "Performance work must delete its owned workspace on disposal.");
}

if (args.Contains("--cancellation", StringComparer.OrdinalIgnoreCase))
{
    var productRoot = FindProductRoot();
    var input = Path.Combine(productRoot, "tools", "whisper-src", "whisper.cpp-1.9.2", "samples", "jfk.wav");
    var workspace = await RuntimeWorkspace.CreateAsync(CancellationToken.None);
    var temporaryRoot = workspace.Root;
    try
    {
        var pipeline = new TranscriptionPipeline(workspace);
        var media = await pipeline.InspectAsync(input, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var work = pipeline.RunAsync(media, media.AudioTracks[0], "en", OutputMode.Transcribe, new Progress<WorkProgress>(), cancellation.Token);
        await Task.Delay(700);
        var cancelClock = System.Diagnostics.Stopwatch.StartNew();
        cancellation.Cancel();
        try { await work; failures.Add("Cancellation should stop the pipeline."); }
        catch (OperationCanceledException) { }
        Check(cancelClock.Elapsed < TimeSpan.FromSeconds(2), $"Cancellation took {cancelClock.Elapsed.TotalSeconds:F2}s.");
    }
    finally { workspace.Dispose(); }
    Check(!Directory.Exists(temporaryRoot), "Cancelled work must delete its owned workspace on disposal.");
}

await AiTests.Run(Check);
if (args.Contains("--ui")) AiUiTests.Run(Check);

if (failures.Count > 0)
{
    foreach (var failure in failures) Console.Error.WriteLine("FAIL: " + failure);
    return 1;
}

var passedScopes = new List<string> { "unit behavior" };
if (bundleArgument >= 0) passedScopes.Add("all packaged asset hashes");
if (args.Contains("--integration", StringComparer.OrdinalIgnoreCase)) passedScopes.Add("FFmpeg + Whisper transcription + offline Russian translation");
if (args.Contains("--performance", StringComparer.OrdinalIgnoreCase)) passedScopes.Add("one 60-second measurement");
if (args.Contains("--cancellation", StringComparer.OrdinalIgnoreCase)) passedScopes.Add("bounded cancellation cleanup");
if (noSpeechMediaArgument >= 0) passedScopes.Add("short non-speech hallucination rejection");
if (quietTailMediaArgument >= 0) passedScopes.Add("quiet speech preservation and silent-tail cutoff");
Console.WriteLine("PASS: " + string.Join(", ", passedScopes) + ".");
return 0;

static void WriteOverlapFixture(string path)
{
    const int rate = 16000;
    var samples = new short[rate * 3];
    for (var i = 0; i < samples.Length; i++)
    {
        var second = i / rate;
        var t = i / (double)rate;
        var value = second switch
        {
            0 => Math.Sin(2 * Math.PI * 190 * t) * 4500,
            1 => Math.Sin(2 * Math.PI * 210 * t) * 13000 + Math.Sin(2 * Math.PI * 1350 * t) * 13000,
            _ => Math.Sin(2 * Math.PI * 1250 * t) * 4500
        };
        samples[i] = (short)Math.Clamp(value, short.MinValue, short.MaxValue);
    }
    using var writer = new BinaryWriter(File.Create(path));
    writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
    writer.Write(36 + samples.Length * 2);
    writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
    writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate);
    writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
    writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
    writer.Write(samples.Length * 2);
    foreach (var sample in samples) writer.Write(sample);
}

static string FindProductRoot()
{
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current is not null)
    {
        if (File.Exists(Path.Combine(current.FullName, "Directory.Build.props"))
            && Directory.Exists(Path.Combine(current.FullName, "third_party"))) return current.FullName;
        current = current.Parent;
    }
    throw new DirectoryNotFoundException("SpeechRibbon product root was not found from the test output directory.");
}

static void WriteRepeatedPcmFixture(string sourcePath, string destinationPath, int seconds)
{
    using var source = new BinaryReader(File.OpenRead(sourcePath));
    if (new string(source.ReadChars(4)) != "RIFF") throw new InvalidDataException("Fixture is not RIFF.");
    source.ReadInt32();
    if (new string(source.ReadChars(4)) != "WAVE") throw new InvalidDataException("Fixture is not WAVE.");
    short format = 0, channels = 0, bits = 0;
    int rate = 0;
    byte[]? pcm = null;
    while (source.BaseStream.Position + 8 <= source.BaseStream.Length)
    {
        var chunk = new string(source.ReadChars(4));
        var length = source.ReadInt32();
        if (chunk == "fmt ")
        {
            format = source.ReadInt16(); channels = source.ReadInt16(); rate = source.ReadInt32();
            source.ReadInt32(); source.ReadInt16(); bits = source.ReadInt16();
            if (length > 16) source.ReadBytes(length - 16);
        }
        else if (chunk == "data") pcm = source.ReadBytes(length);
        else source.ReadBytes(length);
        if ((length & 1) != 0 && source.BaseStream.Position < source.BaseStream.Length) source.ReadByte();
    }
    if (format != 1 || channels != 1 || rate != 16000 || bits != 16 || pcm is null || pcm.Length == 0)
        throw new InvalidDataException("Performance fixture must be PCM 16 kHz mono 16-bit.");
    var targetBytes = checked(seconds * rate * 2);
    using var writer = new BinaryWriter(File.Create(destinationPath));
    writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + targetBytes);
    writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
    writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
    writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(targetBytes);
    for (var written = 0; written < targetBytes;)
    {
        var count = Math.Min(pcm.Length, targetBytes - written);
        writer.Write(pcm, 0, count);
        written += count;
    }
}

