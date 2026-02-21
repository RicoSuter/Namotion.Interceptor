using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Namotion.Interceptor.Connectors.Tests;

public static class ModuleInitializer
{
    // Per-test state for scrubbing base62 subject IDs.
    // Keyed by Verify's Counter instance (one per test) via ConditionalWeakTable
    // so state is isolated per test and garbage-collected when the Counter is disposed.
    private static readonly ConditionalWeakTable<object, ScrubberState> _stateByCounter = new();

    private static readonly Regex Base62IdPattern = new(@"(?<![a-zA-Z0-9])[0-9A-Za-z]{22}(?![a-zA-Z0-9])", RegexOptions.Compiled);

    private sealed class ScrubberState
    {
        public Dictionary<string, string> IdMap { get; } = new();
        public int Counter { get; set; }
    }

    [ModuleInitializer]
    public static void Init()
    {
        // Prevent Verify from re-sorting dictionary keys alphabetically.
        // Our SubjectUpdate dictionaries have root-first ordering that must be preserved
        // for deterministic scrubbing (root always gets SubjectId_1).
        VerifierSettings.DontSortDictionaries();

        // Scrub base62-encoded subject IDs (22-char alphanumeric strings).
        // These are randomly generated GUIDs encoded in base62, so they change every run.
        // We replace them with deterministic "SubjectId_N" placeholders.
        //
        // With DontSortDictionaries() above, Verify preserves the insertion order
        // of dictionary keys. Since our SubjectUpdateBuilder always puts the root
        // entry first, Root always appears first and gets SubjectId_1.
        //
        // Verify may call this scrubber multiple times per test (per-segment),
        // so we use Counter.CurrentOrNull as a per-test key to maintain consistent
        // ID mapping across invocations within the same test.
        VerifierSettings.AddScrubber(builder =>
        {
            var counterKey = (object?)Counter.CurrentOrNull;
            if (counterKey is null)
                return;

            var state = _stateByCounter.GetOrCreateValue(counterKey);

            var text = builder.ToString();

            // Match 22-char alphanumeric strings that look like base62 subject IDs.
            // They appear as dictionary keys, Root values, Id values, AfterId values.
            var replaced = Base62IdPattern.Replace(text, match =>
            {
                var id = match.Value;
                if (!state.IdMap.TryGetValue(id, out var replacement))
                {
                    state.Counter++;
                    replacement = $"SubjectId_{state.Counter}";
                    state.IdMap[id] = replacement;
                }
                return replacement;
            });

            builder.Clear();
            builder.Append(replaced);
        });
    }
}
