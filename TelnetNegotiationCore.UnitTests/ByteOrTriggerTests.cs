using System;
using System.Threading.Tasks;
using TelnetNegotiationCore.Models;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// <see cref="ByteOrTrigger"/> is the parameter of every parameterized trigger. It is a C# union on
/// every target: the BCL supplies <c>UnionAttribute</c> and <c>IUnion</c> from .NET 11, and the library
/// defines them itself below that.
/// </summary>
public class ByteOrTriggerTests : BaseTest
{
	[Test]
	public async Task AByteComesBackAsAByte()
	{
		ByteOrTrigger b = (byte)'A';

		await Assert.That(Describe(b)).IsEqualTo("byte 65");
		await Assert.That(b.TryGetValue(out Trigger _)).IsFalse();
	}

	[Test]
	public async Task ATriggerComesBackAsATrigger()
	{
		ByteOrTrigger t = Trigger.Error;

		await Assert.That(Describe(t)).IsEqualTo("trigger Error");
		await Assert.That(t.TryGetValue(out byte _)).IsFalse();
	}

	/// <summary>
	/// Most triggers are numbered after the byte they stand for, so the number alone cannot tell the
	/// two cases apart. <c>IAC</c> the trigger and 255 the byte must still stay distinct.
	/// </summary>
	[Test]
	public async Task ATriggerNumberedLikeAByteIsStillATrigger()
	{
		ByteOrTrigger t = Trigger.IAC;
		ByteOrTrigger b = (byte)255;

		await Assert.That(Describe(t)).IsEqualTo("trigger IAC");
		await Assert.That(Describe(b)).IsEqualTo("byte 255");
	}

	[Test]
	public async Task ValueHoldsTheCaseTypeAndDefaultHoldsNothing()
	{
		await Assert.That(((ByteOrTrigger)(byte)7).Value).IsEqualTo((object)(byte)7);
		await Assert.That(((ByteOrTrigger)Trigger.Error).Value).IsEqualTo((object)Trigger.Error);
		await Assert.That(default(ByteOrTrigger).HasValue).IsFalse();
		await Assert.That(default(ByteOrTrigger).Value).IsNull();
	}

	/// <summary>The transition trace log prints the parameter, so it should read as the value.</summary>
	[Test]
	public async Task ToStringIsTheValue()
	{
		await Assert.That(((ByteOrTrigger)(byte)65).ToString()).IsEqualTo("65");
		await Assert.That(((ByteOrTrigger)Trigger.Error).ToString()).IsEqualTo("Error");
		await Assert.That(default(ByteOrTrigger).ToString()).IsEqualTo(string.Empty);
	}

	/// <summary>
	/// Constructing and matching one allocates nothing. A <c>union ByteOrTrigger(byte, Trigger);</c>
	/// declaration would box every byte into its <c>object?</c> field -- 24 bytes each on x64.
	/// </summary>
	[Test]
	public async Task ConstructingAndMatchingDoesNotAllocate()
	{
		long sum = 0;
		for (var i = 0; i < 1_000; i++) sum += Number((byte)i);

		var before = GC.GetAllocatedBytesForCurrentThread();
		for (var i = 0; i < 100_000; i++) sum += Number((byte)i);
		var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

		await Assert.That(sum).IsNotEqualTo(0);
		await Assert.That(allocated).IsEqualTo(0);
	}

	private static string Describe(ByteOrTrigger x) => x switch
	{
		byte b => $"byte {b}",
		Trigger t => $"trigger {t}",
	};

	private static int Number(ByteOrTrigger x) => x switch
	{
		byte b => b,
		Trigger t => (int)t,
	};
}
