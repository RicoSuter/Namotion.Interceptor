using System;
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
