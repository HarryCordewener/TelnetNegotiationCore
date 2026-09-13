using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// RFC 2066's optional translation-table prefix on a CHARSET <c>REQUEST</c>.
/// </summary>
/// <remarks>
/// <para>
/// The format is <c>IAC SB CHARSET REQUEST { "[TTABLE ]" &lt;Version&gt; } &lt;char set list&gt; IAC
/// SE</c>, and the prose says "if the string [TTABLE] appears, the sender is willing to accept a
/// mapping (translation table)". The RFC writes the literal one way in its format line and another
/// in its prose, so both spellings are accepted here — being strict about which would reject real
/// peers for an ambiguity in the specification.
/// </para>
/// <para>
/// Without the prefix stripped, the <c>[</c> is read as the separator octet and the entire charset
/// list becomes one unrecognised name, so a peer offering a translation table alongside charsets
/// this library supports was rejected outright. That is worth fixing rather than recording, because
/// <c>TTABLE-IS</c> is already implemented — the library could parse the table it was refusing to
/// let anyone offer.
/// </para>
/// </remarks>
public class CharsetTTablePrefixTests : BaseTest
{
	private static readonly byte[] WillCharset =
		[(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.CHARSET];

	private static byte[] Request(string body) =>
	[
		(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.REQUEST,
		.. Encoding.ASCII.GetBytes(body),
		(byte)Trigger.IAC, (byte)Trigger.SE,
	];

	/// <summary>A REQUEST carrying the prefix, its version octet, then the separated charset list.</summary>
	private static byte[] RequestWithTTable(string prefix, byte version, string list) =>
	[
		(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.REQUEST,
		.. Encoding.ASCII.GetBytes(prefix),
		version,
		.. Encoding.ASCII.GetBytes(list),
		(byte)Trigger.IAC, (byte)Trigger.SE,
	];

	private static readonly byte[] AcceptedUtf8 =
	[
		(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.ACCEPTED,
		.. "utf-8"u8,
		(byte)Trigger.IAC, (byte)Trigger.SE,
	];

	private static readonly byte[] Rejected =
	[
		(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.REJECTED,
		(byte)Trigger.IAC, (byte)Trigger.SE,
	];

	/// <summary>Drives a server through WILL then the given REQUEST, returning what it replied.</summary>
	private static async Task<byte[]> ServerReplyToAsync(byte[] request)
	{
		byte[] reply = null;

		ValueTask Capture(ReadOnlyMemory<byte> data)
		{
			reply = data.ToArray();
			return ValueTask.CompletedTask;
		}

		var server = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit((data, enc, ti) => ValueTask.CompletedTask)
			.OnNegotiation(Capture)
			.AddPlugin<CharsetProtocol>()
				.WithCharsetOrder([Encoding.UTF8])
			.BuildAsync();

		await server.InterpretByteArrayAsync(WillCharset);
		await server.WaitForProcessingAsync();

		reply = null;
		await server.InterpretByteArrayAsync(request);
		await server.WaitForProcessingAsync();

		await server.DisposeAsync();
		return reply;
	}

	[Test]
	public async Task APlainRequestIsStillAccepted()
	{
		var reply = await ServerReplyToAsync(Request(";utf-8;us-ascii"));

		await AssertByteArraysEqual(reply, AcceptedUtf8);
	}

	[Test]
	public async Task ARequestPrefixedWithTTableIsAccepted()
	{
		var reply = await ServerReplyToAsync(RequestWithTTable("[TTABLE]", 1, ";utf-8;us-ascii"));

		await AssertByteArraysEqual(reply, AcceptedUtf8);
	}

	/// <summary>The RFC's format line spells the literal with a space before the bracket.</summary>
	[Test]
	public async Task ARequestPrefixedWithTTableAndASpaceIsAccepted()
	{
		var reply = await ServerReplyToAsync(RequestWithTTable("[TTABLE ]", 1, ";utf-8;us-ascii"));

		await AssertByteArraysEqual(reply, AcceptedUtf8);
	}

	/// <summary>
	/// RFC 2066 says the version octet "must not be zero". A sender that puts zero there is broken,
	/// but its charset list may be perfectly good, so the offer of a table is ignored and the list
	/// is still honoured rather than the whole message refused.
	/// </summary>
	[Test]
	public async Task AZeroVersionIgnoresTheTableOfferButStillHonoursTheList()
	{
		var reply = await ServerReplyToAsync(RequestWithTTable("[TTABLE]", 0, ";utf-8;us-ascii"));

		await AssertByteArraysEqual(reply, AcceptedUtf8);
	}

	/// <summary>The prefix and a version, with no list after it, offers nothing to choose from.</summary>
	[Test]
	public async Task ATTablePrefixWithNoCharsetListIsRejected()
	{
		var reply = await ServerReplyToAsync(RequestWithTTable("[TTABLE]", 1, string.Empty));

		await AssertByteArraysEqual(reply, Rejected);
	}

	/// <summary>The prefix with not even a version octet is malformed.</summary>
	[Test]
	public async Task ATTablePrefixWithNoVersionIsRejected()
	{
		var reply = await ServerReplyToAsync(Request("[TTABLE]"));

		await AssertByteArraysEqual(reply, Rejected);
	}

	/// <summary>
	/// A charset list whose separator happens to be an opening bracket is legal — RFC 2066 lets the
	/// sender pick any octet but IAC — and must not be mistaken for the prefix.
	/// </summary>
	[Test]
	public async Task ASeparatorThatLooksLikeTheStartOfThePrefixStillWorks()
	{
		var reply = await ServerReplyToAsync(Request("[utf-8[us-ascii"));

		await AssertByteArraysEqual(reply, AcceptedUtf8);
	}
}
