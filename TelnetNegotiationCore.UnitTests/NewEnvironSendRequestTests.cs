using TUnit.Core;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// RFC 1572's SEND command asks for particular variables, and the reply owes it an answer for each
/// one: <i>"If a list of variables is specified, then only those variables should be sent"</i>, in
/// the order they were asked for, with a requested variable that is undefined answered by its name
/// carrying no value.
/// </summary>
public class NewEnvironSendRequestTests : BaseTest
{
	private static byte[] Send(params (bool IsUserVar, string Name)[] requested)
	{
		var request = new List<byte>
		{
			(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.NEWENVIRON, (byte)Trigger.SEND
		};

		foreach (var (isUserVar, name) in requested)
		{
			request.Add(isUserVar ? (byte)Trigger.NEWENVIRON_USERVAR : (byte)Trigger.NEWENVIRON_VAR);
			request.AddRange(Encoding.ASCII.GetBytes(name));
		}

		request.Add((byte)Trigger.IAC);
		request.Add((byte)Trigger.SE);
		return request.ToArray();
	}

	private static List<byte> IsReply() =>
	[
		(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.NEWENVIRON, (byte)Trigger.IS
	];

	private static byte[] Ended(List<byte> reply)
	{
		reply.Add((byte)Trigger.IAC);
		reply.Add((byte)Trigger.SE);
		return reply.ToArray();
	}

	private static void AppendUndefined(List<byte> target, bool isUserVar, string name)
	{
		target.Add(isUserVar ? (byte)Trigger.NEWENVIRON_USERVAR : (byte)Trigger.NEWENVIRON_VAR);
		target.AddRange(Encoding.ASCII.GetBytes(name));
	}

	private static void AppendEmpty(List<byte> target, string name)
	{
		target.Add((byte)Trigger.NEWENVIRON_VAR);
		target.AddRange(Encoding.ASCII.GetBytes(name));
		target.Add((byte)Trigger.NEWENVIRON_VALUE);
	}

	private static async Task<(TelnetInterpreter Client, Func<byte[]> LastNegotiation)> ConfiguredClientAsync(
		Dictionary<string, string> variables, ClientIdentity identity = null)
	{
		byte[] negotiation = null;

		var builder = new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(data => { negotiation = data.ToArray(); return ValueTask.CompletedTask; });

		if (identity != null)
		{
			builder = builder.WithClientIdentity(identity);
		}

		var client = await builder
			.AddPlugin<NewEnvironProtocol>()
				.WithClientEnvironmentVariables(variables)
			.BuildAsync();

		await InterpretAndWaitAsync(client, [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NEWENVIRON]);
		negotiation = null;

		return (client, () => negotiation);
	}

	[Test]
	public async Task OnlyTheRequestedVariablesAreSent()
	{
		var (client, negotiation) = await ConfiguredClientAsync(new Dictionary<string, string>
		{
			{ "CHARSET", "UTF-8" },
			{ "WORD_WRAP", "OFF" },
			{ "IPADDRESS", "203.0.113.7" }
		});

		await InterpretAndWaitAsync(client, Send((false, "WORD_WRAP")));

		var expected = IsReply();
		ClientIdentityTests.AppendVariable(expected, "WORD_WRAP", "OFF");

		await AssertByteArraysEqual(negotiation(), Ended(expected));

		await client.DisposeAsync();
	}

	[Test]
	public async Task VariablesComeBackInTheOrderTheyWereAskedFor()
	{
		var (client, negotiation) = await ConfiguredClientAsync(new Dictionary<string, string>
		{
			{ "CHARSET", "UTF-8" },
			{ "WORD_WRAP", "OFF" }
		},
		new ClientIdentity("MUINDEX-CRAWLER"));

		await InterpretAndWaitAsync(client, Send((false, "WORD_WRAP"), (false, "CLIENT_NAME"), (false, "CHARSET")));

		var expected = IsReply();
		ClientIdentityTests.AppendVariable(expected, "WORD_WRAP", "OFF");
		ClientIdentityTests.AppendVariable(expected, "CLIENT_NAME", "MUINDEX-CRAWLER");
		ClientIdentityTests.AppendVariable(expected, "CHARSET", "UTF-8");

		await AssertByteArraysEqual(negotiation(), Ended(expected));

		await client.DisposeAsync();
	}

	/// <summary>
	/// RFC 1572: a requested variable with no value in the reply is how the responder says it has
	/// none. Answering nothing at all would leave the server waiting for a variable it asked for.
	/// </summary>
	[Test]
	public async Task ARequestedVariableThatIsNotConfiguredComesBackUndefined()
	{
		var (client, negotiation) = await ConfiguredClientAsync(new Dictionary<string, string>
		{
			{ "CHARSET", "UTF-8" }
		});

		await InterpretAndWaitAsync(client, Send((false, "PRINTER"), (false, "CHARSET"), (true, "SOMETHING")));

		var expected = IsReply();
		AppendUndefined(expected, false, "PRINTER");
		ClientIdentityTests.AppendVariable(expected, "CHARSET", "UTF-8");
		AppendUndefined(expected, true, "SOMETHING");

		await AssertByteArraysEqual(negotiation(), Ended(expected));

		await client.DisposeAsync();
	}

	/// <summary>
	/// A variable configured with an empty value is defined, and says so with an empty VALUE — which
	/// is a different answer from the one above.
	/// </summary>
	[Test]
	public async Task AConfiguredEmptyValueIsDefinedRatherThanUndefined()
	{
		var (client, negotiation) = await ConfiguredClientAsync(new Dictionary<string, string>
		{
			{ "WORD_WRAP", string.Empty }
		});

		await InterpretAndWaitAsync(client, Send((false, "WORD_WRAP"), (false, "CHARSET")));

		var expected = IsReply();
		AppendEmpty(expected, "WORD_WRAP");
		AppendUndefined(expected, false, "CHARSET");

		await AssertByteArraysEqual(negotiation(), Ended(expected));

		await client.DisposeAsync();
	}

	/// <summary>
	/// RFC 1572: <i>"If one of the variables has no name, then all the variables of that type ...
	/// should be sent."</i>
	/// </summary>
	[Test]
	public async Task ATypeWithNoNameAsksForEveryVariableOfThatType()
	{
		var (client, negotiation) = await ConfiguredClientAsync(new Dictionary<string, string>
		{
			{ "CHARSET", "UTF-8" },
			{ "WORD_WRAP", "OFF" }
		});

		await InterpretAndWaitAsync(client, Send((false, string.Empty)));

		var expected = IsReply();
		ClientIdentityTests.AppendVariable(expected, "CHARSET", "UTF-8");
		ClientIdentityTests.AppendVariable(expected, "WORD_WRAP", "OFF");

		await AssertByteArraysEqual(negotiation(), Ended(expected));

		await client.DisposeAsync();
	}

	/// <summary>
	/// Everything this library sends is a well-known VAR, MNES's own names included, so a request
	/// for every USERVAR is answered by there being none.
	/// </summary>
	[Test]
	public async Task EveryUserVariableIsAnEmptyAnswer()
	{
		var (client, negotiation) = await ConfiguredClientAsync(new Dictionary<string, string>
		{
			{ "CHARSET", "UTF-8" }
		});

		await InterpretAndWaitAsync(client, Send((true, string.Empty)));

		await AssertByteArraysEqual(negotiation(), Ended(IsReply()));

		await client.DisposeAsync();
	}

	/// <summary>
	/// RFC 1572: <i>"If no list is specified, the default environment ... should be sent."</i>
	/// </summary>
	[Test]
	public async Task ASendWithNoListStillAsksForEverything()
	{
		var (client, negotiation) = await ConfiguredClientAsync(new Dictionary<string, string>
		{
			{ "CHARSET", "UTF-8" },
			{ "WORD_WRAP", "OFF" }
		});

		await InterpretAndWaitAsync(client, Send());

		var expected = IsReply();
		ClientIdentityTests.AppendVariable(expected, "CHARSET", "UTF-8");
		ClientIdentityTests.AppendVariable(expected, "WORD_WRAP", "OFF");

		await AssertByteArraysEqual(negotiation(), Ended(expected));

		await client.DisposeAsync();
	}

	[Test]
	public async Task ASecondRequestIsAnsweredOnItsOwnTerms()
	{
		var (client, negotiation) = await ConfiguredClientAsync(new Dictionary<string, string>
		{
			{ "CHARSET", "UTF-8" },
			{ "WORD_WRAP", "OFF" }
		});

		await InterpretAndWaitAsync(client, Send((false, "CHARSET")));

		var first = IsReply();
		ClientIdentityTests.AppendVariable(first, "CHARSET", "UTF-8");
		await AssertByteArraysEqual(negotiation(), Ended(first));

		await InterpretAndWaitAsync(client, Send((false, "WORD_WRAP")));

		var second = IsReply();
		ClientIdentityTests.AppendVariable(second, "WORD_WRAP", "OFF");
		await AssertByteArraysEqual(negotiation(), Ended(second));

		await client.DisposeAsync();
	}

	/// <summary>
	/// The RFC 1408 client honours a named request the same way, minus USERVAR, which its own state
	/// machine does not accept in a request.
	/// </summary>
	[Test]
	public async Task TheRfc1408ClientHonoursANamedRequestToo()
	{
		byte[] negotiation = null;

		var client = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(data => { negotiation = data.ToArray(); return ValueTask.CompletedTask; })
			.AddPlugin<EnvironProtocol>()
				.WithClientEnvironmentVariables(new Dictionary<string, string>
				{
					{ "TERM", "xterm-256color" },
					{ "DISPLAY", "localhost:0" }
				})
			.BuildAsync();

		await InterpretAndWaitAsync(client, [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.ENVIRON]);
		negotiation = null;

		var request = new List<byte>
		{
			(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.ENVIRON, (byte)Trigger.SEND,
			(byte)Trigger.NEWENVIRON_VAR
		};
		request.AddRange(Encoding.ASCII.GetBytes("DISPLAY"));
		request.Add((byte)Trigger.NEWENVIRON_VAR);
		request.AddRange(Encoding.ASCII.GetBytes("ACCT"));
		request.Add((byte)Trigger.IAC);
		request.Add((byte)Trigger.SE);

		await InterpretAndWaitAsync(client, request.ToArray());

		var expected = new List<byte>
		{
			(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.ENVIRON, (byte)Trigger.IS
		};
		ClientIdentityTests.AppendVariable(expected, "DISPLAY", "localhost:0");
		AppendUndefined(expected, false, "ACCT");

		await AssertByteArraysEqual(negotiation, Ended(expected));

		await client.DisposeAsync();
	}
}
