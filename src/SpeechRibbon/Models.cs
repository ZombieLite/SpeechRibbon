using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SpeechRibbon;

public sealed record AudioTrack(int Index, string Codec, int Channels, string Language, string Title)
{
    public string Display => $"{Index + 1}: {Codec}, {Channels} кан."
        + (string.IsNullOrWhiteSpace(Language) ? "" : $", {Language}")
        + (string.IsNullOrWhiteSpace(Title) ? "" : $" — {Title}");
}

public sealed class TranscriptSegment : INotifyPropertyChanged
{
    private string _speaker = "Speaker 1";
    public TimeSpan Start { get; init; }
    public TimeSpan End { get; init; }
    public string Text { get; set; } = "";
    public bool IsUncertainOverlap { get; init; }
    public string Speaker { get => _speaker; set { var edited = SpeakerNames.SanitizeDuringEdit(value); if (_speaker == edited) return; _speaker = edited; OnPropertyChanged(); } }
    public string TimeLabel => $"{Start:hh\\:mm\\:ss} – {End:hh\\:mm\\:ss}";
    public string DisplayText => IsUncertainOverlap ? $"{Text}  [неразборчивое наложение]" : Text;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new(propertyName));
}

public static class SpeakerNames
{
    public static string SanitizeDuringEdit(string? value) => new((value ?? "").Select(character => char.IsControl(character) ? ' ' : character).ToArray());
    public static string Normalize(string? value)
    {
        var normalized = SanitizeDuringEdit(value).Trim();
        return normalized.Length == 0 ? "Speaker" : normalized;
    }
}

public sealed class TranscriptDocument
{
    public string DetectedLanguage { get; set; } = "не определён";
    public ObservableCollection<TranscriptSegment> Segments { get; } = [];
    public bool HasUncertainOverlap => Segments.Any(x => x.IsUncertainOverlap);
}

public enum OutputMode { Transcribe, TranslateRussian, TranslateEnglish }

public enum WorkPhase { Idle, Preparing, Recognizing, Translating, Completed, Cancelled, Failed }

public sealed record WorkProgress(WorkPhase Phase, double Percent, TimeSpan Processed, TimeSpan Elapsed, string Message);

public static class WhisperLanguages
{
    public static IReadOnlyList<KeyValuePair<string, string>> All { get; } = new Dictionary<string, string>
    {
        ["auto"]="Автоопределение", ["af"]="Африкаанс", ["sq"]="Албанский", ["am"]="Амхарский", ["ar"]="Арабский",
        ["hy"]="Армянский", ["as"]="Ассамский", ["az"]="Азербайджанский", ["ba"]="Башкирский", ["eu"]="Баскский",
        ["be"]="Белорусский", ["bn"]="Бенгальский", ["bs"]="Боснийский", ["br"]="Бретонский", ["bg"]="Болгарский",
        ["my"]="Бирманский", ["ca"]="Каталанский", ["zh"]="Китайский", ["hr"]="Хорватский", ["cs"]="Чешский",
        ["da"]="Датский", ["nl"]="Нидерландский", ["en"]="Английский", ["et"]="Эстонский", ["fo"]="Фарерский",
        ["fi"]="Финский", ["fr"]="Французский", ["gl"]="Галисийский", ["ka"]="Грузинский", ["de"]="Немецкий",
        ["el"]="Греческий", ["gu"]="Гуджарати", ["ht"]="Гаитянский креольский", ["ha"]="Хауса", ["haw"]="Гавайский",
        ["he"]="Иврит", ["hi"]="Хинди", ["hu"]="Венгерский", ["is"]="Исландский", ["id"]="Индонезийский",
        ["it"]="Итальянский", ["ja"]="Японский", ["jw"]="Яванский", ["kn"]="Каннада", ["kk"]="Казахский",
        ["km"]="Кхмерский", ["ko"]="Корейский", ["lo"]="Лаосский", ["la"]="Латынь", ["lv"]="Латышский",
        ["ln"]="Лингала", ["lt"]="Литовский", ["lb"]="Люксембургский", ["mk"]="Македонский", ["mg"]="Малагасийский",
        ["ms"]="Малайский", ["ml"]="Малаялам", ["mt"]="Мальтийский", ["mi"]="Маори", ["mr"]="Маратхи",
        ["mn"]="Монгольский", ["ne"]="Непальский", ["no"]="Норвежский", ["nn"]="Нюнорск", ["oc"]="Окситанский",
        ["ps"]="Пушту", ["fa"]="Персидский", ["pl"]="Польский", ["pt"]="Португальский", ["pa"]="Панджаби",
        ["ro"]="Румынский", ["ru"]="Русский", ["sa"]="Санскрит", ["sr"]="Сербский", ["sn"]="Шона",
        ["sd"]="Синдхи", ["si"]="Сингальский", ["sk"]="Словацкий", ["sl"]="Словенский", ["so"]="Сомалийский",
        ["es"]="Испанский", ["su"]="Сунданский", ["sw"]="Суахили", ["sv"]="Шведский", ["tl"]="Тагальский",
        ["tg"]="Таджикский", ["ta"]="Тамильский", ["tt"]="Татарский", ["te"]="Телугу", ["th"]="Тайский",
        ["bo"]="Тибетский", ["tr"]="Турецкий", ["tk"]="Туркменский", ["uk"]="Украинский", ["ur"]="Урду",
        ["uz"]="Узбекский", ["vi"]="Вьетнамский", ["cy"]="Валлийский", ["yi"]="Идиш", ["yo"]="Йоруба"
    }.ToList();
}
