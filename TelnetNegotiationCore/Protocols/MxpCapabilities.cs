using System;
using System.Collections.Generic;
using System.Linq;

namespace TelnetNegotiationCore.Protocols;

/// <summary>
/// What a client said it can render, from its <c>&lt;SUPPORTS&gt;</c> reply. An entry is a tag
/// (<c>image</c>) or one of a tag's arguments (<c>send.expire</c>), and a reply answers only what was
/// asked: anything never asked about is in neither set.
/// </summary>
/// <param name="Supported">The entries the client answered with <c>+</c>.</param>
/// <param name="Unsupported">The entries the client answered with <c>-</c>.</param>
public sealed class MxpSupport
{
    private readonly HashSet<string> _supported;
    private readonly HashSet<string> _unsupported;

    /// <param name="supported">The entries the client answered with <c>+</c>.</param>
    /// <param name="unsupported">The entries the client answered with <c>-</c>.</param>
    public MxpSupport(IEnumerable<string> supported, IEnumerable<string> unsupported)
    {
        if (supported is null) throw new ArgumentNullException(nameof(supported));
        if (unsupported is null) throw new ArgumentNullException(nameof(unsupported));

        _supported = new HashSet<string>(supported, StringComparer.OrdinalIgnoreCase);
        _unsupported = new HashSet<string>(unsupported, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Nothing asked, nothing answered.</summary>
    public static readonly MxpSupport None = new([], []);

    /// <summary>The entries the client answered with <c>+</c>.</summary>
    public IReadOnlyCollection<string> Supported => _supported;

    /// <summary>The entries the client answered with <c>-</c>.</summary>
    public IReadOnlyCollection<string> Unsupported => _unsupported;

    /// <summary>Whether the client said it supports <paramref name="entry"/>, e.g. <c>image</c> or <c>send.expire</c>.</summary>
    public bool Supports(string entry) => _supported.Contains(entry);

    /// <summary>Whether the client said it does not support <paramref name="entry"/>.</summary>
    public bool Refuses(string entry) => _unsupported.Contains(entry);

    /// <summary>This report with <paramref name="other"/>'s answers applied over it, for a second query.</summary>
    public MxpSupport With(MxpSupport other)
    {
        if (other is null) throw new ArgumentNullException(nameof(other));

        var supported = new HashSet<string>(_supported, StringComparer.OrdinalIgnoreCase);
        var unsupported = new HashSet<string>(_unsupported, StringComparer.OrdinalIgnoreCase);

        foreach (var entry in other._supported)
        {
            unsupported.Remove(entry);
            supported.Add(entry);
        }

        foreach (var entry in other._unsupported)
        {
            supported.Remove(entry);
            unsupported.Add(entry);
        }

        return new MxpSupport(supported, unsupported);
    }

    /// <summary>The reply as it goes on the wire: <c>&lt;SUPPORTS +a -b&gt;</c>.</summary>
    public override string ToString() =>
        "<SUPPORTS" + string.Concat(_supported.Select(e => " +" + e)) + string.Concat(_unsupported.Select(e => " -" + e)) + ">";
}

/// <summary>
/// What a client said about itself in its <c>&lt;VERSION&gt;</c> reply. Every field is optional: the
/// specification fixes the shape of the reply, not which of its attributes a client sends.
/// </summary>
/// <param name="Mxp">The MXP version the client implements, e.g. <c>0.4</c>.</param>
/// <param name="Style">The style-sheet version, if it sent one.</param>
/// <param name="Client">The client's name, e.g. <c>zmud</c>.</param>
/// <param name="Version">The client's own version.</param>
/// <param name="Registered">Whether the client reported itself registered.</param>
public sealed record MxpVersion(string? Mxp, string? Style, string? Client, string? Version, bool? Registered)
{
    /// <summary>The reply as it goes on the wire, carrying only the fields that are set.</summary>
    public override string ToString()
    {
        var fields = new List<string>(5);
        if (Mxp is not null) fields.Add("MXP=" + Mxp);
        if (Style is not null) fields.Add("STYLE=" + Style);
        if (Client is not null) fields.Add("CLIENT=" + Client);
        if (Version is not null) fields.Add("VERSION=" + Version);
        if (Registered is { } registered) fields.Add("REGISTERED=" + (registered ? "yes" : "no"));
        return fields.Count == 0 ? "<VERSION>" : "<VERSION " + string.Join(" ", fields) + ">";
    }
}
