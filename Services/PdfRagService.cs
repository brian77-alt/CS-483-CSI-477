using System.Text.RegularExpressions;

namespace CS_483_CSI_477.Services
{
    public sealed class RagHit
    {
        public int Page { get; set; }
        public double Score { get; set; }
        public string Snippet { get; set; } = "";
    }

    public class PdfRagService
    {
        private static readonly Regex TokenRx = new(@"[A-Za-z0-9]+", RegexOptions.Compiled);

        public List<RagHit> FindTopRelevantSnippets(
            List<PdfPageText> pages,
            string question,
            int topK = 5,
            int snippetMaxChars = 900)
        {
            var qTokens = Tokenize(question)
                .Where(t => t.Length >= 3)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (qTokens.Count == 0 || pages.Count == 0)
                return new List<RagHit>();

            var hits = new List<RagHit>();

            foreach (var p in pages)
            {
                var pageText = p.Text ?? "";
                if (pageText.Length == 0) continue;

                // Score = token overlap / sqrt(len)
                var pTokens = Tokenize(pageText).Where(t => t.Length >= 3).ToList();
                if (pTokens.Count == 0) continue;

                int overlap = pTokens.Count(t => qTokens.Contains(t));
                if (overlap == 0) continue;

                double score = overlap / Math.Sqrt(pTokens.Count);

                hits.Add(new RagHit
                {
                    Page = p.Page,
                    Score = score,
                    Snippet = MakeSnippet(pageText, qTokens, snippetMaxChars)
                });
            }

            return hits
                .OrderByDescending(h => h.Score)
                .Take(topK)
                .ToList();
        }

        private static IEnumerable<string> Tokenize(string text)
        {
            foreach (Match m in TokenRx.Matches(text ?? ""))
                yield return m.Value;
        }

        private static string MakeSnippet(string text, HashSet<string> qTokens, int maxChars)
        {
            // Try to center snippet near first matched token
            var idx = IndexOfAnyToken(text, qTokens);
            if (idx < 0) idx = 0;

            int start = Math.Max(0, idx - (maxChars / 3));
            int len = Math.Min(maxChars, text.Length - start);

            var snippet = text.Substring(start, len).Trim();
            if (start > 0) snippet = "… " + snippet;
            if (start + len < text.Length) snippet = snippet + " …";

            return snippet;
        }

        private static int IndexOfAnyToken(string text, HashSet<string> qTokens)
        {
            if (string.IsNullOrWhiteSpace(text)) return -1;
            foreach (var tok in qTokens)
            {
                var idx = text.IndexOf(tok, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0) return idx;
            }
            return -1;
        }
    }
}