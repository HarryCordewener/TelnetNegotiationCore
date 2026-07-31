using TUnit.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// THE PRIVACY GUARANTEE FOR ENVIRONMENT NEGOTIATION — issue #71. Do not delete these tests.
/// <para>
/// Both NEW-ENVIRON (RFC 1572) and ENVIRON (RFC 1408) used to answer a server's SEND with
/// <c>USER</c> taken from the operating system account of whoever was running the client, with no
/// opt-in and no way to discover it had happened. These tests assert on the bytes that actually
/// reach the wire: nothing is sent that the application did not supply, and the local account name
/// is never one of the things sent. A change that makes any of them fail is a regression of a
/// privacy leak, not a test that needs updating.
/// </para>
/// </summary>
public class EnvironPrivacyTests : BaseTest
{
	private static byte[] NewEnvironSend =>
	[
		(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.NEWENVIRON, (byte)Trigger.SEND,
		(byte)Trigger.IAC, (byte)Trigger.SE
	];

	private static byte[] EnvironSend =>
	[
		(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.ENVIRON, (byte)Trigger.SEND,
		(byte)Trigger.IAC, (byte)Trigger.SE
	];

	private static byte[] EmptyIs(Trigger option) =>
	[
		(byte)Trigger.IAC, (byte)Trigger.SB, (byte)option, (byte)Trigger.IS,
		(byte)Trigger.IAC, (byte)Trigger.SE
	];

	[Test]
	public async Task UnconfiguredNewEnvironClientSendsNoVariablesAtAll()
	{
		byte[] negotiation = null;

		var client = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(data => { negotiation = data.ToArray(); return ValueTask.CompletedTask; })
			.AddPlugin<NewEnvironProtocol>()
			.BuildAsync();

		await InterpretAndWaitAsync(client, [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NEWENVIRON]);
		negotiation = null;

		await InterpretAndWaitAsync(client, NewEnvironSend);

		await AssertByteArraysEqual(negotiation, EmptyIs(Trigger.NEWENVIRON));

		await client.DisposeAsync();
	}

	[Test]
	public async Task UnconfiguredEnvironClientSendsNoVariablesAtAll()
	{
		byte[] negotiation = null;

		var client = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(data => { negotiation = data.ToArray(); return ValueTask.CompletedTask; })
			.AddPlugin<EnvironProtocol>()
			.BuildAsync();

		await InterpretAndWaitAsync(client, [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.ENVIRON]);
		negotiation = null;

		await InterpretAndWaitAsync(client, EnvironSend);

		await AssertByteArraysEqual(negotiation, EmptyIs(Trigger.ENVIRON));

		await client.DisposeAsync();
	}

	/// <summary>
	/// The account name is planted in the environment the old code read from, and then looked for in
	/// every byte the client sends. This test fails on the pre-fix library and can only pass while
	/// no code path sources a value from the environment.
	/// </summary>
	[Test]
	[NotInParallel]
	public async Task TheOperatingSystemAccountNameIsNeverPutOnTheWire()
	{
		const string sentinel = "REAL-PERSONS-NAME-SENTINEL";
		var previousUser = Environment.GetEnvironmentVariable("USER");
		var previousLogname = Environment.GetEnvironmentVariable("LOGNAME");

		try
		{
			Environment.SetEnvironmentVariable("USER", sentinel);
			Environment.SetEnvironmentVariable("LOGNAME", sentinel);

			var everythingSent = new List<byte>();

			ValueTask Capture(ReadOnlyMemory<byte> data)
			{
				everythingSent.AddRange(data.ToArray());
				return ValueTask.CompletedTask;
			}

			var newEnviron = await new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Client)
				.UseLogger(logger)
				.OnSubmit(NoOpSubmitCallback)
				.OnNegotiation(Capture)
				.WithClientIdentity(new ClientIdentity("MUINDEX-CRAWLER"))
				.AddPlugin<NewEnvironProtocol>()
				.BuildAsync();

			await InterpretAndWaitAsync(newEnviron, [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NEWENVIRON]);
			await InterpretAndWaitAsync(newEnviron, NewEnvironSend);
			await newEnviron.DisposeAsync();

			var environ = await new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Client)
				.UseLogger(logger)
				.OnSubmit(NoOpSubmitCallback)
				.OnNegotiation(Capture)
				.WithClientIdentity(new ClientIdentity("MUINDEX-CRAWLER"))
				.AddPlugin<EnvironProtocol>()
				.BuildAsync();

			await InterpretAndWaitAsync(environ, [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.ENVIRON]);
			await InterpretAndWaitAsync(environ, EnvironSend);
			await environ.DisposeAsync();

			var wire = Encoding.ASCII.GetString(everythingSent.ToArray());

			await Assert.That(wire).DoesNotContain(sentinel);
			await Assert.That(wire).DoesNotContain(Environment.UserName);
			await Assert.That(wire).DoesNotContain("USER");
			await Assert.That(wire).DoesNotContain("LANG");
		}
		finally
		{
			Environment.SetEnvironmentVariable("USER", previousUser);
			Environment.SetEnvironmentVariable("LOGNAME", previousLogname);
		}
	}

	/// <summary>
	/// A server that asks for <c>USER</c> by name is told the client has none — not who is logged
	/// into the machine. RFC 1572's answer for a variable the responder does not have is its name
	/// with no value, and being asked is not consent to look one up.
	/// </summary>
	[Test]
	[NotInParallel]
	public async Task AskingForUserByNameStillDoesNotGetTheAccountName()
	{
		const string sentinel = "REAL-PERSONS-NAME-SENTINEL";
		var previousUser = Environment.GetEnvironmentVariable("USER");
		var previousLogname = Environment.GetEnvironmentVariable("LOGNAME");

		try
		{
			Environment.SetEnvironmentVariable("USER", sentinel);
			Environment.SetEnvironmentVariable("LOGNAME", sentinel);

			byte[] negotiation = null;

			var client = await new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Client)
				.UseLogger(logger)
				.OnSubmit(NoOpSubmitCallback)
				.OnNegotiation(data => { negotiation = data.ToArray(); return ValueTask.CompletedTask; })
				.WithClientIdentity(new ClientIdentity("MUINDEX-CRAWLER"))
				.AddPlugin<NewEnvironProtocol>()
				.BuildAsync();

			await InterpretAndWaitAsync(client, [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NEWENVIRON]);
			negotiation = null;

			var request = new List<byte>
			{
				(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.NEWENVIRON, (byte)Trigger.SEND,
				(byte)Trigger.NEWENVIRON_VAR
			};
			request.AddRange(Encoding.ASCII.GetBytes("USER"));
			request.Add((byte)Trigger.IAC);
			request.Add((byte)Trigger.SE);

			await InterpretAndWaitAsync(client, request.ToArray());

			// VAR USER, and no VALUE: "I do not have one."
			var expected = new List<byte>
			{
				(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.NEWENVIRON, (byte)Trigger.IS,
				(byte)Trigger.NEWENVIRON_VAR
			};
			expected.AddRange(Encoding.ASCII.GetBytes("USER"));
			expected.Add((byte)Trigger.IAC);
			expected.Add((byte)Trigger.SE);

			await AssertByteArraysEqual(negotiation, expected.ToArray());
			await Assert.That(Encoding.ASCII.GetString(negotiation)).DoesNotContain(sentinel);

			await client.DisposeAsync();
		}
		finally
		{
			Environment.SetEnvironmentVariable("USER", previousUser);
			Environment.SetEnvironmentVariable("LOGNAME", previousLogname);
		}
	}

	/// <summary>
	/// A source-level guard on the same invariant, so that reintroducing the leak in any protocol —
	/// not only the two this file drives — fails a test rather than shipping.
	/// </summary>
	[Test]
	public async Task NoProtocolReadsTheLocalAccountNameOrLocale()
	{
		var protocols = Path.Combine(FindRepositoryRoot(), "TelnetNegotiationCore", "Protocols");
		await Assert.That(Directory.Exists(protocols)).IsTrue();

		var offenders = new List<string>();
		foreach (var file in Directory.EnumerateFiles(protocols, "*.cs"))
		{
			var source = File.ReadAllText(file);
			if (source.Contains("Environment.UserName")
				|| source.Contains("Environment.GetEnvironmentVariable")
				|| source.Contains("CultureInfo.CurrentCulture"))
			{
				offenders.Add(Path.GetFileName(file));
			}
		}

		await Assert.That(offenders).IsEmpty();
	}

	private static string FindRepositoryRoot()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory != null && !File.Exists(Path.Combine(directory.FullName, "TelnetNegotiationCore.sln")))
		{
			directory = directory.Parent;
		}

		return directory?.FullName
			?? throw new InvalidOperationException(
				$"Could not locate the repository root above {AppContext.BaseDirectory}.");
	}

	[Test]
	public async Task ConfiguredVariablesAreSentIncludingMnesNames()
	{
		byte[] negotiation = null;

		var client = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(data => { negotiation = data.ToArray(); return ValueTask.CompletedTask; })
			.AddPlugin<NewEnvironProtocol>()
				.WithClientEnvironmentVariables(new Dictionary<string, string>
				{
					{ "CHARSET", "UTF-8" },
					{ "WORD_WRAP", "OFF" },
					{ "USER", "a-name-the-application-chose" }
				})
			.BuildAsync();

		await InterpretAndWaitAsync(client, [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NEWENVIRON]);
		negotiation = null;

		await InterpretAndWaitAsync(client, NewEnvironSend);

		var expected = new List<byte>
		{
			(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.NEWENVIRON, (byte)Trigger.IS
		};
		ClientIdentityTests.AppendVariable(expected, "CHARSET", "UTF-8");
		ClientIdentityTests.AppendVariable(expected, "WORD_WRAP", "OFF");
		ClientIdentityTests.AppendVariable(expected, "USER", "a-name-the-application-chose");
		expected.Add((byte)Trigger.IAC);
		expected.Add((byte)Trigger.SE);

		await AssertByteArraysEqual(negotiation, expected.ToArray());

		await client.DisposeAsync();
	}

	[Test]
	public async Task ExplicitVariablesWinOverTheIdentityDerivedOnes()
	{
		byte[] negotiation = null;

		var client = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(data => { negotiation = data.ToArray(); return ValueTask.CompletedTask; })
			.WithClientIdentity(new ClientIdentity("MUINDEX-CRAWLER") { Version = "1.2.0" })
			.AddPlugin<NewEnvironProtocol>()
				.WithClientEnvironmentVariables(new Dictionary<string, string>
				{
					{ "CLIENT_VERSION", "1.3.0-rc1" },
					{ "CHARSET", "UTF-8" }
				})
			.BuildAsync();

		await InterpretAndWaitAsync(client, [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NEWENVIRON]);
		negotiation = null;

		await InterpretAndWaitAsync(client, NewEnvironSend);

		var expected = new List<byte>
		{
			(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.NEWENVIRON, (byte)Trigger.IS
		};
		ClientIdentityTests.AppendVariable(expected, "CLIENT_NAME", "MUINDEX-CRAWLER");
		ClientIdentityTests.AppendVariable(expected, "CLIENT_VERSION", "1.3.0-rc1");
		// Nothing was claimed, so MTTS holds only what the library can see about itself:
		// 4 (decoding UTF-8) and 512 (this connection answers MNES).
		ClientIdentityTests.AppendVariable(expected, "MTTS", "516");
		ClientIdentityTests.AppendVariable(expected, "CHARSET", "UTF-8");
		expected.Add((byte)Trigger.IAC);
		expected.Add((byte)Trigger.SE);

		await AssertByteArraysEqual(negotiation, expected.ToArray());

		await client.DisposeAsync();
	}

	/// <summary>
	/// An empty answer is still an answer: the server's SEND handshake completes and its callback
	/// fires with no variables, which is the same position a server is in when talking to a client
	/// that never negotiated NEW-ENVIRON at all.
	/// </summary>
	[Test]
	public async Task TheSendHandshakeCompletesWithAnEmptyVariableMap()
	{
		byte[] clientAnswer = null;

		var client = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(data => { clientAnswer = data.ToArray(); return ValueTask.CompletedTask; })
			.AddPlugin<NewEnvironProtocol>()
			.BuildAsync();

		await InterpretAndWaitAsync(client, [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NEWENVIRON]);
		clientAnswer = null;
		await InterpretAndWaitAsync(client, NewEnvironSend);
		await Assert.That(clientAnswer).IsNotNull();

		Dictionary<string, string> receivedEnvVars = null;
		Dictionary<string, string> receivedUserVars = null;

		var server = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(_ => ValueTask.CompletedTask)
			.AddPlugin<NewEnvironProtocol>()
				.OnEnvironmentVariables((envVars, userVars) =>
				{
					receivedEnvVars = new Dictionary<string, string>(envVars);
					receivedUserVars = new Dictionary<string, string>(userVars);
					return ValueTask.CompletedTask;
				})
			.BuildAsync();

		await InterpretAndWaitAsync(server, [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NEWENVIRON]);
		await InterpretAndWaitAsync(server, clientAnswer);

		await Assert.That(receivedEnvVars).IsNotNull();
		await Assert.That(receivedEnvVars).IsEmpty();
		await Assert.That(receivedUserVars).IsEmpty();

		await client.DisposeAsync();
		await server.DisposeAsync();
	}

	/// <summary>
	/// MNES forbids the VAR, VALUE, ESC, USERVAR and IAC bytes inside a value, but an application
	/// supplies these strings, so a value carrying one must not be able to end the subnegotiation.
	/// </summary>
	[Test]
	public async Task AValueCarryingAControlByteIsEscapedRatherThanEndingTheSubnegotiation()
	{
		byte[] negotiation = null;

		var client = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(data => { negotiation = data.ToArray(); return ValueTask.CompletedTask; })
			.AddPlugin<NewEnvironProtocol>()
				.WithClientEnvironmentVariables(new Dictionary<string, string>
				{
					{ "CLIENT_NAME", "endshere" }
				})
			.BuildAsync();

		await InterpretAndWaitAsync(client, [(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.NEWENVIRON]);
		negotiation = null;

		await InterpretAndWaitAsync(client, NewEnvironSend);

		var expected = new List<byte>
		{
			(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.NEWENVIRON, (byte)Trigger.IS,
			(byte)Trigger.NEWENVIRON_VAR
		};
		expected.AddRange(Encoding.ASCII.GetBytes("CLIENT_NAME"));
		expected.Add((byte)Trigger.NEWENVIRON_VALUE);
		expected.AddRange(Encoding.ASCII.GetBytes("ends"));
		expected.Add((byte)Trigger.NEWENVIRON_ESC);
		expected.Add((byte)Trigger.NEWENVIRON_VALUE);
		expected.AddRange(Encoding.ASCII.GetBytes("here"));
		expected.Add((byte)Trigger.IAC);
		expected.Add((byte)Trigger.SE);

		await AssertByteArraysEqual(negotiation, expected.ToArray());

		await client.DisposeAsync();
	}
}
