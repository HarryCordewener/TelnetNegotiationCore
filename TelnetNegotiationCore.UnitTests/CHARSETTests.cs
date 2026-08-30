using Microsoft.Extensions.Logging;
using TUnit.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;

namespace TelnetNegotiationCore.UnitTests
{
	
	public class CharsetTests() : BaseTest
	{
		private ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => ValueTask.CompletedTask;

		/// <summary>
		/// Escapes IAC (0xFF/255) bytes by doubling them, matching what a remote Telnet endpoint would send over the wire.
		/// </summary>
		private static byte[] EscapeIACBytes(byte[] input)
		{
			int count255 = input.Count(b => b == 255);
			if (count255 == 0) return input;
			var result = new byte[input.Length + count255];
			int writePos = 0;
			foreach (var bt in input)
			{
				if (bt == 255) result[writePos++] = 255;
				result[writePos++] = bt;
			}
			return result;
		}

		[Test]
		[MethodDataSource(nameof(ServerCHARSETSequences))]
		public async Task ServerEvaluationCheck(IEnumerable<byte[]> clientSends, IEnumerable<byte[]> serverShouldRespondWith, IEnumerable<Encoding> currentEncoding)
		{
			byte[] negotiationOutput = null;
			
			ValueTask CaptureNegotiation(ReadOnlyMemory<byte> data)
			{
				negotiationOutput = data.ToArray();
				return ValueTask.CompletedTask;
			}
			
			var server_ti = await new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Server)
				.UseLogger(logger)
				.OnSubmit((data, enc, ti) => throw new NotImplementedException())
				.OnNegotiation(CaptureNegotiation)
				.AddPlugin<CharsetProtocol>()
				.BuildAsync();

			if (clientSends.Count() != serverShouldRespondWith.Count())
				throw new Exception("Invalid Testcase.");

			foreach ((var clientSend, var serverShouldRespond, var shouldHaveCurrentEncoding) in clientSends.Zip(serverShouldRespondWith, currentEncoding))
			{
				negotiationOutput = null;
				foreach (var x in clientSend ?? Enumerable.Empty<byte>())
				{
					await server_ti.InterpretAsync(x);
				}
				await server_ti.WaitForProcessingAsync();

				await Assert.That(server_ti.CurrentEncoding).IsEqualTo(shouldHaveCurrentEncoding);
				await AssertByteArraysEqual(negotiationOutput, serverShouldRespond);
			}
			
			await server_ti.DisposeAsync();
		}

		[Test]
		[MethodDataSource(nameof(ClientCHARSETSequences))]
		public async Task ClientEvaluationCheck(IEnumerable<byte[]> serverSends, IEnumerable<byte[]> serverShouldRespondWith, IEnumerable<Encoding> currentEncoding)
		{
			byte[] negotiationOutput = null;
			
			ValueTask CaptureNegotiation(ReadOnlyMemory<byte> data)
			{
				negotiationOutput = data.ToArray();
				return ValueTask.CompletedTask;
			}
			
			var client_ti = await new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Client)
				.UseLogger(logger)
				.OnSubmit((data, enc, ti) => throw new NotImplementedException())
				.OnNegotiation(CaptureNegotiation)
				.AddPlugin<CharsetProtocol>()
				.BuildAsync();

			var charsetPlugin = client_ti.PluginManager!.GetPlugin<CharsetProtocol>();
			charsetPlugin!.CharsetOrder = new[] { Encoding.GetEncoding("utf-8"), Encoding.GetEncoding("iso-8859-1") };

			if (serverSends.Count() != serverShouldRespondWith.Count())
				throw new Exception("Invalid Testcase.");

			foreach ((var serverSend, var clientShouldRespond, var shouldHaveCurrentEncoding) in serverSends.Zip(serverShouldRespondWith, currentEncoding))
			{
				negotiationOutput = null;
				foreach (var x in serverSend ?? Enumerable.Empty<byte>())
				{
					await client_ti.InterpretAsync(x);
				}
				await client_ti.WaitForProcessingAsync();

				await Assert.That(client_ti.CurrentEncoding).IsEqualTo(shouldHaveCurrentEncoding);
				await AssertByteArraysEqual(negotiationOutput, clientShouldRespond);
			}
			await client_ti.DisposeAsync();
		}

		public static IEnumerable<(IEnumerable<byte[]>, IEnumerable<byte[]>, IEnumerable<Encoding>)> ClientCHARSETSequences()
		{
			yield return (
				new[]
				{
					new [] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.CHARSET }
				},
				new[]
				{
					new [] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.CHARSET }
				},
				new[] // Registered CHARSET List After Negotiation
				{
					Encoding.UTF8,
				});
			yield return (
				new byte[][]
				{
					[(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.CHARSET],
					[(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.CHARSET],
					[(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.ACCEPTED, (byte)'u', (byte)'t', (byte)'f', (byte)'-', (byte)'8', (byte)Trigger.IAC, (byte)Trigger.SE]

				},
				new byte[][]
				{
					[(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.CHARSET],
					[(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.REQUEST,
					 (byte)';', (byte)'u', (byte)'t', (byte)'f', (byte)'-', (byte)'8',
					 (byte)';', (byte)'i', (byte)'s', (byte)'o', (byte)'-', (byte)'8', (byte)'8',(byte)'5', (byte)'9',(byte)'-', (byte)'1',
					 (byte)';', (byte)'u', (byte)'t', (byte)'f', (byte)'-', (byte)'1', (byte)'6',
					 (byte)';', (byte)'u', (byte)'t', (byte)'f', (byte)'-', (byte)'1', (byte)'6',(byte)'B', (byte)'E',
					 (byte)';', (byte)'u', (byte)'t', (byte)'f', (byte)'-', (byte)'3', (byte)'2',
					 (byte)';', (byte)'u', (byte)'t', (byte)'f', (byte)'-', (byte)'3', (byte)'2',(byte)'B', (byte)'E',
					 (byte)';', (byte)'u', (byte)'s', (byte)'-', (byte)'a', (byte)'s', (byte)'c',(byte)'i', (byte)'i',
					 (byte)Trigger.IAC, (byte)Trigger.SE],
					null
				},
				new[] // Registered CHARSET List After Negotiation
				{
					Encoding.UTF8,
					Encoding.UTF8,
					Encoding.UTF8
				});
		}

		public static IEnumerable<(IEnumerable<byte[]>, IEnumerable<byte[]>, IEnumerable<Encoding>)> ServerCHARSETSequences()
		{
			yield return (
				new[] { // Client Sends
					new [] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.CHARSET }
				},
				new[] { // Server Should Respond With
					new [] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.CHARSET }
				},
				new[] // Registered CHARSET List After Negotiation
				{
					Encoding.UTF8
				});
			yield return (
				new byte[][] { // Client Sends
					[(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.CHARSET ],
					[(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.REQUEST,
					 (byte)';', (byte)'u', (byte)'t', (byte)'f', (byte)'-', (byte)'1', (byte)'6',
					 (byte)';', (byte)'u', (byte)'t', (byte)'f', (byte)'-', (byte)'1', (byte)'6',(byte)'B', (byte)'E',
					 (byte)';', (byte)'u', (byte)'t', (byte)'f', (byte)'-', (byte)'3', (byte)'2',(byte)'B', (byte)'E',
					 (byte)';', (byte)'u', (byte)'t', (byte)'f', (byte)'-', (byte)'3', (byte)'2',
					 (byte)';', (byte)'u', (byte)'t', (byte)'f', (byte)'-', (byte)'8',
					 (byte)';', (byte)'i', (byte)'s', (byte)'o', (byte)'-', (byte)'8', (byte)'8',(byte)'5', (byte)'9',(byte)'-', (byte)'1',
					 (byte)';', (byte)'u', (byte)'s', (byte)'-', (byte)'a', (byte)'s', (byte)'c',(byte)'i', (byte)'i',
					 (byte)Trigger.IAC, (byte)Trigger.SE ]
				},
				new[] { // Server Should Respond With
					[(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.CHARSET],
					new [] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.ACCEPTED, 
						(byte)'u', (byte)'t', (byte)'f', (byte)'-', (byte)'1', (byte)'6', 
						(byte)Trigger.IAC, (byte)Trigger.SE }
				},
				new[] // Registered CHARSET List After Negotiation
				{
					Encoding.UTF8,
					Encoding.GetEncoding("UTF-16")
				});
			yield return (
				new byte[][] { // Client Sends
					[(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.CHARSET],
					[ (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.REQUEST,
						(byte)';', (byte)'u', (byte)'t', (byte)'f', (byte)'-', (byte)'8',
						(byte)';', (byte)'a', (byte)'n', (byte)'s', (byte)'i',
						(byte)Trigger.IAC, (byte)Trigger.SE ]
				},
				new byte[][] { // Server Should Respond With
					[(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.CHARSET],
					[(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.ACCEPTED, 
						(byte)'u', (byte)'t', (byte)'f', (byte)'-', (byte)'8', 
						(byte)Trigger.IAC, (byte)Trigger.SE]
				},
				new[] // Registered CHARSET List After Negotiation
				{
					Encoding.UTF8,
					Encoding.UTF8
				});
		}

		private static bool ContainsSequence(IReadOnlyList<byte> haystack, byte[] needle)
		{
			for (var i = 0; i + needle.Length <= haystack.Count; i++)
			{
				var match = true;
				for (var j = 0; j < needle.Length; j++)
				{
					if (haystack[i + j] != needle[j]) { match = false; break; }
				}
				if (match) return true;
			}
			return false;
		}

		/// <summary>
		/// RFC 2066 CHARSET is initiated by the server (WILL CHARSET), and the client responds.
		/// A client must NOT proactively send WILL CHARSET: if both peers offer WILL CHARSET the
		/// negotiation collides and never resolves, and a stuck CHARSET state can cause a server to
		/// discard the client's first line (observed against SharpMUSH: the login line was dropped and
		/// the login screen redrawn). Only the server initiates.
		/// </summary>
		[Test]
		public async Task ClientDoesNotProactivelyOfferCharset()
		{
			var initialNegotiation = new List<byte>();
			ValueTask CaptureNegotiation(ReadOnlyMemory<byte> data)
			{
				initialNegotiation.AddRange(data.ToArray());
				return ValueTask.CompletedTask;
			}

			var client = await new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Client)
				.UseLogger(logger)
				.OnSubmit(WriteBackToOutput)
				.OnNegotiation(CaptureNegotiation)
				.AddPlugin<CharsetProtocol>()
				.BuildAsync();

			var willCharset = new[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.CHARSET };
			await Assert.That(ContainsSequence(initialNegotiation, willCharset)).IsFalse();

			await client.DisposeAsync();
		}

		/// <summary>
		/// The server side still initiates CHARSET by offering WILL CHARSET on connect, so a
		/// responding client can negotiate an encoding. This guards against "fixing" the collision
		/// by disabling CHARSET entirely.
		/// </summary>
		[Test]
		public async Task ServerProactivelyOffersCharset()
		{
			var initialNegotiation = new List<byte>();
			ValueTask CaptureNegotiation(ReadOnlyMemory<byte> data)
			{
				initialNegotiation.AddRange(data.ToArray());
				return ValueTask.CompletedTask;
			}

			var server = await new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Server)
				.UseLogger(logger)
				.OnSubmit(WriteBackToOutput)
				.OnNegotiation(CaptureNegotiation)
				.AddPlugin<CharsetProtocol>()
				.BuildAsync();

			var willCharset = new[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.CHARSET };
			await Assert.That(ContainsSequence(initialNegotiation, willCharset)).IsTrue();

			await server.DisposeAsync();
		}

		// New tests for character encoding with IAC escaping

		[Test]
		public async Task ServerAndClientHandleUTF8WithIACEscaping()
		{
			await TestEncodingWithIACEscaping(
				Encoding.UTF8,
				"utf-8",
				"Hello, World! \u00FF Test with byte 255: \u00FF\u00FF",
				TelnetInterpreter.TelnetMode.Server);

			await TestEncodingWithIACEscaping(
				Encoding.UTF8,
				"utf-8",
				"Client UTF-8 test \u00FF with special chars: \u00E9\u00E8\u00E0",
				TelnetInterpreter.TelnetMode.Client);
		}

		[Test]
		public async Task ServerAndClientHandleUTF16WithIACEscaping()
		{
			await TestEncodingWithIACEscaping(
				Encoding.Unicode, // UTF-16 LE
				"utf-16",
				"UTF-16 Test \u00FF Special: \u4E2D\u6587",
				TelnetInterpreter.TelnetMode.Server);

			await TestEncodingWithIACEscaping(
				Encoding.Unicode,
				"utf-16",
				"Client UTF-16 \u00FF\u00FF Test",
				TelnetInterpreter.TelnetMode.Client);
		}

		[Test]
		public async Task ServerAndClientHandleLatin1WithIACEscaping()
		{
			var latin1 = Encoding.GetEncoding("ISO-8859-1");
			await TestEncodingWithIACEscaping(
				latin1,
				"iso-8859-1",
				"Latin-1: \u00E9\u00E8\u00E0 \u00FF Byte 255 here!",
				TelnetInterpreter.TelnetMode.Server);

			await TestEncodingWithIACEscaping(
				latin1,
				"iso-8859-1",
				"Client Latin-1 \u00FF test",
				TelnetInterpreter.TelnetMode.Client);
		}

		[Test]
		public async Task ServerAndClientHandleASCIIWithIACEscaping()
		{
			// ASCII test with basic ASCII characters
			await TestEncodingWithIACEscaping(
				Encoding.ASCII,
				"us-ascii",
				"ASCII Test: Hello World!",
				TelnetInterpreter.TelnetMode.Server);

			await TestEncodingWithIACEscaping(
				Encoding.ASCII,
				"us-ascii",
				"Client ASCII test",
				TelnetInterpreter.TelnetMode.Client);
		}

		[Test]
		public async Task ServerAndClientHandleBinaryDataWithIACEscaping()
		{
			// Test binary data containing actual byte 255 (IAC) with UTF-8 encoding
			var receivedData = new List<(byte[] data, Encoding encoding)>();
			
			ValueTask CaptureOutput(byte[] data, Encoding enc, TelnetInterpreter ti)
			{
				receivedData.Add((data, enc));
				return ValueTask.CompletedTask;
			}

			byte[] negotiationOutput = null;
			
			ValueTask CaptureNegotiation(ReadOnlyMemory<byte> data)
			{
				negotiationOutput = data.ToArray();
				return ValueTask.CompletedTask;
			}

			var server = await new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Server)
				.UseLogger(logger)
				.OnSubmit(CaptureOutput)
				.OnNegotiation(CaptureNegotiation)
				.AddPlugin<CharsetProtocol>()
				.BuildAsync();

			// Negotiate UTF-8
			await NegotiateCharset(server, "utf-8", TelnetInterpreter.TelnetMode.Server);
			await server.WaitForProcessingAsync();

			// Create binary data with actual byte 255
			var binaryData = new byte[] { 72, 101, 108, 108, 111, 255, 87, 111, 114, 108, 100 }; // "Hello[255]World"
			
			// Escape the IAC bytes
			var escapedData = EscapeIACBytes(binaryData);
			
			// Verify IAC was doubled
			var originalIACCount = binaryData.Count(b => b == 255);
			var escapedIACCount = escapedData.Count(b => b == 255);
			await Assert.That(originalIACCount).IsEqualTo(1);
			await Assert.That(escapedIACCount).IsEqualTo(2);

			// Send the escaped data with newline
			var withNewline = escapedData.Concat(new byte[] { (byte)'\n' }).ToArray();
			await server.InterpretByteArrayAsync(withNewline);
			await server.WaitForProcessingAsync();

			// Verify the data was received correctly (IAC unescaped)
			await Assert.That(receivedData.Count).IsGreaterThan(0);
			var received = receivedData.Last();
			await AssertByteArraysEqual(received.data, binaryData);

			await server.DisposeAsync();
		}

		[Test]
		public async Task ServerCanSwitchBetweenEncodings()
		{
			var receivedData = new List<(byte[] data, Encoding encoding)>();
			byte[] negotiationOutput = null;
			
			ValueTask CaptureOutput(byte[] data, Encoding enc, TelnetInterpreter ti)
			{
				receivedData.Add((data, enc));
				return ValueTask.CompletedTask;
			}

			ValueTask CaptureNegotiation(ReadOnlyMemory<byte> data)
			{
				negotiationOutput = data.ToArray();
				return ValueTask.CompletedTask;
			}

			var server = await new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Server)
				.UseLogger(logger)
				.OnSubmit(CaptureOutput)
				.OnNegotiation(CaptureNegotiation)
				.AddPlugin<CharsetProtocol>()
				.BuildAsync();

			// Test switching from ASCII -> UTF-8 -> Latin-1 -> UTF-16
			var encodings = new[] {
				(Encoding.ASCII, "us-ascii", "ASCII"),
				(Encoding.UTF8, "utf-8", "UTF-8 \u00E9"),
				(Encoding.GetEncoding("ISO-8859-1"), "iso-8859-1", "Latin-1 \u00E0"),
				(Encoding.Unicode, "utf-16", "UTF-16 \u4E2D")
			};

			foreach (var (targetEncoding, webName, testString) in encodings)
			{
				// Negotiate charset
				receivedData.Clear();
				await NegotiateCharset(server, webName, TelnetInterpreter.TelnetMode.Server);
				await server.WaitForProcessingAsync();

				// Verify encoding was changed
				await Assert.That(server.CurrentEncoding.WebName).IsEqualTo(targetEncoding.WebName);

				// Send test data
				var testBytes = targetEncoding.GetBytes(testString);
				var escapedBytes = EscapeIACBytes(testBytes);
				
				// Add newline to trigger OnSubmit
				var withNewline = escapedBytes.Concat(new byte[] { (byte)'\n' }).ToArray();
				await server.InterpretByteArrayAsync(withNewline);
				await server.WaitForProcessingAsync();

				// Verify data was received correctly
				await Assert.That(receivedData.Count).IsGreaterThan(0);
				var received = receivedData.Last();
				var receivedString = received.encoding.GetString(received.data);
				await Assert.That(receivedString).IsEqualTo(testString);
			}

			await server.DisposeAsync();
		}

		[Test]
		public async Task ClientCanSwitchBetweenEncodings()
		{
			var receivedData = new List<(byte[] data, Encoding encoding)>();
			byte[] negotiationOutput = null;
			
			ValueTask CaptureOutput(byte[] data, Encoding enc, TelnetInterpreter ti)
			{
				receivedData.Add((data, enc));
				return ValueTask.CompletedTask;
			}

			ValueTask CaptureNegotiation(ReadOnlyMemory<byte> data)
			{
				negotiationOutput = data.ToArray();
				return ValueTask.CompletedTask;
			}

			var client = await new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Client)
				.UseLogger(logger)
				.OnSubmit(CaptureOutput)
				.OnNegotiation(CaptureNegotiation)
				.AddPlugin<CharsetProtocol>()
				.BuildAsync();

			var charsetPlugin = client.PluginManager!.GetPlugin<CharsetProtocol>();
			charsetPlugin!.CharsetOrder = new[] { 
				Encoding.GetEncoding("utf-8"), 
				Encoding.GetEncoding("iso-8859-1"),
				Encoding.GetEncoding("utf-16"),
				Encoding.ASCII
			};

			// Test switching from ASCII -> UTF-8 -> Latin-1 -> UTF-16
			var encodings = new[] {
				(Encoding.ASCII, "us-ascii", "ASCII"),
				(Encoding.UTF8, "utf-8", "UTF-8 \u00E9"),
				(Encoding.GetEncoding("ISO-8859-1"), "iso-8859-1", "Latin-1 \u00E0"),
				(Encoding.Unicode, "utf-16", "UTF-16 \u4E2D")
			};

			foreach (var (targetEncoding, webName, testString) in encodings)
			{
				// Negotiate charset
				receivedData.Clear();
				await NegotiateCharset(client, webName, TelnetInterpreter.TelnetMode.Client);
				await client.WaitForProcessingAsync();

				// Verify encoding was changed
				await Assert.That(client.CurrentEncoding.WebName).IsEqualTo(targetEncoding.WebName);

				// Send test data
				var testBytes = targetEncoding.GetBytes(testString);
				var escapedBytes = EscapeIACBytes(testBytes);
				
				// Add newline to trigger OnSubmit
				var withNewline = escapedBytes.Concat(new byte[] { (byte)'\n' }).ToArray();
				await client.InterpretByteArrayAsync(withNewline);
				await client.WaitForProcessingAsync();

				// Verify data was received correctly
				await Assert.That(receivedData.Count).IsGreaterThan(0);
				var received = receivedData.Last();
				var receivedString = received.encoding.GetString(received.data);
				await Assert.That(receivedString).IsEqualTo(testString);
			}

			await client.DisposeAsync();
		}

		/// <summary>
		/// A client learns its encoding from the server's CHARSET ACCEPTED, which is a different code
		/// path to the server's CHARSET REQUEST. Both must tell the consumer the encoding moved -
		/// otherwise a client that queues text to encode after negotiation (RFC 2066 page 9: "While a
		/// CHARSET subnegotiation is in progress, data SHOULD be queued") never learns it may send.
		/// </summary>
		[Test]
		public async Task ClientReportsCharsetChangeWhenServerAcceptsOurCharset()
		{
			var reported = new List<Encoding>();

			var client = await new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Client)
				.UseLogger(logger)
				.OnSubmit(WriteBackToOutput)
				.OnNegotiation(_ => ValueTask.CompletedTask)
				.AddPlugin<CharsetProtocol>()
				.BuildAsync();

			var charsetPlugin = client.PluginManager!.GetPlugin<CharsetProtocol>();
			charsetPlugin!.OnCharsetChange(encoding =>
			{
				reported.Add(encoding);
				return ValueTask.CompletedTask;
			});

			await NegotiateCharset(client, "iso-8859-1", TelnetInterpreter.TelnetMode.Client);
			await client.WaitForProcessingAsync();

			await Assert.That(client.CurrentEncoding.WebName).IsEqualTo("iso-8859-1");
			await Assert.That(reported.Count).IsEqualTo(1);
			await Assert.That(reported[0].WebName).IsEqualTo("iso-8859-1");

			await client.DisposeAsync();
		}

		/// <summary>
		/// The server side of <see cref="ClientReportsCharsetChangeWhenServerAcceptsOurCharset"/>:
		/// choosing an encoding from the peer's CHARSET REQUEST reports it too.
		/// </summary>
		[Test]
		public async Task ServerReportsCharsetChangeWhenItChoosesFromTheOfferedCharsets()
		{
			var reported = new List<Encoding>();

			var server = await new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Server)
				.UseLogger(logger)
				.OnSubmit(WriteBackToOutput)
				.OnNegotiation(_ => ValueTask.CompletedTask)
				.AddPlugin<CharsetProtocol>()
				.BuildAsync();

			var charsetPlugin = server.PluginManager!.GetPlugin<CharsetProtocol>();
			charsetPlugin!.OnCharsetChange(encoding =>
			{
				reported.Add(encoding);
				return ValueTask.CompletedTask;
			});

			await NegotiateCharset(server, "iso-8859-1", TelnetInterpreter.TelnetMode.Server);
			await server.WaitForProcessingAsync();

			await Assert.That(server.CurrentEncoding.WebName).IsEqualTo("iso-8859-1");
			await Assert.That(reported.Count).IsEqualTo(1);
			await Assert.That(reported[0].WebName).IsEqualTo("iso-8859-1");

			await server.DisposeAsync();
		}

		/// <summary>
		/// A consumer's callback throwing is the consumer's bug, but it must not leave the connection
		/// holding two different ideas of what encoding it is on. The plugin and the interpreter have
		/// to agree either way, because the interpreter's copy is the one every line is labelled from.
		/// </summary>
		[Test]
		public async Task AThrowingCharsetChangeCallbackStillLeavesTheClientEncodingApplied()
		{
			var receivedData = new List<(byte[] data, Encoding encoding)>();

			ValueTask CaptureOutput(byte[] data, Encoding enc, TelnetInterpreter ti)
			{
				receivedData.Add((data, enc));
				return ValueTask.CompletedTask;
			}

			var client = await new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Client)
				.UseLogger(logger)
				.OnSubmit(CaptureOutput)
				.OnNegotiation(_ => ValueTask.CompletedTask)
				.AddPlugin<CharsetProtocol>()
				.BuildAsync();

			var charsetPlugin = client.PluginManager!.GetPlugin<CharsetProtocol>();
			charsetPlugin!.OnCharsetChange(_ => throw new InvalidOperationException("consumer blew up"));

			await NegotiateCharset(client, "iso-8859-1", TelnetInterpreter.TelnetMode.Client);
			await client.WaitForProcessingAsync();

			// The interpreter's encoding is what consumers read and what lines are labelled from, so a
			// throwing notification must not leave it behind the plugin's.
			await Assert.That(charsetPlugin.CurrentEncoding.WebName).IsEqualTo("iso-8859-1");
			await Assert.That(client.CurrentEncoding.WebName).IsEqualTo("iso-8859-1");

			// The concrete harm if it did lag: every later line carries the stale label.
			await client.InterpretByteArrayAsync(Encoding.Latin1.GetBytes("über\n"));
			await client.WaitForProcessingAsync();

			await Assert.That(receivedData.Count).IsEqualTo(1);
			await Assert.That(receivedData[0].encoding.WebName).IsEqualTo("iso-8859-1");
			await Assert.That(receivedData[0].encoding.GetString(receivedData[0].data)).IsEqualTo("über");

			await client.DisposeAsync();
		}

		/// <summary>
		/// The server's side of the same hazard, which costs more than divergent state: the CHARSET
		/// ACCEPTED reply is what terminates the subnegotiation for the peer (RFC 2066: "Receipt of a
		/// CHARSET ACCEPTED or TTABLE-ACK message terminates the subnegotiation, with the new character
		/// set in force"). A consumer throwing out of the change notification must not swallow it and
		/// leave the peer waiting on a reply that never comes.
		/// </summary>
		[Test]
		public async Task AThrowingCharsetChangeCallbackStillSendsTheServersAcceptedReply()
		{
			var negotiationOutput = new List<byte[]>();

			var server = await new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Server)
				.UseLogger(logger)
				.OnSubmit(WriteBackToOutput)
				.OnNegotiation(data => { negotiationOutput.Add(data.ToArray()); return ValueTask.CompletedTask; })
				.AddPlugin<CharsetProtocol>()
				.BuildAsync();

			var charsetPlugin = server.PluginManager!.GetPlugin<CharsetProtocol>();
			charsetPlugin!.OnCharsetChange(_ => throw new InvalidOperationException("consumer blew up"));

			await NegotiateCharset(server, "iso-8859-1", TelnetInterpreter.TelnetMode.Server);
			await server.WaitForProcessingAsync();

			await Assert.That(server.CurrentEncoding.WebName).IsEqualTo("iso-8859-1");

			var accepted = new List<byte>
			{
				(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.ACCEPTED
			};
			accepted.AddRange(Encoding.ASCII.GetBytes("iso-8859-1"));
			accepted.AddRange(new byte[] { (byte)Trigger.IAC, (byte)Trigger.SE });

			await Assert.That(negotiationOutput.Any(x => x.SequenceEqual(accepted))).IsTrue();

			await server.DisposeAsync();
		}

		/// <summary>
		/// RFC 2066 page 9 asks a peer to hold its data back: "While a CHARSET subnegotiation is in
		/// progress, data SHOULD be queued. Once the CHARSET subnegotiation has terminated, the data
		/// can be sent (in the correct character set)." That is a SHOULD on the sender, and a peer
		/// that ignores it is the case we have to survive: bytes of an ordinary line arrive, CHARSET
		/// completes before that line's newline does, and the line is left half-delivered.
		///
		/// Those bytes were written in the encoding in force when they were sent. The encoding handed
		/// to the consumer alongside them must therefore be the one the line started in, not whatever
		/// CHARSET has since moved to - the peer cannot retroactively have sent Latin-1.
		/// </summary>
		[Test]
		public async Task DataArrivingBeforeCharsetSwitchKeepsTheEncodingItWasSentIn()
		{
			var receivedData = new List<(byte[] data, Encoding encoding)>();

			ValueTask CaptureOutput(byte[] data, Encoding enc, TelnetInterpreter ti)
			{
				receivedData.Add((data, enc));
				return ValueTask.CompletedTask;
			}

			var server = await new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Server)
				.UseLogger(logger)
				.OnSubmit(CaptureOutput)
				.OnNegotiation(_ => ValueTask.CompletedTask)
				.AddPlugin<CharsetProtocol>()
				.BuildAsync();

			// The connection is on its default UTF-8, and the peer sends a line in it. No newline yet:
			// this line is still open when the negotiation below lands.
			const string sentText = "Café";
			var sentBytes = Encoding.UTF8.GetBytes(sentText);
			await Assert.That(sentBytes).IsEquivalentTo(new byte[] { 0x43, 0x61, 0x66, 0xC3, 0xA9 });

			await server.InterpretByteArrayAsync(sentBytes);
			await server.WaitForProcessingAsync();

			// Mid-line, CHARSET completes and moves the connection to Latin-1.
			await NegotiateCharset(server, "iso-8859-1", TelnetInterpreter.TelnetMode.Server);
			await server.WaitForProcessingAsync();

			// Positive control: the switch really happened, so this test cannot pass by the
			// negotiation having quietly failed.
			await Assert.That(server.CurrentEncoding.WebName).IsEqualTo("iso-8859-1");

			// Only now does the line end.
			await server.InterpretByteArrayAsync(new byte[] { (byte)'\n' });
			await server.WaitForProcessingAsync();

			await Assert.That(receivedData.Count).IsEqualTo(1);
			var received = receivedData[0];

			// The bytes themselves survive - the interpreter never transcodes.
			await AssertByteArraysEqual(received.data, sentBytes);

			// The label must still be the encoding the line was sent in. Reading these bytes as
			// Latin-1 yields "CafÃ©", which is the mojibake this guards against.
			await Assert.That(received.encoding.WebName).IsEqualTo("utf-8");
			await Assert.That(received.encoding.GetString(received.data)).IsEqualTo(sentText);

			await server.DisposeAsync();
		}

		/// <summary>
		/// The harder half of the previous test: a peer that not only ignores RFC 2066's request to
		/// queue, but changes its own output encoding part-way through a line. The line then holds
		/// bytes written in two encodings, and the single Encoding delivered beside it cannot describe
		/// both halves - whichever is chosen, the other half reads as mojibake.
		///
		/// This is not a defect to be fixed at this layer; it is the ambiguity the RFC asks senders to
		/// avoid by queueing, arriving anyway. What is pinned here is the choice made in its face: the
		/// label is the encoding the line began in, and every byte is delivered intact so that a
		/// consumer which knows better can decode the parts itself. Nothing is silently dropped or
		/// transcoded, so the loss is recoverable rather than baked in.
		/// </summary>
		[Test]
		public async Task ALineStraddlingACharsetSwitchIsLabelledByItsStartAndKeepsEveryByte()
		{
			var receivedData = new List<(byte[] data, Encoding encoding)>();

			ValueTask CaptureOutput(byte[] data, Encoding enc, TelnetInterpreter ti)
			{
				receivedData.Add((data, enc));
				return ValueTask.CompletedTask;
			}

			var server = await new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Server)
				.UseLogger(logger)
				.OnSubmit(CaptureOutput)
				.OnNegotiation(_ => ValueTask.CompletedTask)
				.AddPlugin<CharsetProtocol>()
				.BuildAsync();

			// First half of the line, in the UTF-8 the connection is currently on.
			var utf8Half = Encoding.UTF8.GetBytes("Café ");
			await Assert.That(utf8Half).IsEquivalentTo(new byte[] { 0x43, 0x61, 0x66, 0xC3, 0xA9, 0x20 });
			await server.InterpretByteArrayAsync(utf8Half);
			await server.WaitForProcessingAsync();

			// The switch lands in the middle of that same line.
			await NegotiateCharset(server, "iso-8859-1", TelnetInterpreter.TelnetMode.Server);
			await server.WaitForProcessingAsync();
			await Assert.That(server.CurrentEncoding.WebName).IsEqualTo("iso-8859-1");

			// Second half, which the peer really does write in Latin-1. 0xFC is "ü" there, and is not
			// a legal UTF-8 lead byte at all - so these bytes cannot be read under the line's label.
			var latin1Half = Encoding.Latin1.GetBytes("über");
			await Assert.That(latin1Half).IsEquivalentTo(new byte[] { 0xFC, 0x62, 0x65, 0x72 });
			await server.InterpretByteArrayAsync(latin1Half);
			await server.InterpretByteArrayAsync(new byte[] { (byte)'\n' });
			await server.WaitForProcessingAsync();

			await Assert.That(receivedData.Count).IsEqualTo(1);
			var received = receivedData[0];

			// Every byte of both halves arrives, in order and untouched. This is the property that
			// makes the ambiguity survivable: the consumer still has the original wire bytes.
			await AssertByteArraysEqual(received.data, utf8Half.Concat(latin1Half).ToArray());

			// The label is the encoding the line started in, not the one it ended in.
			await Assert.That(received.encoding.WebName).IsEqualTo("utf-8");

			// So the half that predates the switch reads correctly...
			await Assert.That(received.encoding.GetString(received.data.Take(utf8Half.Length).ToArray()))
				.IsEqualTo("Café ");

			// ...and the half after it does not. Decoding the whole line under its label costs exactly
			// the one byte that is not legal UTF-8: 0xFC becomes U+FFFD and "ber" survives beside it.
			await Assert.That(received.encoding.GetString(received.data)).IsEqualTo("Café �ber");

			// The consumer is not stuck with that, which is the point: the bytes are intact, so it can
			// decode the tail as Latin-1 itself and recover "über" in full.
			await Assert.That(Encoding.Latin1.GetString(received.data.Skip(utf8Half.Length).ToArray()))
				.IsEqualTo("über");

			await server.DisposeAsync();
		}

		// Helper method to test encoding with IAC escaping
		private async Task TestEncodingWithIACEscaping(Encoding encoding, string webName, string testString, TelnetInterpreter.TelnetMode mode)
		{
			var receivedData = new List<(byte[] data, Encoding encoding)>();
			byte[] negotiationOutput = null;
			
			ValueTask CaptureOutput(byte[] data, Encoding enc, TelnetInterpreter ti)
			{
				receivedData.Add((data, enc));
				logger.LogInformation("=== CaptureOutput called ===");
				logger.LogInformation("Received {Length} bytes with encoding {Encoding}",data.Length, enc.WebName);
				logger.LogInformation("Bytes: {Bytes}", string.Join(" ", data.Select(b => $"{b:X2}")));
				logger.LogInformation("Decoded: {Data}", enc.GetString(data));
				return ValueTask.CompletedTask;
			}

			ValueTask CaptureNegotiation(ReadOnlyMemory<byte> data)
			{
				negotiationOutput = data.ToArray();
				return ValueTask.CompletedTask;
			}

			var builder = new TelnetInterpreterBuilder()
				.UseMode(mode)
				.UseLogger(logger)
				.OnSubmit(CaptureOutput)
				.OnNegotiation(CaptureNegotiation)
				.AddPlugin<CharsetProtocol>();

			var ti = await builder.BuildAsync();
			
			// For client mode, configure charset order
			if (mode == TelnetInterpreter.TelnetMode.Client)
			{
				var charsetPlugin = ti.PluginManager!.GetPlugin<CharsetProtocol>();
				charsetPlugin!.CharsetOrder = new[] { encoding };
			}

			// Negotiate the charset
			await NegotiateCharset(ti, webName, mode);
			await ti.WaitForProcessingAsync();

			// Verify encoding was set
			await Assert.That(ti.CurrentEncoding.WebName).IsEqualTo(encoding.WebName);

			// Convert test string to bytes in the target encoding
			var originalBytes = encoding.GetBytes(testString);
			logger.LogInformation("=== Sending data ===");
			logger.LogInformation("Original string: {String}", testString);
			logger.LogInformation("Original bytes ({Length}): {Bytes}", originalBytes.Length, string.Join(" ", originalBytes.Select(b => $"{b:X2}")));
			
			// Escape IAC bytes (255) by doubling them
			var escapedBytes = EscapeIACBytes(originalBytes);
			logger.LogInformation("Escaped bytes ({Length}): {Bytes}", escapedBytes.Length, string.Join(" ", escapedBytes.Select(b => $"{b:X2}")));
			
			// Verify IAC escaping: count 255s in original and escaped
			var originalIACCount = originalBytes.Count(b => b == 255);
			var escapedIACCount = escapedBytes.Count(b => b == 255);
			logger.LogInformation("IAC count: original={Original}, escaped={Escaped}", originalIACCount, escapedIACCount);
			
			if (originalIACCount > 0)
			{
				// Each IAC (255) should be doubled
				await Assert.That(escapedIACCount).IsEqualTo(originalIACCount * 2);
			}

			// Send the escaped bytes with newline to trigger OnSubmit
			var withNewline = escapedBytes.Concat(new byte[] { (byte)'\n' }).ToArray();
			logger.LogInformation("Sending {Length} bytes (with newline): {Bytes}", withNewline.Length, string.Join(" ", withNewline.Select(b => $"{b:X2}")));
			await ti.InterpretByteArrayAsync(withNewline);
			await ti.WaitForProcessingAsync();

			// Verify the data was received correctly (IAC unescaped)
			logger.LogInformation("=== Verification ===");
			logger.LogInformation("Received {Count} callbacks", receivedData.Count);
			await Assert.That(receivedData.Count).IsGreaterThan(0);
			var received = receivedData.Last();
			var receivedString = received.encoding.GetString(received.data);
			logger.LogInformation("Expected string: {Expected}", testString);
			logger.LogInformation("Received string: {Received}", receivedString);
			logger.LogInformation("Match: {Match}", receivedString == testString);
			
			// The received string should match the original
			await Assert.That(receivedString).IsEqualTo(testString);

			await ti.DisposeAsync();
		}

		// Helper to negotiate charset
		private async Task NegotiateCharset(TelnetInterpreter ti, string webName, TelnetInterpreter.TelnetMode mode)
		{
			if (mode == TelnetInterpreter.TelnetMode.Server)
			{
				// Client sends WILL CHARSET
				await ti.InterpretByteArrayAsync(new byte[] { 
					(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.CHARSET 
				});
				await ti.WaitForProcessingAsync();

				// Client sends REQUEST with charset
				var charsetBytes = Encoding.ASCII.GetBytes(webName);
				var request = new List<byte> { 
					(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.REQUEST,
					(byte)';'
				};
				request.AddRange(charsetBytes);
				request.AddRange(new byte[] { (byte)Trigger.IAC, (byte)Trigger.SE });
				
				await ti.InterpretByteArrayAsync(request.ToArray());
				await ti.WaitForProcessingAsync();
			}
			else // Client
			{
				// Server sends WILL CHARSET
				await ti.InterpretByteArrayAsync(new byte[] { 
					(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.CHARSET 
				});
				await ti.WaitForProcessingAsync();

				// Server sends DO CHARSET
				await ti.InterpretByteArrayAsync(new byte[] { 
					(byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.CHARSET 
				});
				await ti.WaitForProcessingAsync();

				// Server sends ACCEPTED with charset
				var charsetBytes = Encoding.ASCII.GetBytes(webName);
				var accepted = new List<byte> { 
					(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.ACCEPTED
				};
				accepted.AddRange(charsetBytes);
				accepted.AddRange(new byte[] { (byte)Trigger.IAC, (byte)Trigger.SE });
				
				await ti.InterpretByteArrayAsync(accepted.ToArray());
				await ti.WaitForProcessingAsync();
			}
		}

		// TTABLE Support Tests
		[Test]
		public async Task TTableReceivedCallback_ShouldBeInvoked()
		{
			byte[] receivedTTableData = Array.Empty<byte>();
			var wasCallbackInvoked = false;
			byte[] negotiationOutput = null;

			ValueTask CaptureNegotiation(ReadOnlyMemory<byte> data)
			{
				negotiationOutput = data.ToArray();
				return ValueTask.CompletedTask;
			}

			var server_ti = await new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Server)
				.UseLogger(logger)
				.OnSubmit(WriteBackToOutput)
				.OnNegotiation(CaptureNegotiation)
				.AddPlugin<CharsetProtocol>()
				.BuildAsync();

			var charsetPlugin = server_ti.PluginManager!.GetPlugin<CharsetProtocol>();
			charsetPlugin!.EnableTTableSupport = true;
			charsetPlugin.OnTTableReceived((data) =>
			{
				receivedTTableData = data;
				wasCallbackInvoked = true;
				return ValueTask.FromResult(true); // ACK the table
			});

			// Client sends WILL CHARSET
			await server_ti.InterpretByteArrayAsync(new byte[] { 
				(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.CHARSET 
			});
			await server_ti.WaitForProcessingAsync();

			// Client sends TTABLE-IS message
			// Format: version(1) sep(;) charset1 sep size1(3bytes) count1(3bytes) charset2 sep size2 count2 map1 map2
			var ttableMessage = new List<byte> {
				(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.TTABLE_IS,
				1, // version
				(byte)';', // separator
				(byte)'u', (byte)'t', (byte)'f', (byte)'-', (byte)'8', (byte)';', // charset1
				8, // size1 (8 bits)
				0, 0, 10, // count1 (10 characters)
				(byte)'u', (byte)'s', (byte)'-', (byte)'a', (byte)'s', (byte)'c', (byte)'i', (byte)'i', (byte)';', // charset2
				8, // size2
				0, 0, 10, // count2
				// map1: 10 bytes of mapping data
				0, 1, 2, 3, 4, 5, 6, 7, 8, 9,
				// map2: 10 bytes of mapping data
				0, 1, 2, 3, 4, 5, 6, 7, 8, 9,
				(byte)Trigger.IAC, (byte)Trigger.SE
			};

			await server_ti.InterpretByteArrayAsync(ttableMessage.ToArray());
			await server_ti.WaitForProcessingAsync();

			// Verify callback was invoked
			await Assert.That(wasCallbackInvoked).IsTrue();
			await Assert.That(receivedTTableData).IsNotNull();
			
			// Verify TTABLE-ACK was sent
			await Assert.That(negotiationOutput).IsNotNull();
			var expected = new byte[] { 
				(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.TTABLE_ACK, 
				(byte)Trigger.IAC, (byte)Trigger.SE 
			};
			await AssertByteArraysEqual(negotiationOutput, expected);

			await server_ti.DisposeAsync();
		}

		[Test]
		public async Task TTableRejected_WhenNoCallbackRegistered()
		{
			byte[] negotiationOutput = null;

			ValueTask CaptureNegotiation(ReadOnlyMemory<byte> data)
			{
				negotiationOutput = data.ToArray();
				return ValueTask.CompletedTask;
			}

			var server_ti = await new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Server)
				.UseLogger(logger)
				.OnSubmit(WriteBackToOutput)
				.OnNegotiation(CaptureNegotiation)
				.AddPlugin<CharsetProtocol>()
				.BuildAsync();

			var charsetPlugin = server_ti.PluginManager!.GetPlugin<CharsetProtocol>();
			charsetPlugin!.EnableTTableSupport = true;
			// No callback registered

			// Client sends WILL CHARSET
			await server_ti.InterpretByteArrayAsync(new byte[] { 
				(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.CHARSET 
			});
			await server_ti.WaitForProcessingAsync();

			// Client sends TTABLE-IS message (minimal)
			var ttableMessage = new List<byte> {
				(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.TTABLE_IS,
				1, // version
				(byte)Trigger.IAC, (byte)Trigger.SE
			};

			await server_ti.InterpretByteArrayAsync(ttableMessage.ToArray());
			await server_ti.WaitForProcessingAsync();

			// Verify TTABLE-REJECTED was sent
			await Assert.That(negotiationOutput).IsNotNull();
			var expected = new byte[] { 
				(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.TTABLE_REJECTED, 
				(byte)Trigger.IAC, (byte)Trigger.SE 
			};
			await AssertByteArraysEqual(negotiationOutput, expected);

			await server_ti.DisposeAsync();
		}

		[Test]
		public async Task TTableNak_WhenCallbackReturnsFalse()
		{
			byte[] negotiationOutput = null;

			ValueTask CaptureNegotiation(ReadOnlyMemory<byte> data)
			{
				negotiationOutput = data.ToArray();
				return ValueTask.CompletedTask;
			}

			var server_ti = await new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Server)
				.UseLogger(logger)
				.OnSubmit(WriteBackToOutput)
				.OnNegotiation(CaptureNegotiation)
				.AddPlugin<CharsetProtocol>()
				.BuildAsync();

			var charsetPlugin = server_ti.PluginManager!.GetPlugin<CharsetProtocol>();
			charsetPlugin!.EnableTTableSupport = true;
			charsetPlugin.OnTTableReceived((data) =>
			{
				return ValueTask.FromResult(false); // NAK the table
			});

			// Client sends WILL CHARSET
			await server_ti.InterpretByteArrayAsync(new byte[] { 
				(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.CHARSET 
			});
			await server_ti.WaitForProcessingAsync();

			// Client sends TTABLE-IS message
			var ttableMessage = new List<byte> {
				(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.TTABLE_IS,
				1, // version
				(byte)';', // separator
				(byte)'u', (byte)'t', (byte)'f', (byte)'-', (byte)'8', (byte)';',
				8, 0, 0, 10,
				(byte)'a', (byte)'s', (byte)'c', (byte)'i', (byte)'i', (byte)';',
				8, 0, 0, 10,
				0, 1, 2, 3, 4, 5, 6, 7, 8, 9,
				0, 1, 2, 3, 4, 5, 6, 7, 8, 9,
				(byte)Trigger.IAC, (byte)Trigger.SE
			};

			await server_ti.InterpretByteArrayAsync(ttableMessage.ToArray());
			await server_ti.WaitForProcessingAsync();

			// Verify TTABLE-NAK was sent
			await Assert.That(negotiationOutput).IsNotNull();
			var expected = new byte[] { 
				(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.TTABLE_NAK, 
				(byte)Trigger.IAC, (byte)Trigger.SE 
			};
			await AssertByteArraysEqual(negotiationOutput, expected);

			await server_ti.DisposeAsync();
		}

		[Test]
		public async Task SendTTableAsync_ShouldSendCorrectMessage()
		{
			byte[] negotiationOutput = null;

			ValueTask CaptureNegotiation(ReadOnlyMemory<byte> data)
			{
				negotiationOutput = data.ToArray();
				return ValueTask.CompletedTask;
			}

			var server_ti = await new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Server)
				.UseLogger(logger)
				.OnSubmit(WriteBackToOutput)
				.OnNegotiation(CaptureNegotiation)
				.AddPlugin<CharsetProtocol>()
				.BuildAsync();

			var charsetPlugin = server_ti.PluginManager!.GetPlugin<CharsetProtocol>();
			charsetPlugin!.EnableTTableSupport = true;

			// Client sends WILL CHARSET
			await server_ti.InterpretByteArrayAsync(new byte[] { 
				(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.CHARSET 
			});
			await server_ti.WaitForProcessingAsync();

			// Server sends TTABLE-IS
			var ttableData = new byte[] { 1, (byte)';', (byte)'t', (byte)'e', (byte)'s', (byte)'t' };
			await charsetPlugin.SendTTableAsync(ttableData);

			// Verify TTABLE-IS was sent with correct format
			await Assert.That(negotiationOutput).IsNotNull();
			var expectedPrefix = new byte[] { 
				(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.TTABLE_IS
			};
			var expectedSuffix = new byte[] { (byte)Trigger.IAC, (byte)Trigger.SE };
			
			await AssertByteArraysEqual(negotiationOutput.Take(4).ToArray(), expectedPrefix);
			await AssertByteArraysEqual(negotiationOutput.Skip(negotiationOutput.Length - 2).ToArray(), expectedSuffix);

			await server_ti.DisposeAsync();
		}

		[Test]
		public async Task TTableUnsupportedVersion_ShouldBeRejected()
		{
			byte[] negotiationOutput = null;

			ValueTask CaptureNegotiation(ReadOnlyMemory<byte> data)
			{
				negotiationOutput = data.ToArray();
				return ValueTask.CompletedTask;
			}

			var server_ti = await new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Server)
				.UseLogger(logger)
				.OnSubmit(WriteBackToOutput)
				.OnNegotiation(CaptureNegotiation)
				.AddPlugin<CharsetProtocol>()
				.BuildAsync();

			var charsetPlugin = server_ti.PluginManager!.GetPlugin<CharsetProtocol>();
			charsetPlugin!.EnableTTableSupport = true;
			charsetPlugin.OnTTableReceived((data) => ValueTask.FromResult(true));

			// Client sends WILL CHARSET
			await server_ti.InterpretByteArrayAsync(new byte[] { 
				(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.CHARSET 
			});
			await server_ti.WaitForProcessingAsync();

			// Client sends TTABLE-IS with unsupported version
			var ttableMessage = new List<byte> {
				(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.TTABLE_IS,
				99, // unsupported version
				(byte)Trigger.IAC, (byte)Trigger.SE
			};

			await server_ti.InterpretByteArrayAsync(ttableMessage.ToArray());
			await server_ti.WaitForProcessingAsync();

			// Verify TTABLE-REJECTED was sent
			await Assert.That(negotiationOutput).IsNotNull();
			var expected = new byte[] {
				(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.TTABLE_REJECTED,
				(byte)Trigger.IAC, (byte)Trigger.SE
			};
			await AssertByteArraysEqual(negotiationOutput, expected);

			await server_ti.DisposeAsync();
		}

		/// <summary>
		/// TTABLE capture shared GMCP's 8KB ceiling: past it, bytes were dropped and the remainder
		/// was parsed as if it were the whole table. A truncated translation table is a wrong
		/// translation table, so the table is now rejected outright - and RFC 2066 has a message
		/// for exactly that.
		/// </summary>
		[Test]
		public async Task TTableBeyondTheConfiguredCeiling_IsRejectedNotTruncated()
		{
			var wasCallbackInvoked = false;
			byte[] negotiationOutput = null;

			ValueTask CaptureNegotiation(ReadOnlyMemory<byte> data)
			{
				negotiationOutput = data.ToArray();
				return ValueTask.CompletedTask;
			}

			var server_ti = await new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Server)
				.UseLogger(logger)
				.OnSubmit(WriteBackToOutput)
				.OnNegotiation(CaptureNegotiation)
				.AddPlugin<CharsetProtocol>()
					.WithTTableSupport()
					.WithMaxTTableSize(1024)
					.OnTTableReceived((data) =>
					{
						wasCallbackInvoked = true;
						return ValueTask.FromResult(true);
					})
				.BuildAsync();

			await server_ti.InterpretByteArrayAsync(new byte[] {
				(byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.CHARSET
			});
			await server_ti.WaitForProcessingAsync();

			// A well-formed version 1 header followed by more mapping data than the ceiling allows.
			var ttableMessage = new List<byte> {
				(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.TTABLE_IS,
				1, // version
				(byte)';', // separator
				(byte)'u', (byte)'t', (byte)'f', (byte)'-', (byte)'8', (byte)';', // charset1
				8, 0, 0, 10, // size1, count1
				(byte)'u', (byte)'s', (byte)'-', (byte)'a', (byte)'s', (byte)'c', (byte)'i', (byte)'i', (byte)';', // charset2
				8, 0, 0, 10 // size2, count2
			};
			ttableMessage.AddRange(Enumerable.Repeat((byte)0x41, 2048));
			ttableMessage.Add((byte)Trigger.IAC);
			ttableMessage.Add((byte)Trigger.SE);

			await server_ti.InterpretByteArrayAsync(ttableMessage.ToArray());
			await server_ti.WaitForProcessingAsync(maxWaitMs: 30000);

			// The consumer is never handed a partial table...
			await Assert.That(wasCallbackInvoked).IsFalse();

			// ...and the peer is told, in the protocol's own words.
			await Assert.That(negotiationOutput).IsNotNull();
			var expectedRejection = new byte[] {
				(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.TTABLE_REJECTED,
				(byte)Trigger.IAC, (byte)Trigger.SE
			};
			await AssertByteArraysEqual(negotiationOutput, expectedRejection);

			await server_ti.DisposeAsync();
		}

		/// <summary>
		/// A line of non-ASCII text that arrives <em>before</em> CHARSET has been agreed must still reach
		/// the application recoverably.
		/// <para>
		/// This is the ordinary case rather than a corner one. CHARSET (RFC 2066) takes several round
		/// trips — WILL/DO, the offered list, the agreement — and a MU* server greets the moment the
		/// socket opens, so its banner is on the wire while negotiation is still in flight. Plenty of
		/// servers never implement RFC 2066 at all and simply send UTF-8 regardless, in which case
		/// "before negotiation completes" means "for the whole session".
		/// </para>
		/// <para>
		/// The assertion is deliberately a property rather than a named encoding, so it does not
		/// over-specify the fix: whatever <see cref="TelnetInterpreter.CurrentEncoding"/> starts as, it
		/// must not <em>destroy</em> the bytes handed alongside it. UTF-8 passes because it is correct;
		/// ISO-8859-1 passes because it is byte-preserving, so a caller that later learns the real
		/// charset can still re-decode. <c>Encoding.ASCII</c> — the previous default — fails, because it maps
		/// every byte above 127 to '?', and no later knowledge can undo that.
		/// </para>
		/// </summary>
		[Test]
		public async Task NonAsciiBeforeCharsetIsNegotiated_IsHandedOverRecoverably()
		{
			var received = new List<(byte[] Data, Encoding Encoding)>();

			ValueTask Capture(byte[] data, Encoding encoding, TelnetInterpreter t)
			{
				received.Add((data, encoding));
				return ValueTask.CompletedTask;
			}

			var client = await new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Client)
				.UseLogger(logger)
				.OnSubmit(Capture)
				.OnNegotiation(_ => ValueTask.CompletedTask)
				.AddPlugin<CharsetProtocol>()
				.BuildAsync();

			// No NegotiateCharset call: this is the banner landing first.
			var text = "Caf\u00e9 \u65e5\u672c";
			var line = Encoding.UTF8.GetBytes(text + "\n");
			await client.InterpretByteArrayAsync(line);
			await client.WaitForProcessingAsync();

			await Assert.That(received.Count).IsEqualTo(1);
			var (data, encoding) = received[0];

			// The raw bytes reach the application unchanged — that part already works.
			await AssertByteArraysEqual(data, Encoding.UTF8.GetBytes(text));

			// ...but the encoding handed over with them has to be able to carry them. Round-tripping is
			// the test: an encoding that cannot reproduce what it was just given has destroyed it, and
			// no later knowledge of the real charset can undo that.
			var decoded = encoding.GetString(data);
			var roundTripped = encoding.GetBytes(decoded);
			await Assert.That(Convert.ToHexString(roundTripped))
				.IsEqualTo(Convert.ToHexString(data))
				.Because(
					$"CurrentEncoding was '{encoding.WebName}' for bytes that arrived before CHARSET was "
					+ $"agreed, which decodes them to \"{decoded}\" — the original is unrecoverable. A "
					+ "byte-preserving default (iso-8859-1) or an optimistic one (utf-8) would both keep "
					+ "them.");

			await client.DisposeAsync();
		}
	}
}
