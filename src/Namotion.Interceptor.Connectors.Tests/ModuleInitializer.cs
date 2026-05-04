using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Namotion.Interceptor.Connectors.Tests;

public static class ModuleInitializer
{
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
        VerifierSettings.DontSortDictionaries();

        // Replace random base62 subject IDs (22-char) with deterministic "SubjectId_N" placeholders.
        // Per-test state is isolated via Counter.CurrentOrNull (Verify's per-test counter).
        VerifierSettings.AddScrubber(builder =>
        {
            var counterKey = (object?)Counter.CurrentOrNull;
            if (counterKey is null)
                return;

            var state = _stateByCounter.GetOrCreateValue(counterKey);

            var text = builder.ToString();
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
