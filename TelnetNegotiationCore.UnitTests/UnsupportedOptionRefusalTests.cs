using System.Collections.Generic;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// An option nothing here handles is refused, and the refusal has to name the option that was offered.
/// </summary>
/// <remarks>
/// A byte with no <see cref="Trigger"/> of its own reaches the state machine as
/// <see cref="Trigger.ReadNextCharacter"/>, whose value is 256. Writing that trigger back as the option
/// byte truncates it to 0 -- <c>IAC DONT BINARY</c> -- so the peer is refused an option it never
/// offered and never hears an answer about the one it did.
/// </remarks>
public class UnsupportedOptionRefusalTests : BaseTest
{
	[Test]
	[Arguments(TelnetInterpreter.TelnetMode.Client)]
	[Arguments(TelnetInterpreter.TelnetMode.Server)]
	public async Task AWillForAnOptionWithNoTriggerIsRefusedByItsOwnNumber(TelnetInterpreter.TelnetMode mode)
	{
		var sent = await AnswerTo(mode, [(byte)Trigger.IAC, (byte)Trigger.WILL, 200]);

		await AssertByteArraysEqual(sent, [(byte)Trigger.IAC, (byte)Trigger.DONT, 200]);
	}

	[Test]
	[Arguments(TelnetInterpreter.TelnetMode.Client)]
	[Arguments(TelnetInterpreter.TelnetMode.Server)]
	public async Task ADoForAnOptionWithNoTriggerIsRefusedByItsOwnNumber(TelnetInterpreter.TelnetMode mode)
	{
		var sent = await AnswerTo(mode, [(byte)Trigger.IAC, (byte)Trigger.DO, 200]);

		await AssertByteArraysEqual(sent, [(byte)Trigger.IAC, (byte)Trigger.WONT, 200]);
	}

	/// <summary>
	/// A named option whose plugin is not attached takes the same path, and always named itself
	/// correctly; pinned so the fix for the unnamed case cannot break it.
	/// </summary>
	[Test]
	public async Task AWillForANamedOptionWithNoPluginIsRefusedByItsOwnNumber()
	{
		var sent = await AnswerTo(TelnetInterpreter.TelnetMode.Client, [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MSDP]);

		await AssertByteArraysEqual(sent, [(byte)Trigger.IAC, (byte)Trigger.DONT, (byte)Trigger.MSDP]);
	}

	private static async Task<byte[]> AnswerTo(TelnetInterpreter.TelnetMode mode, byte[] offer)
	{
		var sent = new List<byte>();

		var interpreter = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
			.UseMode(mode)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(data =>
			{
				lock (sent) sent.AddRange(data.ToArray());
				return ValueTask.CompletedTask;
			}));

		await InterpretAndWaitAsync(interpreter, offer);
		await PollUntilAsync(() => sent.Count >= 3);
		await interpreter.DisposeAsync();

		lock (sent) return [.. sent];
	}
}
