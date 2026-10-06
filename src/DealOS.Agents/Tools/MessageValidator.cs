using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace DealOS.Agents.Tools
{
    /// <summary>
    /// Deterministic checks on any message an agent drafts, before a human ever sees it:
    /// no number that the agent was not shown, no forbidden (confidential) term, no
    /// "verified" wording without a Verified fact, hedged wording when Claimed values are used.
    /// </summary>
    public static class MessageValidator
    {
        private static readonly Regex NumberRx = new Regex(@"\d+(?:[.,]\d+)*", RegexOptions.Compiled);
        private static readonly Regex EmailRx = new Regex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);
        private static readonly Regex PhoneRx = new Regex(@"\+?\d[\d\s().-]{8,}\d", RegexOptions.Compiled);
        private static readonly string[] Hedges = { "stated", "states", "indicat", "claimed", "claims", "reported", "according to the seller", "per the seller", "per seller", "seller advises", "to be confirmed", "subject to verification" };

        public static void CollectNumbers(string text, HashSet<string> into)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (Match m in NumberRx.Matches(text))
            {
                foreach (var n in Normalise(m.Value)) into.Add(n);
            }
        }

        /// <summary>"1,250.50" → {"1250.5"}; "500.0000" → {"500"}; ambiguous "1,250" → {"1250","1.25"}.</summary>
        public static IEnumerable<string> Normalise(string raw)
        {
            var results = new List<string>();
            var noComma = raw.Replace(",", "");
            results.Add(Canon(noComma));
            if (raw.Contains(",") && !raw.Contains(".")) results.Add(Canon(raw.Replace(",", ".")));
            return results.Where(r => r != null).Distinct();
        }

        private static string Canon(string s)
        {
            decimal d;
            if (!decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out d)) return null;
            return d.ToString("0.############", CultureInfo.InvariantCulture);
        }

        public sealed class Report
        {
            public readonly List<string> Errors = new List<string>();
            public readonly List<string> Checks = new List<string>();
            public bool Ok { get { return Errors.Count == 0; } }
        }

        public static Report Check(string text, HashSet<string> knownNumbers, IEnumerable<string> forbiddenTerms,
                                   bool hasVerifiedFact, IEnumerable<string> claimedNumbers, bool marketAudience)
        {
            var r = new Report();
            if (string.IsNullOrWhiteSpace(text)) { r.Errors.Add("Message text is empty."); return r; }
            if (text.Length > 1500) r.Errors.Add("Message is too long (" + text.Length + " chars); keep it under 1,500 characters.");

            var unknown = new List<string>();
            foreach (Match m in NumberRx.Matches(text))
            {
                var forms = Normalise(m.Value).ToList();
                decimal d;
                if (forms.Count == 1 && decimal.TryParse(forms[0], NumberStyles.Number, CultureInfo.InvariantCulture, out d) && d < 10 && d == Math.Floor(d)) continue; // list numbering, "2 documents"
                if (!forms.Any(knownNumbers.Contains)) unknown.Add(m.Value);
            }
            if (unknown.Count > 0) r.Errors.Add("Numbers not found in the data you were given: " + string.Join(", ", unknown.Distinct()) + ". Use only numbers from CONTEXT or tool results.");
            r.Checks.Add("numbers_grounded");

            var lower = text.ToLowerInvariant();
            foreach (var term in forbiddenTerms.Where(t => !string.IsNullOrWhiteSpace(t) && t.Trim().Length >= 4).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (lower.Contains(term.Trim().ToLowerInvariant())) r.Errors.Add("Message contains confidential information that must not be shared with this recipient: \"" + term.Trim() + "\".");
            }
            r.Checks.Add("no_confidential_terms");

            if (marketAudience)
            {
                if (EmailRx.IsMatch(text) || PhoneRx.IsMatch(text)) r.Errors.Add("Market-side messages must not contain email addresses or phone numbers.");
                if (!hasVerifiedFact && Regex.IsMatch(lower, @"\bverified\b") && !lower.Contains("not yet verified") && !lower.Contains("unverified"))
                    r.Errors.Add("Message says 'verified' but no fact on this listing has status Verified.");
                var usesClaimed = claimedNumbers.Any(n => NumberRx.Matches(text).Cast<Match>().SelectMany(m => Normalise(m.Value)).Contains(n));
                if (usesClaimed && !Hedges.Any(lower.Contains))
                    r.Errors.Add("Message uses values that are only Claimed by the seller; word them as stated by the seller (e.g. 'seller states ...').");
                r.Checks.Add("evidence_wording");
            }
            return r;
        }
    }
}
