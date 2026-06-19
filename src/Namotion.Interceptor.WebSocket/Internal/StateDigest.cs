using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.WebSocket.Serialization;

namespace Namotion.Interceptor.WebSocket.Internal;

/// <summary>
/// Computes a deterministic, value-aware, timestamp-insensitive digest of the converged graph state.
/// Used as an eventual-consistency backstop: server and client each compute the digest during idle
/// and compare it on the heartbeat. A mismatch routes to the existing recovery (Resync for a
/// client-to-server divergence, reconnect/Welcome for a server-to-client divergence).
/// </summary>
/// <remarks>
/// The digest is computed on demand from the live registry/graph. There is no persistent per-update
/// shadow structure (the previous <c>SentStructuralState</c> approach leaked memory and only hashed
/// structure). It is O(1) on the wire (a hex hash string) and O(N) to compute, computed only at idle.
///
/// Determinism guarantees so two participants in the same converged state produce the same digest:
/// <list type="bullet">
///   <item>Subjects are sorted by stable subject id (ordinal); subjects without an id are skipped.</item>
///   <item>Value properties are sorted by name (ordinal) and serialized with the same JSON options the
///         protocol uses (invariant culture, camelCase), so a value that round-tripped through the
///         protocol hashes identically to the live value on the originating side.</item>
///   <item>Write timestamps are never hashed: value write timestamps legitimately differ between
///         participants even when the value is identical.</item>
///   <item>Structural properties are reduced to the referenced child subject ids (object: child id or
///         '-'; collection: ordered child ids; dictionary: sorted key=childId pairs). Child contents are
///         not hashed here because each child is hashed as its own subject.</item>
/// </list>
///
/// Concurrency: properties are read without taking any lock, so a digest computed while writes are in
/// flight can observe a torn snapshot. This is tolerated by design: the digest is only consumed during
/// the idle-gated heartbeat (server: no broadcast within the last interval; client: replying to that
/// heartbeat), so reads are normally quiescent, and the worst case of a non-quiescent read is a
/// false mismatch that triggers one extra (idempotent) resync, never a wrong-but-accepted state.
///
/// Caveat: a value whose JSON serialization is not identical across participants in the same logical
/// state (for example an <c>object</c>-typed property holding a type that serializes differently after a
/// protocol round-trip) produces a persistent digest mismatch and can thrash resync. Keep value
/// properties to types whose protocol JSON round-trips byte-for-byte; this is the same constraint the
/// connector value transport already relies on.
/// </remarks>
internal static class StateDigest
{
    // Same options the protocol uses for the on-the-wire value representation, so the digest of a
    // value matches whether it was just written locally or round-tripped through JSON on the peer.
    private static readonly JsonSerializerOptions ValueSerializerOptions = JsonWebSocketSerializer.SerializerOptions;

    private const byte FieldSeparator = 0x1F;  // unit separator
    private const byte RecordSeparator = 0x1E; // record separator
    private const byte NullMarker = 0x00;

    /// <summary>
    /// Computes the deterministic digest of the converged state reachable from <paramref name="root"/>.
    /// Uses the root's registry to enumerate known subjects. Returns an empty string when no registry
    /// is configured (no comparison possible).
    /// </summary>
    public static string Compute(IInterceptorSubject root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var registry = root.Context.TryGetService<ISubjectRegistry>();
        return registry is null ? string.Empty : Compute(registry);
    }

    /// <summary>
    /// Computes the deterministic digest over all known subjects in <paramref name="registry"/>.
    /// </summary>
    public static string Compute(ISubjectRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        // Snapshot id -> subject for every known subject that has a stable id; skip the rest.
        // A momentarily-unregistered subject still hashes consistently because property values are
        // read via the subject's own metadata below, not via the registry.
        var known = registry.KnownSubjects;
        var entries = new List<KeyValuePair<string, IInterceptorSubject>>(known.Count);
        foreach (var subject in known.Keys)
        {
            var id = subject.TryGetSubjectId();
            if (id is not null)
            {
                entries.Add(new KeyValuePair<string, IInterceptorSubject>(id, subject));
            }
        }

        // Deterministic order across participants: sort by stable id (ordinal).
        entries.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));

        using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        // Reusable scratch buffer for JSON value serialization to avoid per-property allocation churn.
        var valueWriter = new ArrayBufferWriter<byte>(256);

        // Reusable, name-sorted lists per subject (ordinal name sort for determinism).
        var valueProperties = new List<KeyValuePair<string, SubjectPropertyMetadata>>();
        var structuralProperties = new List<KeyValuePair<string, SubjectPropertyMetadata>>();

        foreach (var (subjectId, subject) in entries)
        {
            AppendUtf8(incrementalHash, subjectId);
            AppendSeparator(incrementalHash, RecordSeparator);

            valueProperties.Clear();
            structuralProperties.Clear();

            // Use the subject's own property metadata (registry-independent) so a momentarily
            // unregistered subject hashes the same as when it is registered.
            foreach (var entry in subject.Properties)
            {
                var metadata = entry.Value;

                // No getter (write-only) or derived (computed, not authoritative state): skip.
                // Mirrors the connector's ProcessSubjectFromMetadata fallback.
                if (metadata.GetValue is null || metadata.IsDerived)
                {
                    continue;
                }

                if (metadata.Type.CanContainSubjects())
                {
                    structuralProperties.Add(entry);
                }
                else
                {
                    valueProperties.Add(entry);
                }
            }

            valueProperties.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
            structuralProperties.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));

            foreach (var (name, metadata) in valueProperties)
            {
                AppendUtf8(incrementalHash, name);
                AppendSeparator(incrementalHash, FieldSeparator);
                AppendValue(incrementalHash, metadata.GetValue!.Invoke(subject), valueWriter);
                AppendSeparator(incrementalHash, FieldSeparator);
            }

            AppendSeparator(incrementalHash, RecordSeparator);

            foreach (var (name, metadata) in structuralProperties)
            {
                AppendUtf8(incrementalHash, name);
                AppendSeparator(incrementalHash, FieldSeparator);
                AppendStructure(incrementalHash, metadata, subject);
                AppendSeparator(incrementalHash, FieldSeparator);
            }

            AppendSeparator(incrementalHash, RecordSeparator);
        }

        return Convert.ToHexString(incrementalHash.GetHashAndReset());
    }

    private static void AppendStructure(
        IncrementalHash incrementalHash,
        SubjectPropertyMetadata metadata,
        IInterceptorSubject subject)
    {
        var value = metadata.GetValue!.Invoke(subject);
        var type = metadata.Type;

        if (type.IsSubjectDictionaryType())
        {
            // Dictionary: sorted key=childId pairs (membership without child contents).
            if (value is IDictionary dictionary)
            {
                var pairs = new List<string>(dictionary.Count);
                foreach (DictionaryEntry dictionaryEntry in dictionary)
                {
                    if (dictionaryEntry.Value is IInterceptorSubject childSubject)
                    {
                        var childId = childSubject.TryGetSubjectId() ?? "-";
                        pairs.Add($"{dictionaryEntry.Key}={childId}");
                    }
                }

                pairs.Sort(StringComparer.Ordinal);
                for (var i = 0; i < pairs.Count; i++)
                {
                    if (i > 0)
                    {
                        AppendSeparator(incrementalHash, FieldSeparator);
                    }

                    AppendUtf8(incrementalHash, pairs[i]);
                }
            }
        }
        else if (type.IsSubjectCollectionType())
        {
            // Collection: ordered child ids (membership and order, without child contents).
            if (value is IEnumerable enumerable)
            {
                var first = true;
                foreach (var item in enumerable)
                {
                    if (item is IInterceptorSubject childSubject)
                    {
                        if (!first)
                        {
                            AppendSeparator(incrementalHash, FieldSeparator);
                        }

                        first = false;
                        AppendUtf8(incrementalHash, childSubject.TryGetSubjectId() ?? "-");
                    }
                }
            }
        }
        else
        {
            // Object reference: the child id, or '-' when null.
            var childId = (value as IInterceptorSubject)?.TryGetSubjectId() ?? "-";
            AppendUtf8(incrementalHash, childId);
        }
    }

    private static void AppendValue(IncrementalHash incrementalHash, object? value, ArrayBufferWriter<byte> valueWriter)
    {
        if (value is null)
        {
            AppendSeparator(incrementalHash, NullMarker);
            return;
        }

        // Serialize with the protocol's JSON options so a live value and a value that round-tripped
        // through the protocol on the peer hash identically. System.Text.Json uses invariant culture
        // and shortest round-trippable numeric formatting, both deterministic.
        valueWriter.Clear();
        using (var writer = new Utf8JsonWriter(valueWriter))
        {
            JsonSerializer.Serialize(writer, value, value.GetType(), ValueSerializerOptions);
        }

        incrementalHash.AppendData(valueWriter.WrittenSpan);
    }

    private static void AppendUtf8(IncrementalHash incrementalHash, string text)
    {
        var maxBytes = Encoding.UTF8.GetMaxByteCount(text.Length);
        byte[]? rented = null;
        var buffer = maxBytes <= 256
            ? stackalloc byte[256]
            : (rented = ArrayPool<byte>.Shared.Rent(maxBytes));

        try
        {
            var written = Encoding.UTF8.GetBytes(text, buffer);
            incrementalHash.AppendData(buffer[..written]);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    private static void AppendSeparator(IncrementalHash incrementalHash, byte separator)
    {
        Span<byte> single = [separator];
        incrementalHash.AppendData(single);
    }
}
