using System.Text;
using UglyToad.PdfPig;

namespace CS_483_CSI_477.Services
{
    public sealed class PdfPageText
    {
        public int Page { get; set; }
        public string Text { get; set; } = "";
    }

    public sealed class PdfExtractResult
    {
        public string FileName { get; set; } = "";
        public List<PdfPageText> Pages { get; set; } = new();
        public int TotalChars => Pages.Sum(p => p.Text?.Length ?? 0);
    }

    public class PdfService
    {
        // Extract per-page so we can cite page numbers.
        public PdfExtractResult Extract(byte[] pdfBytes, string fileName, int maxPages = 25, int maxCharsTotal = 200_000)
        {
            using var ms = new MemoryStream(pdfBytes);
            using var doc = PdfDocument.Open(ms);

            int pagesToRead = Math.Min(doc.NumberOfPages, maxPages);

            var result = new PdfExtractResult { FileName = fileName };
            var charBudget = maxCharsTotal;

            for (int i = 1; i <= pagesToRead; i++)
            {
                if (charBudget <= 0) break;

                var page = doc.GetPage(i);
                var text = (page.Text ?? "").Trim();

                if (string.IsNullOrWhiteSpace(text)) continue;

                // Trim each page if it is huge
                if (text.Length > charBudget)
                    text = text.Substring(0, charBudget);

                result.Pages.Add(new PdfPageText
                {
                    Page = i,
                    Text = text
                });

                charBudget -= text.Length;
            }

            if (result.Pages.Count == 0)
            {
                result.Pages.Add(new PdfPageText
                {
                    Page = 1,
                    Text = "(No text extracted. If the PDF is scanned (image-only), OCR is required.)"
                });
            }

            return result;
        }

        public static string FlattenForDebug(List<PdfPageText> pages, int maxChars = 10_000)
        {
            var sb = new StringBuilder();
            foreach (var p in pages)
            {
                sb.AppendLine($"[Page {p.Page}]");
                sb.AppendLine(p.Text);
                sb.AppendLine();
                if (sb.Length >= maxChars) break;
            }
            var s = sb.ToString();
            return s.Length > maxChars ? s.Substring(0, maxChars) : s;
        }
    }
}