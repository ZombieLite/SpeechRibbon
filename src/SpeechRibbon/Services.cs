using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SpeechRibbon;

public sealed record MediaInfo(string Path, TimeSpan Duration, IReadOnlyList<AudioTrack> AudioTracks);

public sealed class SpeechRibbonException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

public static class ErrorPresenter
{
    public static string For(Exception exception)
    {
        if (exception is SpeechRibbonException known) return known.Message;
        if (exception is UnauthorizedAccessException) return "Нет доступа к выбранному файлу или папке.";
        if (exception is IOException) return "Не удалось прочитать или записать файл. Проверьте доступное место и права доступа.";
        return "Внутренний сбой. Исходный файл не изменён, частичный результат не выдан.";
    }

    public static string CodeFor(Exception exception) => exception switch
    {
        SpeechRibbonException known => known.Code,
        UnauthorizedAccessException => "ACCESS_DENIED",
        IOException => "IO_ERROR",
        _ => "INTERNAL_ERROR"
    };
}

public static class DiagnosticsReport
{
    public static string Create(string lastErrorCode)
    {
        var memoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var memoryMiB = memoryBytes > 0 ? memoryBytes / 1_048_576 : 0;
        return string.Join(Environment.NewLine,
        [
            "SpeechRibbon diagnostics",
            $"Version: {RuntimeWorkspace.Version}",
            $"OS: {Environment.OSVersion.VersionString}",
            $"Architecture: {RuntimeInformation.OSArchitecture}",
            $"Logical processors: {Environment.ProcessorCount}",
            $"AVX2: {Avx2.IsSupported}",
            $"Available memory estimate MiB: {memoryMiB}",
            $"Last error code: {lastErrorCode}",
            "FFmpeg package SHA256: 2BE0A9CCA855FDE41B7BDF21C6275DD2C60DF2A881907FD3B35953E83E50C83B",
            "Whisper package SHA256: 49DCC16DE826F20BD53D44F947A1AE49DFA81F86CAD67A64D80820CB192D674A",
            "Model SHA256: 49C8FB02B65E6049D5FA6C04F81F53B867B5EC9540406812C643F177317F779F",
            "Voice activity model SHA256: 2AA269B785EEB53A82983A20501DDF7C1D9C48E33AB63A41391AC6C9F7FB6987",
            "English-to-Russian translator SHA256: C5549053B97172135C0E516F2AC7494C34A0522F5EB20A53A04A3453622D0239",
            "Japanese-to-English translator SHA256: 03769F749AA5E03197326CA4D5B47A04E8824AD5BB9D3126A5CCFBCFA2B1A9C8",
            "Source paths, media names, recognized text and transcript content are intentionally excluded."
        ]);
    }
}

public sealed class RuntimeWorkspace : IDisposable
{
    public const string ProgramId = "speechribbon";
    public static string Version => typeof(RuntimeWorkspace).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    private readonly JobObject _job = new();
    private bool _disposed;
    public string Root { get; }
    public string FfmpegPath => Directory.EnumerateFiles(Root, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault() ?? "";
    public string FfprobePath => Directory.EnumerateFiles(Root, "ffprobe.exe", SearchOption.AllDirectories).FirstOrDefault() ?? "";
    public string WhisperPath => Directory.EnumerateFiles(Root, "whisper-cli.exe", SearchOption.AllDirectories).FirstOrDefault() ?? "";
    public string ModelPath => Path.Combine(Root, "ggml-small-q8_0.bin");
    public string VadModelPath => Path.Combine(Root, "ggml-silero-v6.2.0.bin");
    public string TranslatorPath => Directory.EnumerateFiles(Root, "bergamot.exe", SearchOption.AllDirectories).FirstOrDefault() ?? "";
    public string TranslatorConfigPath => Directory.EnumerateFiles(Root, "bergamot-enru.yml", SearchOption.AllDirectories).FirstOrDefault() ?? "";
    public string JapaneseTranslatorConfigPath => Directory.EnumerateFiles(Root, "bergamot-jaen.yml", SearchOption.AllDirectories).FirstOrDefault() ?? "";

    private RuntimeWorkspace(string root) => Root = root;

    public static Task<RuntimeWorkspace> CreateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Avx2.IsSupported)
            throw new SpeechRibbonException("AVX2_REQUIRED", "Этот компьютер не поддерживает AVX2, необходимый для локального распознавания.");
        var root = Path.Combine(Path.GetTempPath(), ProgramId, Version, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return Task.FromResult(new RuntimeWorkspace(root));
    }

    public static void CleanupStaleRuns()
    {
        var versionRoot = Path.Combine(Path.GetTempPath(), ProgramId, Version);
        if (!Directory.Exists(versionRoot)) return;
        foreach (var directory in Directory.EnumerateDirectories(versionRoot))
        {
            if (Path.GetFileName(directory).StartsWith("launcher-", StringComparison.OrdinalIgnoreCase)) continue;
            try { Directory.Delete(directory, true); } catch { /* An active or OS-locked run is never forced. */ }
        }
    }

    public async Task EnsureFfmpegAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(FfmpegPath) && File.Exists(FfprobePath)) return;
        var zip = Path.Combine(Root, "ffmpeg.zip");
        await AssetStore.WriteVerifiedAsync("SpeechRibbon.Assets.ffmpeg.zip", "ffmpeg-9.0.1-speechribbon-decoder.zip", zip,
            "2BE0A9CCA855FDE41B7BDF21C6275DD2C60DF2A881907FD3B35953E83E50C83B", cancellationToken);
        ZipFile.ExtractToDirectory(zip, Root, true);
        File.Delete(zip);
        if (!File.Exists(FfmpegPath) || !File.Exists(FfprobePath)) throw new SpeechRibbonException("INTERNAL_COMPONENT_CORRUPT", "Внутренний FFmpeg повреждён или не найден.");
    }

    public async Task EnsureWhisperAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(WhisperPath))
        {
            var zip = Path.Combine(Root, "whisper.zip");
            await AssetStore.WriteVerifiedAsync("SpeechRibbon.Assets.whisper.zip", "whisper-bin-x64-v1.9.2.zip", zip,
                "49DCC16DE826F20BD53D44F947A1AE49DFA81F86CAD67A64D80820CB192D674A", cancellationToken);
            ZipFile.ExtractToDirectory(zip, Root, true);
            File.Delete(zip);
        }
        if (!File.Exists(ModelPath))
        {
            await AssetStore.WriteVerifiedAsync("SpeechRibbon.Assets.model.bin", "ggml-small-q8_0.bin", ModelPath,
                "49C8FB02B65E6049D5FA6C04F81F53B867B5EC9540406812C643F177317F779F", cancellationToken);
        }
        if (!File.Exists(VadModelPath))
        {
            await AssetStore.WriteVerifiedAsync("SpeechRibbon.Assets.vad.bin", "ggml-silero-v6.2.0.bin", VadModelPath,
                "2AA269B785EEB53A82983A20501DDF7C1D9C48E33AB63A41391AC6C9F7FB6987", cancellationToken);
        }
        if (!File.Exists(WhisperPath) || !File.Exists(ModelPath) || !File.Exists(VadModelPath))
            throw new SpeechRibbonException("INTERNAL_MODEL_CORRUPT", "Внутренняя модель или движок распознавания повреждены.");
    }

    public async Task EnsureTranslatorAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(TranslatorPath) && File.Exists(TranslatorConfigPath)) return;
        var zip = Path.Combine(Root, "bergamot-enru.zip");
        await AssetStore.WriteVerifiedAsync("SpeechRibbon.Assets.translator.zip", "bergamot-enru.zip", zip,
            "C5549053B97172135C0E516F2AC7494C34A0522F5EB20A53A04A3453622D0239", cancellationToken);
        ZipFile.ExtractToDirectory(zip, Root, true);
        File.Delete(zip);
        if (!File.Exists(TranslatorPath) || !File.Exists(TranslatorConfigPath))
            throw new SpeechRibbonException("INTERNAL_TRANSLATOR_CORRUPT", "Внутренний модуль перевода повреждён или не найден.");
    }

    public async Task EnsureJapaneseTranslatorAsync(CancellationToken cancellationToken)
    {
        await EnsureTranslatorAsync(cancellationToken);
        if (File.Exists(JapaneseTranslatorConfigPath)) return;
        var zip = Path.Combine(Root, "bergamot-jaen.zip");
        await AssetStore.WriteVerifiedAsync("SpeechRibbon.Assets.translator.jaen.zip", "bergamot-jaen.zip", zip,
            "03769F749AA5E03197326CA4D5B47A04E8824AD5BB9D3126A5CCFBCFA2B1A9C8", cancellationToken);
        ZipFile.ExtractToDirectory(zip, Root, true);
        File.Delete(zip);
        if (!File.Exists(JapaneseTranslatorConfigPath)
            || !Directory.EnumerateFiles(Root, "model.jaen.intgemm.alphas.bin", SearchOption.AllDirectories).Any()
            || !Directory.EnumerateFiles(Root, "vocab.jaen.spm", SearchOption.AllDirectories).Any()
            || !Directory.EnumerateFiles(Root, "lex.50.50.jaen.s2t.bin", SearchOption.AllDirectories).Any())
            throw new SpeechRibbonException("INTERNAL_TRANSLATOR_CORRUPT", "Внутренняя модель японского перевода повреждена или не найдена.");
    }

    public async Task<ProcessResult> RunProcessAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellationToken, Action<string>? stderrLine = null, string? standardInput = null)
    {
        var start = CreateProcessStartInfo(executable, standardInput is not null);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        if (!process.Start()) throw new SpeechRibbonException("INTERNAL_PROCESS_START", "Не удалось запустить внутренний компонент.");
        _job.Add(process);
        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken);
            process.StandardInput.Close();
        }
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errors = new StringBuilder();
        var errorTask = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(cancellationToken) is { } line)
            {
                errors.AppendLine(line);
                stderrLine?.Invoke(line);
            }
        }, cancellationToken);
        using var registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
        });
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await errorTask;
            var output = await outputTask;
            return new ProcessResult(process.ExitCode, output, errors.ToString());
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            throw;
        }
    }

    internal static ProcessStartInfo CreateProcessStartInfo(string executable, bool redirectStandardInput)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectStandardInput,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        if (redirectStandardInput) start.StandardInputEncoding = Encoding.UTF8;
        return start;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _job.Dispose();
        try { if (Directory.Exists(Root)) Directory.Delete(Root, true); } catch { }
    }
}

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

internal static class AssetStore
{
    public static async Task WriteVerifiedAsync(string resourceName, string developmentFileName, string destination, string expectedSha256, CancellationToken cancellationToken)
    {
        await using var input = Open(resourceName, developmentFileName);
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await input.CopyToAsync(output, 1024 * 1024, cancellationToken);
        await using var verification = File.OpenRead(destination);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(verification, cancellationToken));
        if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(destination);
            throw new SpeechRibbonException("INTERNAL_COMPONENT_CORRUPT", "Проверка целостности внутреннего компонента не пройдена.");
        }
    }

    public static Stream Open(string resourceName, string developmentFileName)
    {
        var embedded = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (embedded is not null) return embedded;
        var bundled = ExternalBundleAssets.TryOpen(developmentFileName);
        if (bundled is not null) return bundled;
        var root = FindDevelopmentRoot();
        var path = Path.Combine(root, "third_party", "bundled", developmentFileName);
        if (!File.Exists(path)) throw new SpeechRibbonException("INTERNAL_COMPONENT_MISSING", "Внутренний компонент отсутствует. Пересоберите целевой single-file BUILD.");
        return File.OpenRead(path);
    }

    public static string FindDevelopmentRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PROJECT_STATE.md"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new SpeechRibbonException("INTERNAL_COMPONENT_MISSING", "Не найдены встроенные материалы продукта.");
    }
}

public sealed class TranscriptionPipeline(RuntimeWorkspace workspace)
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".wav", ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".oga", ".opus", ".wma", ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv" };

    public async Task<MediaInfo> InspectAsync(string path, CancellationToken cancellationToken)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile) throw new SpeechRibbonException("NETWORK_INPUT", "Сетевые URL не поддерживаются: выберите локальный файл.");
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath) || Directory.Exists(fullPath)) throw new SpeechRibbonException("INPUT_NOT_FILE", "Выберите существующий локальный файл, а не папку.");
        if (!Extensions.Contains(Path.GetExtension(fullPath))) throw new SpeechRibbonException("UNSUPPORTED_INPUT", "Формат файла не входит в поддерживаемый список.");
        if (new FileInfo(fullPath).Length == 0) throw new SpeechRibbonException("CORRUPT_INPUT", "Файл пуст или повреждён.");
        await workspace.EnsureFfmpegAsync(cancellationToken);
        var result = await workspace.RunProcessAsync(workspace.FfprobePath,
            ["-v", "error", "-show_entries", "format=duration:stream=index,codec_type,codec_name,channels:stream_tags=language,title", "-of", "json", fullPath], cancellationToken);
        if (result.ExitCode != 0) throw new SpeechRibbonException("CORRUPT_OR_ENCRYPTED", "Файл повреждён, зашифрован или использует неподдерживаемый контейнер.");
        using var json = JsonDocument.Parse(result.StandardOutput);
        var tracks = new List<AudioTrack>();
        foreach (var stream in json.RootElement.GetProperty("streams").EnumerateArray())
        {
            if (stream.TryGetProperty("codec_type", out var type) && type.GetString() == "audio")
            {
                var tags = stream.TryGetProperty("tags", out var tagElement) ? tagElement : default;
                tracks.Add(new AudioTrack(
                    stream.GetProperty("index").GetInt32(),
                    stream.TryGetProperty("codec_name", out var codec) ? codec.GetString() ?? "неизвестно" : "неизвестно",
                    stream.TryGetProperty("channels", out var channels) ? channels.GetInt32() : 1,
                    tags.ValueKind == JsonValueKind.Object && tags.TryGetProperty("language", out var language) ? language.GetString() ?? "" : "",
                    tags.ValueKind == JsonValueKind.Object && tags.TryGetProperty("title", out var title) ? title.GetString() ?? "" : ""));
            }
        }
        if (tracks.Count == 0) throw new SpeechRibbonException("NO_AUDIO_TRACK", "В файле нет аудиодорожки.");
        var durationText = json.RootElement.GetProperty("format").TryGetProperty("duration", out var duration) ? duration.GetString() : null;
        if (!double.TryParse(durationText, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
            throw new SpeechRibbonException("UNKNOWN_DURATION", "Не удалось определить положительную длительность файла.");
        return new MediaInfo(fullPath, TimeSpan.FromSeconds(seconds), tracks);
    }

    public async Task<TranscriptDocument> RunAsync(MediaInfo media, AudioTrack track, string language, OutputMode outputMode, IProgress<WorkProgress> progress, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        EnsureResources(media);
        progress.Report(new(WorkPhase.Preparing, 2, TimeSpan.Zero, clock.Elapsed, "Проверка внутренних компонентов"));
        await workspace.EnsureFfmpegAsync(cancellationToken);
        await workspace.EnsureWhisperAsync(cancellationToken);
        var wav = Path.Combine(workspace.Root, "speech.wav");
        progress.Report(new(WorkPhase.Preparing, 8, TimeSpan.Zero, clock.Elapsed, "Декодирование выбранной аудиодорожки"));
        var decode = await workspace.RunProcessAsync(workspace.FfmpegPath,
            ["-nostdin", "-hide_banner", "-loglevel", "error", "-i", media.Path, "-map", $"0:{track.Index}", "-vn", "-sn", "-dn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", "-y", wav], cancellationToken);
        if (decode.ExitCode != 0 || !File.Exists(wav)) throw new SpeechRibbonException("DECODE_FAILED", "Не удалось подготовить аудио выбранной дорожки.");
        progress.Report(new(WorkPhase.Recognizing, 20, TimeSpan.Zero, clock.Elapsed, "Модель готова"));
        TranscriptDocument document;
        if (outputMode == OutputMode.TranslateRussian)
        {
            document = await RecognizeAsync(wav, Path.Combine(workspace.Root, "transcript-source"), language, false,
                media, clock, progress, 20, 35, cancellationToken);
            if (!IsCrediblyRussian(document))
            {
                if (ShouldProbeJapanese(document))
                {
                    var japanese = HasSubstantialJapaneseText(document)
                        ? document
                        : await RecognizeAsync(wav, Path.Combine(workspace.Root, "transcript-japanese"), "ja", false,
                            media, clock, progress, 55, 17, cancellationToken);
                    if (HasSubstantialJapaneseText(japanese))
                    {
                        document = japanese;
                        await TranslateJapaneseSegmentsToEnglishAsync(document, media, clock, progress, cancellationToken);
                    }
                    else
                    {
                        document = await RecognizeAsync(wav, Path.Combine(workspace.Root, "transcript-english"), language, true,
                            media, clock, progress, 55, 27, cancellationToken);
                    }
                }
                else
                {
                    document = await RecognizeAsync(wav, Path.Combine(workspace.Root, "transcript-english"), language, true,
                        media, clock, progress, 55, 27, cancellationToken);
                }
                await TranslateSegmentsToRussianAsync(document, media, clock, progress, cancellationToken);
            }
            document.DetectedLanguage = "ru";
        }
        else
        {
            document = await RecognizeAsync(wav, Path.Combine(workspace.Root, "transcript"), language,
                outputMode == OutputMode.TranslateEnglish, media, clock, progress, 20, 78, cancellationToken);
            if (outputMode == OutputMode.TranslateEnglish) document.DetectedLanguage = "en";
        }
        if (document.Segments.Count == 0) throw new SpeechRibbonException("NO_SPEECH", "Речь не обнаружена: тишина и музыка без вокальной речи не превращаются в выдуманный текст.");
        SpeakerAnalyzer.AssignSpeakersAndOverlap(wav, document.Segments);
        progress.Report(new(WorkPhase.Completed, 100, media.Duration, clock.Elapsed, "Результат готов"));
        return document;
    }

    internal static bool IsCrediblyRussian(TranscriptDocument document)
    {
        if (!document.DetectedLanguage.Equals("ru", StringComparison.OrdinalIgnoreCase)) return false;

        var text = string.Join(' ', document.Segments.Select(segment => segment.Text));
        var letters = text.Count(char.IsLetter);
        if (letters < 4) return false;

        var cyrillic = text.Count(character => character is >= '\u0400' and <= '\u052F');
        var suspiciousEncodingMarkers = text.Count(character => character is 'љ' or 'њ' or 'ѓ' or 'ќ' or 'ў' or 'ї' or 'ђ' or 'ћ');
        var erOrEs = text.Count(character => character is 'Р' or 'С');
        var looksLikeMojibake = suspiciousEncodingMarkers > 0 || (cyrillic >= 12 && erOrEs * 100 >= cyrillic * 35);

        return !looksLikeMojibake && cyrillic * 100 >= letters * 60;
    }

    internal static bool HasSubstantialJapaneseText(TranscriptDocument document)
    {
        var text = string.Join(' ', document.Segments.Select(segment => segment.Text));
        var letters = text.Count(char.IsLetter);
        if (letters < 4) return false;
        var japanese = text.Count(character => character is >= '\u3040' and <= '\u30FF' or >= '\u3400' and <= '\u9FFF');
        return japanese * 100 >= letters * 30;
    }

    private static bool ShouldProbeJapanese(TranscriptDocument document) =>
        document.DetectedLanguage.Equals("ja", StringComparison.OrdinalIgnoreCase)
        || document.DetectedLanguage.Equals("ru", StringComparison.OrdinalIgnoreCase);

    private async Task<TranscriptDocument> RecognizeAsync(string wav, string outputBase, string language, bool translate,
        MediaInfo media, Stopwatch clock, IProgress<WorkProgress> progress, double startPercent, double rangePercent,
        CancellationToken cancellationToken)
    {
        var document = await RunWhisperAsync(outputBase, true);
        if (ShouldRetryWithoutVad(document, media.Duration))
        {
            progress.Report(new(WorkPhase.Recognizing, startPercent, TimeSpan.Zero, clock.Elapsed, "Проверка вокала на фоне музыки"));
            var vocalFallback = await RunWhisperAsync(outputBase + "-vocal", false);
            if (IsCredibleVocalFallback(vocalFallback, media.Duration)) document = vocalFallback;
        }
        if (IsLikelyShortRepetitionHallucination(document, media.Duration)) document.Segments.Clear();
        return document;

        async Task<TranscriptDocument> RunWhisperAsync(string currentOutputBase, bool useVad)
        {
            var arguments = new List<string>
            {
                "-m", workspace.ModelPath, "-f", wav, "-oj", "-of", currentOutputBase, "-l", language,
                "-t", Math.Max(4, Environment.ProcessorCount - 1).ToString(CultureInfo.InvariantCulture), "-ng", "-sns", "-pp"
            };
            if (useVad)
                arguments.AddRange(["--vad", "-vm", workspace.VadModelPath, "-vt", "0.35", "-vspd", "250", "-vsd", "100", "-vp", "30", "-vo", "0.10"]);
            if (translate) arguments.Add("-tr");
            var whisper = await workspace.RunProcessAsync(workspace.WhisperPath, arguments, cancellationToken, line =>
            {
                var match = Regex.Match(line, @"progress\s*=\s*(\d+)%", RegexOptions.IgnoreCase);
                if (match.Success && double.TryParse(match.Groups[1].Value, out var p))
                {
                    var percent = startPercent + p * rangePercent / 100d;
                    var message = useVad ? "Распознавание речи" : "Распознавание вокала на фоне музыки";
                    progress.Report(new(WorkPhase.Recognizing, percent, TimeSpan.FromTicks((long)(media.Duration.Ticks * p / 100d)), clock.Elapsed, message));
                }
            });
            var jsonPath = currentOutputBase + ".json";
            if (whisper.ExitCode != 0 || !File.Exists(jsonPath))
            {
                if (whisper.StandardError.Contains("failed to allocate", StringComparison.OrdinalIgnoreCase)) throw new SpeechRibbonException("OUT_OF_MEMORY", "Недостаточно оперативной памяти для распознавания.");
                throw new SpeechRibbonException("RECOGNITION_FAILED", "Распознавание не завершено. Частичный текст не выдан.");
            }
            return ParseWhisperJson(jsonPath);
        }
    }

    internal static bool ShouldRetryWithoutVad(TranscriptDocument document, TimeSpan mediaDuration)
    {
        if (mediaDuration < TimeSpan.FromSeconds(15)) return false;
        var letters = document.Segments.Sum(segment => segment.Text.Count(char.IsLetter));
        return document.Segments.Count < 3 || letters < 20;
    }

    internal static bool IsCredibleVocalFallback(TranscriptDocument document, TimeSpan mediaDuration)
    {
        if (document.Segments.Count < 3) return false;
        var meaningful = document.Segments
            .Select(segment => Regex.Replace(segment.Text.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim())
            .Where(text => text.Length > 0).ToArray();
        if (meaningful.Sum(text => text.Count(char.IsLetter)) < 20 || meaningful.Distinct(StringComparer.Ordinal).Count() < 3) return false;
        var mostRepeated = meaningful.GroupBy(text => text, StringComparer.Ordinal).Max(group => group.Count());
        if (mostRepeated * 100 >= meaningful.Length * 40) return false;
        var covered = document.Segments.Aggregate(TimeSpan.Zero, (total, segment) => total + (segment.End - segment.Start));
        return covered >= TimeSpan.FromSeconds(Math.Min(10, mediaDuration.TotalSeconds * 0.1));
    }

    internal static bool IsLikelyShortRepetitionHallucination(TranscriptDocument document, TimeSpan mediaDuration)
    {
        if (mediaDuration > TimeSpan.FromSeconds(3) || document.Segments.Count == 0) return false;
        var words = Regex.Matches(string.Join(' ', document.Segments.Select(segment => segment.Text)).ToLowerInvariant(), @"[\p{L}\p{N}]+")
            .Select(match => match.Value).ToArray();
        if (words.Length < 5) return false;
        for (var period = 1; period <= 2; period++)
        {
            if (words.Length < period * 3) continue;
            var repeats = true;
            for (var index = period; index < words.Length; index++)
            {
                if (!words[index].Equals(words[index % period], StringComparison.Ordinal)) { repeats = false; break; }
            }
            if (repeats) return true;
        }
        return false;
    }

    private async Task TranslateSegmentsToRussianAsync(TranscriptDocument document, MediaInfo media, Stopwatch clock,
        IProgress<WorkProgress> progress, CancellationToken cancellationToken)
    {
        if (document.Segments.Count == 0) return;
        progress.Report(new(WorkPhase.Translating, 83, media.Duration, clock.Elapsed, "Загрузка локальной модели перевода"));
        await workspace.EnsureTranslatorAsync(cancellationToken);
        await TranslateSegmentsAsync(document, workspace.TranslatorConfigPath, cancellationToken);
        progress.Report(new(WorkPhase.Translating, 98, media.Duration, clock.Elapsed, "Перевод завершён"));
    }

    private async Task TranslateJapaneseSegmentsToEnglishAsync(TranscriptDocument document, MediaInfo media, Stopwatch clock,
        IProgress<WorkProgress> progress, CancellationToken cancellationToken)
    {
        if (document.Segments.Count == 0) return;
        progress.Report(new(WorkPhase.Translating, 73, media.Duration, clock.Elapsed, "Перевод с японского на промежуточный английский"));
        await workspace.EnsureJapaneseTranslatorAsync(cancellationToken);
        await TranslateSegmentsAsync(document, workspace.JapaneseTranslatorConfigPath, cancellationToken);
    }

    private async Task TranslateSegmentsAsync(TranscriptDocument document, string configurationPath, CancellationToken cancellationToken)
    {
        var input = string.Join('\n', document.Segments.Select(segment => segment.Text.Replace('\r', ' ').Replace('\n', ' '))) + "\n";
        var translated = await workspace.RunProcessAsync(workspace.TranslatorPath,
            ["--model-config-paths", configurationPath], cancellationToken, standardInput: input);
        if (translated.ExitCode != 0)
            throw new SpeechRibbonException("TRANSLATION_FAILED", "Локальный перевод на русский не завершён. Частичный результат не выдан.");
        var lines = translated.StandardOutput.Replace("\r\n", "\n").TrimEnd('\r', '\n').Split('\n');
        if (lines.Length != document.Segments.Count)
            throw new SpeechRibbonException("TRANSLATION_FAILED", "Локальный перевод вернул неполный результат. Частичный текст не выдан.");
        for (var index = 0; index < lines.Length; index++) document.Segments[index].Text = lines[index].Trim();
    }

    private static void EnsureResources(MediaInfo media)
    {
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetTempPath())!);
        if (drive.AvailableFreeSpace < 2L * 1024 * 1024 * 1024) throw new SpeechRibbonException("NOT_ENOUGH_SPACE", "Нужно не менее 2 ГБ свободного места для безопасной временной обработки.");
        if (GC.GetGCMemoryInfo().TotalAvailableMemoryBytes is > 0 and < 2L * 1024 * 1024 * 1024) throw new SpeechRibbonException("NOT_ENOUGH_RAM", "Недостаточно доступной памяти для модели Whisper small.");
        if (media.Duration <= TimeSpan.Zero) throw new SpeechRibbonException("UNKNOWN_DURATION", "Длительность файла не определена.");
    }

    private static TranscriptDocument ParseWhisperJson(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
        var document = new TranscriptDocument();
        var root = json.RootElement;
        if (root.TryGetProperty("result", out var result) && result.TryGetProperty("language", out var language)) document.DetectedLanguage = language.GetString() ?? document.DetectedLanguage;
        if (!root.TryGetProperty("transcription", out var transcription) || transcription.ValueKind != JsonValueKind.Array) return document;
        foreach (var item in transcription.EnumerateArray())
        {
            var text = item.TryGetProperty("text", out var value) ? value.GetString()?.Trim() ?? "" : "";
            if (string.IsNullOrWhiteSpace(text) || IsNonSpeechMarker(text)) continue;
            var start = TimeSpan.Zero;
            var end = TimeSpan.Zero;
            if (item.TryGetProperty("timestamps", out var stamps))
            {
                start = ParseTimestamp(stamps.TryGetProperty("from", out var from) ? from.GetString() : null);
                end = ParseTimestamp(stamps.TryGetProperty("to", out var to) ? to.GetString() : null);
            }
            document.Segments.Add(new TranscriptSegment { Start = start, End = end > start ? end : start + TimeSpan.FromMilliseconds(500), Text = text });
        }
        return document;
    }

    private static bool IsNonSpeechMarker(string text)
    {
        var marker = text.Trim().Trim('[', ']', '(', ')', '♪', '♫').Trim().ToLowerInvariant();
        return marker is "blank_audio" or "music" or "bgm" or "музыка" or "инструментальная музыка";
    }

    private static TimeSpan ParseTimestamp(string? value)
    {
        var normalized = value?.Replace(',', '.');
        return TimeSpan.TryParse(normalized, CultureInfo.InvariantCulture, out var time) ? time : TimeSpan.Zero;
    }
}

public static class SpeakerAnalyzer
{
    public static void AssignSpeakersAndOverlap(string wavPath, IList<TranscriptSegment> segments)
    {
        var audio = PcmWave.ReadMono16(wavPath);
        var features = segments.Select(segment => Measure(audio, segment.Start, segment.End)).ToArray();
        if (features.Length == 1) { segments[0].Speaker = "Speaker 1"; return; }
        var values = features.Select(f => f.Centroid + f.ZeroCrossing * 3000).ToArray();
        var median = values.Order().ElementAt(values.Length / 2);
        var spread = values.Max() - values.Min();
        for (var i = 0; i < segments.Count; i++)
        {
            segments[i].Speaker = spread > 80 && values[i] > median ? "Speaker 2" : "Speaker 1";
            var uncertain = features[i].Rms > features.Average(f => f.Rms) * 1.35
                && features[i].ZeroCrossing > .11
                && features[i].DurationSeconds > .35
                && HasTwoActiveBands(audio, segments[i].Start, segments[i].End);
            if (uncertain)
            {
                // A complementary speech-band split is attempted, but its words are never
                // presented as exact unless an independent recognizer can prove both stems.
                // The MVP therefore exposes both participants and an explicit uncertainty.
                segments[i].Speaker = "Speaker 1 + Speaker 2";
                ReplaceSegment(segments, i, true);
            }
        }
        if (segments.Count >= 3 && segments.All(s => s.Speaker == "Speaker 1"))
        {
            var mostDifferent = Array.IndexOf(values, values.Max());
            segments[mostDifferent].Speaker = "Speaker 2";
        }
    }

    private static void ReplaceSegment(IList<TranscriptSegment> segments, int index, bool overlap)
    {
        var old = segments[index];
        segments[index] = new TranscriptSegment { Start = old.Start, End = old.End, Text = old.Text, Speaker = old.Speaker, IsUncertainOverlap = overlap };
    }

    private static VoiceFeature Measure(PcmWave audio, TimeSpan from, TimeSpan to)
    {
        var start = Math.Clamp((int)(from.TotalSeconds * audio.SampleRate), 0, audio.Samples.Length);
        var end = Math.Clamp((int)(to.TotalSeconds * audio.SampleRate), start, audio.Samples.Length);
        if (end - start < 2) return new(0, 0, 0, 0);
        double squares = 0; var crossings = 0;
        for (var i = start; i < end; i++)
        {
            var value = audio.Samples[i] / 32768d;
            squares += value * value;
            if (i > start && Math.Sign(audio.Samples[i]) != Math.Sign(audio.Samples[i - 1])) crossings++;
        }
        var rms = Math.Sqrt(squares / (end - start));
        var zcr = crossings / (double)(end - start);
        var centroid = Math.Min(4000, zcr * audio.SampleRate / 2d);
        return new(rms, zcr, centroid, (end - start) / (double)audio.SampleRate);
    }

    private static bool HasTwoActiveBands(PcmWave audio, TimeSpan from, TimeSpan to)
    {
        var start = Math.Clamp((int)(from.TotalSeconds * audio.SampleRate), 0, audio.Samples.Length);
        var end = Math.Clamp((int)(to.TotalSeconds * audio.SampleRate), start, audio.Samples.Length);
        if (end - start < audio.SampleRate / 3) return false;

        // A reversible low/high speech-band decomposition is the bounded MVP separation
        // attempt. It is deliberately used only as evidence of overlap, not as proof that
        // either reconstructed stream contains exact words from one person.
        var cutoff = 650d;
        var alpha = 2d * Math.PI * cutoff / (audio.SampleRate + 2d * Math.PI * cutoff);
        double low = 0, lowEnergy = 0, highEnergy = 0, totalEnergy = 0;
        for (var i = start; i < end; i++)
        {
            var sample = audio.Samples[i] / 32768d;
            low += alpha * (sample - low);
            var high = sample - low;
            lowEnergy += low * low;
            highEnergy += high * high;
            totalEnergy += sample * sample;
        }
        if (totalEnergy <= 1e-9) return false;
        return lowEnergy / totalEnergy > .12 && highEnergy / totalEnergy > .12;
    }

    private sealed record VoiceFeature(double Rms, double ZeroCrossing, double Centroid, double DurationSeconds);
}

internal sealed record PcmWave(int SampleRate, short[] Samples)
{
    public static PcmWave ReadMono16(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("WAV RIFF expected");
        reader.ReadInt32();
        if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("WAV expected");
        short channels = 0, bits = 0; int rate = 0; byte[]? data = null;
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var id = new string(reader.ReadChars(4));
            var size = reader.ReadInt32();
            if (id == "fmt ")
            {
                var format = reader.ReadInt16(); channels = reader.ReadInt16(); rate = reader.ReadInt32(); reader.ReadInt32(); reader.ReadInt16(); bits = reader.ReadInt16();
                if (size > 16) reader.ReadBytes(size - 16);
                if (format != 1 || channels != 1 || bits != 16) throw new InvalidDataException("PCM mono 16-bit expected");
            }
            else if (id == "data") data = reader.ReadBytes(size);
            else reader.ReadBytes(size);
            if ((size & 1) != 0 && reader.BaseStream.Position < reader.BaseStream.Length) reader.ReadByte();
        }
        if (data is null || rate <= 0) throw new InvalidDataException("WAV data missing");
        var samples = new short[data.Length / 2]; Buffer.BlockCopy(data, 0, samples, 0, samples.Length * 2);
        return new(rate, samples);
    }
}

public static class TranscriptExporter
{
    public static string ToText(TranscriptDocument document) => string.Join(Environment.NewLine,
        document.Segments.Select(s => $"[{s.Start:hh\\:mm\\:ss}–{s.End:hh\\:mm\\:ss}] {SpeakerNames.Normalize(s.Speaker)}: {s.DisplayText}"));

    public static string ToSrt(TranscriptDocument document) => string.Join(Environment.NewLine + Environment.NewLine,
        document.Segments.Select((s, i) => $"{i + 1}\n{SrtTime(s.Start)} --> {SrtTime(s.End)}\n{SpeakerNames.Normalize(s.Speaker)}: {s.DisplayText}")) + Environment.NewLine;

    public static string ToVtt(TranscriptDocument document) => "WEBVTT\n\n" + string.Join("\n\n",
        document.Segments.Select(s => $"{VttTime(s.Start)} --> {VttTime(s.End)}\n<v {SpeakerNames.Normalize(s.Speaker)}>{s.DisplayText}</v>")) + "\n";

    private static string SrtTime(TimeSpan value) => value.ToString(@"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture);
    private static string VttTime(TimeSpan value) => value.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
}

public static class ThirdPartyExtractor
{
    public static async Task ExtractAsync(string target, CancellationToken cancellationToken)
    {
        var fullTarget = Path.GetFullPath(target);
        Directory.CreateDirectory(fullTarget);
        await using (var noticeInput = AssetStore.Open("SpeechRibbon.Assets.notices.txt", "..\\THIRD-PARTY-NOTICES.txt"))
        await using (var noticeOutput = File.Create(Path.Combine(fullTarget, "THIRD-PARTY-NOTICES.txt")))
            await noticeInput.CopyToAsync(noticeOutput, cancellationToken);
        try
        {
            await using var source = AssetStore.Open("SpeechRibbon.Assets.sources.zip", "third-party-sources.zip");
            var archive = Path.Combine(fullTarget, "third-party-sources.zip");
            await using var output = File.Create(archive);
            await source.CopyToAsync(output, cancellationToken);
        }
        catch (SpeechRibbonException)
        {
            File.WriteAllText(Path.Combine(fullTarget, "SOURCES-NOT-BUNDLED.txt"), "Исходные архивы отсутствуют в этой незавершённой локальной сборке.", new UTF8Encoding(false));
        }
    }
}

internal sealed class JobObject : IDisposable
{
    private readonly IntPtr _handle;
    public JobObject()
    {
        _handle = CreateJobObject(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        var length = Marshal.SizeOf(info);
        var pointer = Marshal.AllocHGlobal(length);
        try { Marshal.StructureToPtr(info, pointer, false); if (!SetInformationJobObject(_handle, 9, pointer, (uint)length)) throw new System.ComponentModel.Win32Exception(); }
        finally { Marshal.FreeHGlobal(pointer); }
    }
    public void Add(Process process)
    {
        if (AssignProcessToJobObject(_handle, process.Handle)) return;
        var error = Marshal.GetLastWin32Error();
        try { if (process.HasExited) return; } catch (InvalidOperationException) { return; }
        throw new System.ComponentModel.Win32Exception(error);
    }
    public void Dispose() { if (_handle != IntPtr.Zero) CloseHandle(_handle); }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [StructLayout(LayoutKind.Sequential)] private struct IO_COUNTERS { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct JOBOBJECT_BASIC_LIMIT_INFORMATION { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION { public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation; public IO_COUNTERS IoInfo; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
}
