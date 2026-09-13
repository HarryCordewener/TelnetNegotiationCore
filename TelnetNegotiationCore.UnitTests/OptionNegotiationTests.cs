using System.Linq;
using System.Threading.Tasks;
using TelnetNegotiationCore.Helpers;
using TelnetNegotiationCore.Models;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// RFC 854's answer pairing, which RFC 1143 requires be given rather than withheld.
/// </summary>
/// <remarks>
/// The pairing is asymmetric and easy to get backwards — a <c>DO</c> is answered with
/// <c>WILL</c>/<c>WONT</c> and a <c>WILL</c> with <c>DO</c>/<c>DONT</c> — which is the reason it now
/// lives in one place instead of being written out wherever it was needed. Two separate web sources
/// consulted while settling this got the pairing wrong, so it is pinned here.
/// </remarks>
public class OptionNegotiationTests
{
	private const byte OPTION = 201;

	[Test]
	public async Task ADoIsHonouredWithWill()
	{
		var answer = OptionNegotiation.AnswerFor(honour: true, (byte)Trigger.DO, OPTION);

		await Assert.That(answer!.SequenceEqual(new byte[] { 255, (byte)Trigger.WILL, OPTION })).IsTrue();
	}

	[Test]
	public async Task ADoIsRefusedWithWont()
	{
		var answer = OptionNegotiation.AnswerFor(honour: false, (byte)Trigger.DO, OPTION);

		await Assert.That(answer!.SequenceEqual(new byte[] { 255, (byte)Trigger.WONT, OPTION })).IsTrue();
	}

	[Test]
	public async Task AWillIsHonouredWithDo()
	{
		var answer = OptionNegotiation.AnswerFor(honour: true, (byte)Trigger.WILL, OPTION);

		await Assert.That(answer!.SequenceEqual(new byte[] { 255, (byte)Trigger.DO, OPTION })).IsTrue();
	}

	[Test]
	public async Task AWillIsRefusedWithDont()
	{
		var answer = OptionNegotiation.AnswerFor(honour: false, (byte)Trigger.WILL, OPTION);

		await Assert.That(answer!.SequenceEqual(new byte[] { 255, (byte)Trigger.DONT, OPTION })).IsTrue();
	}

	/// <summary>
	/// A refusal gets no answer. RFC 1143 warns that answering one invites a loop, and there is
	/// nothing to say: the peer has already declined.
	/// </summary>
	[Test]
	[Arguments((byte)Trigger.DONT)]
	[Arguments((byte)Trigger.WONT)]
	public async Task ARefusalIsNotAnswered(byte verb)
	{
		await Assert.That(OptionNegotiation.AnswerFor(honour: true, verb, OPTION)).IsNull();
		await Assert.That(OptionNegotiation.AnswerFor(honour: false, verb, OPTION)).IsNull();
	}

	/// <summary>Anything that is not a negotiation verb is not answered either.</summary>
	[Test]
	public async Task ANonVerbIsNotAnswered()
	{
		await Assert.That(OptionNegotiation.AnswerFor(honour: true, (byte)Trigger.SB, OPTION)).IsNull();
	}

	/// <summary>The option byte is carried through unchanged, whatever it is.</summary>
	[Test]
	[Arguments((byte)0)]
	[Arguments((byte)69)]
	[Arguments((byte)200)]
	[Arguments((byte)255)]
	public async Task TheOptionByteIsCarriedThrough(byte option)
	{
		var answer = OptionNegotiation.AnswerFor(honour: false, (byte)Trigger.DO, option);

		await Assert.That(answer![2]).IsEqualTo(option);
	}
}
