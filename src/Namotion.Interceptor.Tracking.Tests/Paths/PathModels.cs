using System;
using System.Collections;
using System.Collections.Generic;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Tracking.Tests.Paths;

[InterceptorSubject]
public partial class Node
{
    public Node() { Name = string.Empty; Children = []; ByName = new(); }

    public partial string Name { get; set; }
    public partial Node? Child { get; set; }
    public partial Node[] Children { get; set; }
    public partial Dictionary<string, Node> ByName { get; set; }
    public int PlainField;                 // not a property
    public int Index { get; set; }         // used to build an invalid index-arg expression
}

[InterceptorSubject]
public partial class GridHolder
{
    public GridHolder() { Grid = new Node[0, 0]; Rows = []; }

    public partial Node[,] Grid { get; set; }           // multi-dimensional indexer
    public partial List<List<Node>> Rows { get; set; }  // nested indexer receiver
    public partial int Number { get; set; }             // non-subject intermediate target
}

// Shaped as root.Child(A).Value for the transaction retrack-stranding regression. Value is a
// [Derived]-with-setter: a write to it is not staged by an active transaction (IsDerived bypasses
// staging) and dispatches immediately, so it can drive the path event walk while a transaction holds
// a staged reassignment of the watched Child intermediate.
[InterceptorSubject]
public partial class TransactionNode
{
    // Partial properties cannot use "= ..." initializers; initialize in the constructor.
    public TransactionNode() { Value = string.Empty; }

    public partial TransactionNode? Child { get; set; }

    [Derived]
    public partial string Value { get; set; }
}

// Two non-derived leaves for the failed-commit-with-rollback test: writing "poison" to Reject makes
// its apply throw, so a Rollback commit reverts the already-applied Accept write.
[InterceptorSubject]
public partial class RollbackNode
{
    public RollbackNode() { Accept = string.Empty; Reject = string.Empty; }

    public partial string Accept { get; set; }

    public partial string Reject { get; set; }

    // The setter runs on both the transaction staging write and the commit apply write. Staging must
    // succeed (the value is only captured, never terminally written) so the change reaches the apply pass;
    // the throw is therefore gated to the apply pass, which runs with the transaction committing (or no
    // ambient transaction at all). This drives the Rollback revert of the already-applied Accept write.
    partial void OnRejectChanging(ref string newValue, ref bool cancel)
    {
        var transaction = SubjectTransaction.Current;
        if (newValue == "poison" && (transaction is null || transaction.IsCommitting))
        {
            throw new InvalidOperationException("Reject rejects the poison value during apply.");
        }
    }
}

// A subject leaf-holder whose equality is by Identity (not reference), so two distinct instances with the
// same Identity compare equal. This drives the intermediate equality-suppression carve-out: a derived
// intermediate recomputing to a distinct-but-equal instance is suppressed by the equality-check handler.
[InterceptorSubject]
public partial class EqualityNode
{
    public EqualityNode() { Name = string.Empty; Identity = string.Empty; }

    public EqualityNode(string identity) { Name = string.Empty; Identity = identity; }

    public partial string Name { get; set; }

    // Non-partial (untracked): only participates in equality, never on any watched path.
    public string Identity { get; init; }

    public override bool Equals(object? obj) => obj is EqualityNode other && other.Identity == Identity;

    public override int GetHashCode() => Identity.GetHashCode();
}

// A derived subject-typed intermediate whose child is held ONLY via the derived property (the three
// EqualityNode fields are referenced by no intercepted property anywhere), so the child is never attached
// by context inheritance and stays dormant until a recalculation reconciles it via the derived write.
// Selector drives the recomputation: 0 -> alpha, 1 -> beta (Equals-distinct from alpha), >=2 -> betaClone
// (a distinct instance that is Equals-equal to beta).
[InterceptorSubject]
public partial class DerivedExclusiveHolder
{
    private readonly EqualityNode _alpha;
    private readonly EqualityNode _beta;
    private readonly EqualityNode _betaClone;

    public DerivedExclusiveHolder()
    {
        _alpha = new EqualityNode("alpha") { Name = "A0" };
        _beta = new EqualityNode("beta") { Name = "B0" };
        _betaClone = new EqualityNode("beta") { Name = "C0" };
        Selector = 0;
    }

    public partial int Selector { get; set; }

    [Derived]
    public EqualityNode Exclusive => Selector <= 0 ? _alpha : (Selector == 1 ? _beta : _betaClone);

    // Plain accessors (untracked) so the test can reach the field-held children.
    public EqualityNode Alpha => _alpha;

    public EqualityNode Beta => _beta;

    public EqualityNode BetaClone => _betaClone;
}

// A derived intermediate that ALIASES an intercepted-held child: Thrower returns the intercepted Backing
// (so the child is attached via that alias and delivers immediately), unless the untracked ThrowOnGet
// toggle is set, in which case its getter throws during the walk.
[InterceptorSubject]
public partial class GetterThrowHolder
{
    public GetterThrowHolder() { }

    // Plain field (untracked): toggled in place so it never dispatches a watched change of its own.
    public bool ThrowOnGet;

    public partial Node? Backing { get; set; }

    [Derived]
    public Node? Thrower => ThrowOnGet ? throw new InvalidOperationException("getter boom") : Backing;
}

// A hostile string comparer whose members throw once armed. Injected into a standard dictionary so the
// walk's TryGetValue (which honors the dictionary's comparer) throws mid-walk.
public sealed class ThrowingStringComparer : IEqualityComparer<string>
{
    public bool Throw;

    public bool Equals(string? x, string? y)
        => Throw ? throw new InvalidOperationException("comparer boom") : StringComparer.Ordinal.Equals(x, y);

    public int GetHashCode(string obj)
        => Throw ? throw new InvalidOperationException("comparer boom") : StringComparer.Ordinal.GetHashCode(obj);
}

// A hostile reference collection whose Count and indexer throw once armed. Implements the non-generic
// IList so the walk's IndexReferenceCollection takes the IList branch and the throw surfaces there.
public sealed class HostileList<T> : IList<T>, IReadOnlyList<T>, IList
    where T : class
{
    private readonly List<T> _inner;

    public HostileList(IEnumerable<T> items) => _inner = new List<T>(items);

    public bool Throw;

    public int Count => Throw ? throw new InvalidOperationException("count boom") : _inner.Count;

    public T this[int index]
    {
        get => Throw ? throw new InvalidOperationException("indexer boom") : _inner[index];
        set => _inner[index] = value;
    }

    object? IList.this[int index]
    {
        get => this[index];
        set => this[index] = (T)value!;
    }

    public IEnumerator<T> GetEnumerator() => _inner.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _inner.GetEnumerator();

    public bool IsReadOnly => false;

    public bool IsFixedSize => false;

    public bool IsSynchronized => false;

    public object SyncRoot => this;

    public int IndexOf(T item) => _inner.IndexOf(item);

    public void Insert(int index, T item) => _inner.Insert(index, item);

    public void RemoveAt(int index) => _inner.RemoveAt(index);

    public void Add(T item) => _inner.Add(item);

    public void Clear() => _inner.Clear();

    public bool Contains(T item) => _inner.Contains(item);

    public void CopyTo(T[] array, int arrayIndex) => _inner.CopyTo(array, arrayIndex);

    public bool Remove(T item) => _inner.Remove(item);

    int IList.Add(object? value) { _inner.Add((T)value!); return _inner.Count - 1; }

    bool IList.Contains(object? value) => value is T item && _inner.Contains(item);

    int IList.IndexOf(object? value) => value is T item ? _inner.IndexOf(item) : -1;

    void IList.Insert(int index, object? value) => _inner.Insert(index, (T)value!);

    void IList.Remove(object? value) { if (value is T item) _inner.Remove(item); }

    void ICollection.CopyTo(Array array, int index) => ((ICollection)_inner).CopyTo(array, index);
}

// A subject holding a hostile reference collection as a subject collection property.
[InterceptorSubject]
public partial class HostileContainerHolder
{
    public HostileContainerHolder() { Items = new HostileList<Node>(Array.Empty<Node>()); }

    public partial HostileList<Node> Items { get; set; }
}
