using System.Windows.Controls;
using System.Windows.Documents;

namespace SpeechRibbon;

internal static class AnswerFormatting
{
    // Plain text plus paired **strong** markers only; no HTML, links or executable markup.
    public static void Render(TextBlock target, string text)
    {
        target.Inlines.Clear();
        int position = 0;
        while (position < text.Length)
        {
            int start = text.IndexOf("**", position, StringComparison.Ordinal);
            int end = start < 0 ? -1 : text.IndexOf("**", start + 2, StringComparison.Ordinal);
            if (start < 0 || end <= start + 2)
            {
                target.Inlines.Add(new Run(text[position..]));
                break;
            }
            if (start > position) target.Inlines.Add(new Run(text[position..start]));
            target.Inlines.Add(new Bold(new Run(text[(start + 2)..end])));
            position = end + 2;
        }
    }
}
