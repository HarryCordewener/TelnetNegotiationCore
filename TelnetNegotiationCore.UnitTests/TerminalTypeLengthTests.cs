using System;
using System.Threading.Tasks;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// RFC 1091's limit on a terminal type name: "The maximum length of a terminal type name is 40
/// characters."
/// </summary>
/// <remarks>
/// <para>
/// Enforced where a name is configured rather than where it is sent, so that a mistake surfaces at
/// build time with the offending value named instead of becoming a non-conforming frame on the wire.
/// There are three places a name can come from — <see cref="TerminalTypeProtocol.WithTerminalTypes"/>,
/// <see cref="ClientIdentity.Name"/> and <see cref="ClientIdentity.TerminalType"/> — and all three
/// validate, which is why nothing needs checking again at send time.
/// </para>
/// <para>
/// Rejecting rather than truncating is safe because nothing legitimate comes close. The MTTS cycle
/// sends a client name, a terminal type and an <c>MTTS &lt;bitvector&gt;</c> claim; the longest
/// values in real use are terminal types like <c>XTERM-256COLOR</c> at 14 characters. No real client
/// is anywhere near the boundary, so an exception cannot wrongly refuse one.
/// </para>
/// <para>
/// The receive side stays liberal on purpose: the limit constrains senders, and a peer that exceeds
/// it should still be understood.
/// </para>
/// </remarks>
public class TerminalTypeLengthTests : BaseTest
{
	/// <summary>41 characters — one past the limit.</summary>
	private const string TooLong = "ABCDEFGHIJABCDEFGHIJABCDEFGHIJABCDEFGHIJX";

	/// <summary>40 characters — exactly the limit, which is allowed.</summary>
	private const string ExactlyAtTheLimit = "ABCDEFGHIJABCDEFGHIJABCDEFGHIJABCDEFGHIJ";

	[Test]
	public async Task WithTerminalTypesRefusesAnOverLongName()
	{
		var protocol = new TerminalTypeProtocol();

		await Assert.That(() => protocol.WithTerminalTypes("XTERM", TooLong))
			.Throws<ArgumentException>();
	}

	/// <summary>The message has to name the offending value, or the exception is a puzzle.</summary>
	[Test]
	public async Task TheRefusalNamesTheOffendingValue()
	{
		var protocol = new TerminalTypeProtocol();

		var message = string.Empty;

		try
		{
			protocol.WithTerminalTypes("XTERM", TooLong);
		}
		catch (ArgumentException ex)
		{
			message = ex.Message;
		}

		await Assert.That(message).Contains(TooLong);
		await Assert.That(message).Contains("40");
	}

	[Test]
	public async Task WithTerminalTypesAcceptsANameExactlyAtTheLimit()
	{
		var protocol = new TerminalTypeProtocol();

		protocol.WithTerminalTypes(ExactlyAtTheLimit);

		await Assert.That(protocol).IsNotNull();
	}

	[Test]
	public async Task WithTerminalTypesAcceptsRealisticNames()
	{
		var protocol = new TerminalTypeProtocol();

		// What the MTTS cycle actually sends.
		protocol.WithTerminalTypes("TINTIN++", "XTERM-256COLOR", "MTTS 2815");

		await Assert.That(protocol).IsNotNull();
	}

	[Test]
	public async Task ClientIdentityRefusesAnOverLongName()
	{
		await Assert.That(() => new ClientIdentity(TooLong))
			.Throws<ArgumentException>();
	}

	[Test]
	public async Task ClientIdentityRefusesAnOverLongTerminalType()
	{
		await Assert.That(() => new ClientIdentity("MyClient") { TerminalType = TooLong })
			.Throws<ArgumentException>();
	}

	[Test]
	public async Task ClientIdentityAcceptsRealisticValues()
	{
		var identity = new ClientIdentity("MyClient")
		{
			TerminalType = "XTERM-256COLOR",
			Version = "1.2.3",
		};

		await Assert.That(identity.Name).IsEqualTo("MyClient");
		await Assert.That(identity.TerminalType).IsEqualTo("XTERM-256COLOR");
	}

	/// <summary>
	/// <see cref="ClientIdentity.Version"/> is not constrained by RFC 1091: it goes to MNES as
	/// <c>CLIENT_VERSION</c>, not into a TTYPE response, and MNES sets no such limit.
	/// </summary>
	[Test]
	public async Task ClientIdentityDoesNotConstrainTheVersion()
	{
		var identity = new ClientIdentity("MyClient") { Version = TooLong };

		await Assert.That(identity.Version).IsEqualTo(TooLong);
	}
}
