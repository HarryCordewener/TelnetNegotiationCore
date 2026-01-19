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
		private byte[] _negotiationOutput;

		private ValueTask WriteBackToOutput(byte[] arg1, Encoding arg2, TelnetInterpreter t) => throw new NotImplementedException();

		private ValueTask WriteBackToGMCP((string module, string writeback) arg1) => throw new NotImplementedException();

		private ValueTask ClientWriteBackToNegotiate(byte[] arg1) { _negotiationOutput = arg1; return ValueTask.CompletedTask; }

		private ValueTask ServerWriteBackToNegotiate(byte[] arg1) { _negotiationOutput = arg1; return ValueTask.CompletedTask; }

	[Before(Test)]
		public void Setup()
		{
			_negotiationOutput = null;

		}

		[Test]
		[MethodDataSource(nameof(ServerCHARSETSequences))]
		public async Task ServerEvaluationCheck(IEnumerable<byte[]> clientSends, IEnumerable<byte[]> serverShouldRespondWith, IEnumerable<Encoding> currentEncoding)
		{
			var server_ti = await new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Server)
				.UseLogger(logger)
				.OnSubmit(WriteBackToOutput)
				.OnNegotiation(ServerWriteBackToNegotiate)
				.AddPlugin<CharsetProtocol>()
				.BuildAsync();

			if (clientSends.Count() != serverShouldRespondWith.Count())
				throw new Exception("Invalid Testcase.");

			foreach ((var clientSend, var serverShouldRespond, var shouldHaveCurrentEncoding) in clientSends.Zip(serverShouldRespondWith, currentEncoding))
			{
				_negotiationOutput = null;
				foreach (var x in clientSend ?? Enumerable.Empty<byte>())
				{
					await server_ti.InterpretAsync(x);
				}
				await server_ti.WaitForProcessingAsync();

				await Assert.That(server_ti.CurrentEncoding).IsEqualTo(shouldHaveCurrentEncoding);
				await Assert.That(_negotiationOutput).IsEquivalentTo(serverShouldRespond);
			}
		}

		[Test]
		[MethodDataSource(nameof(ClientCHARSETSequences))]
		public async Task ClientEvaluationCheck(IEnumerable<byte[]> serverSends, IEnumerable<byte[]> serverShouldRespondWith, IEnumerable<Encoding> currentEncoding)
		{
			var client_ti = await new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Client)
				.UseLogger(logger)
				.OnSubmit(WriteBackToOutput)
				.OnNegotiation(ClientWriteBackToNegotiate)
				.AddPlugin<CharsetProtocol>()
				.BuildAsync();

			var charsetPlugin = client_ti.PluginManager!.GetPlugin<CharsetProtocol>();
			charsetPlugin!.CharsetOrder = new[] { Encoding.GetEncoding("utf-8"), Encoding.GetEncoding("iso-8859-1") };

			if (serverSends.Count() != serverShouldRespondWith.Count())
				throw new Exception("Invalid Testcase.");

			foreach ((var serverSend, var clientShouldRespond, var shouldHaveCurrentEncoding) in serverSends.Zip(serverShouldRespondWith, currentEncoding))
			{
				_negotiationOutput = null;
				foreach (var x in serverSend ?? Enumerable.Empty<byte>())
				{
					await client_ti.InterpretAsync(x);
				}
				await client_ti.WaitForProcessingAsync();
				await Assert.That(client_ti.CurrentEncoding).IsEqualTo(shouldHaveCurrentEncoding);
				await Assert.That(_negotiationOutput).IsEquivalentTo(clientShouldRespond);
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
					Encoding.ASCII,
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
					Encoding.ASCII,
					Encoding.ASCII,
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
					Encoding.ASCII
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
					Encoding.ASCII,
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
					Encoding.ASCII,
					Encoding.UTF8
				});
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
			// ASCII can't represent byte 255 as a character, but should handle it in binary data
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
		public async Task ServerCanSwitchBetweenEncodings()
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
				.OnNegotiation(ServerWriteBackToNegotiate)
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
				var escapedBytes = server.TelnetSafeBytes(testBytes);
				
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
			
			ValueTask CaptureOutput(byte[] data, Encoding enc, TelnetInterpreter ti)
			{
				receivedData.Add((data, enc));
				return ValueTask.CompletedTask;
			}

			var client = await new TelnetInterpreterBuilder()
				.UseMode(TelnetInterpreter.TelnetMode.Client)
				.UseLogger(logger)
				.OnSubmit(CaptureOutput)
				.OnNegotiation(ClientWriteBackToNegotiate)
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
				var escapedBytes = client.TelnetSafeBytes(testBytes);
				
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

		// Helper method to test encoding with IAC escaping
		private async Task TestEncodingWithIACEscaping(Encoding encoding, string webName, string testString, TelnetInterpreter.TelnetMode mode)
		{
			var receivedData = new List<(byte[] data, Encoding encoding)>();
			
			ValueTask CaptureOutput(byte[] data, Encoding enc, TelnetInterpreter ti)
			{
				receivedData.Add((data, enc));
				logger.LogInformation("Received {Length} bytes with encoding {Encoding}: {Data}", 
					data.Length, enc.WebName, enc.GetString(data));
				return ValueTask.CompletedTask;
			}

			ValueTask CaptureNegotiation(byte[] data)
			{
				_negotiationOutput = data;
				return ValueTask.CompletedTask;
			}

			var builder = new TelnetInterpreterBuilder()
				.UseMode(mode)
				.UseLogger(logger)
				.OnSubmit(CaptureOutput)
				.OnNegotiation(CaptureNegotiation)
				.AddPlugin<CharsetProtocol>();

			if (mode == TelnetInterpreter.TelnetMode.Client)
			{
				var ti = await builder.BuildAsync();
				var charsetPlugin = ti.PluginManager!.GetPlugin<CharsetProtocol>();
				charsetPlugin!.CharsetOrder = new[] { encoding };

				// Negotiate the charset
				await NegotiateCharset(ti, webName, mode);
				await ti.WaitForProcessingAsync();

				// Verify encoding was set
				await Assert.That(ti.CurrentEncoding.WebName).IsEqualTo(encoding.WebName);

				// Convert test string to bytes in the target encoding
				var originalBytes = encoding.GetBytes(testString);
				
				// Escape IAC bytes (255) by doubling them
				var escapedBytes = ti.TelnetSafeBytes(originalBytes);
				
				// Verify IAC escaping: count 255s in original and escaped
				var originalIACCount = originalBytes.Count(b => b == 255);
				var escapedIACCount = escapedBytes.Count(b => b == 255);
				
				if (originalIACCount > 0)
				{
					// Each IAC (255) should be doubled
					await Assert.That(escapedIACCount).IsEqualTo(originalIACCount * 2);
				}

				// Send the escaped bytes with newline to trigger OnSubmit
				var withNewline = escapedBytes.Concat(new byte[] { (byte)'\n' }).ToArray();
				await ti.InterpretByteArrayAsync(withNewline);
				await ti.WaitForProcessingAsync();

				// Verify the data was received correctly (IAC unescaped)
				await Assert.That(receivedData.Count).IsGreaterThan(0);
				var received = receivedData.Last();
				var receivedString = received.encoding.GetString(received.data);
				
				// The received string should match the original
				await Assert.That(receivedString).IsEqualTo(testString);

				await ti.DisposeAsync();
			}
			else
			{
				var ti = await builder.BuildAsync();

				// Negotiate the charset
				await NegotiateCharset(ti, webName, mode);
				await ti.WaitForProcessingAsync();

				// Verify encoding was set
				await Assert.That(ti.CurrentEncoding.WebName).IsEqualTo(encoding.WebName);

				// Convert test string to bytes in the target encoding
				var originalBytes = encoding.GetBytes(testString);
				
				// Escape IAC bytes (255) by doubling them
				var escapedBytes = ti.TelnetSafeBytes(originalBytes);
				
				// Verify IAC escaping
				var originalIACCount = originalBytes.Count(b => b == 255);
				var escapedIACCount = escapedBytes.Count(b => b == 255);
				
				if (originalIACCount > 0)
				{
					await Assert.That(escapedIACCount).IsEqualTo(originalIACCount * 2);
				}

				// Send the escaped bytes with newline to trigger OnSubmit
				var withNewline = escapedBytes.Concat(new byte[] { (byte)'\n' }).ToArray();
				await ti.InterpretByteArrayAsync(withNewline);
				await ti.WaitForProcessingAsync();

				// Verify the data was received correctly
				await Assert.That(receivedData.Count).IsGreaterThan(0);
				var received = receivedData.Last();
				var receivedString = received.encoding.GetString(received.data);
				await Assert.That(receivedString).IsEqualTo(testString);

				await ti.DisposeAsync();
			}
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
	}
}