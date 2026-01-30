using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using System;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;

namespace TelnetNegotiationCore.UnitTests
{
	public class BaseTest
	{
		internal static readonly Microsoft.Extensions.Logging.ILogger logger;

		static BaseTest()
		{
			var log = new LoggerConfiguration()
				.Enrich.FromLogContext()
				.WriteTo.Console()
				.MinimumLevel.Debug()
				.CreateLogger();

			Log.Logger = log;

			var services = new ServiceCollection().AddLogging(x => x.AddSerilog());
			IServiceProvider serviceProvider = services.BuildServiceProvider();
			var factory = serviceProvider.GetRequiredService<ILoggerFactory>();
			logger = factory.CreateLogger(nameof(BaseTest));
		}

		/// <summary>
		/// Creates a no-op submit callback for tests that don't need to capture submitted data.
		/// </summary>
		protected static ValueTask NoOpSubmitCallback(byte[] data, Encoding encoding, TelnetInterpreter ti) => 
			ValueTask.CompletedTask;

		/// <summary>
		/// Builds the interpreter and waits for initialization to complete.
		/// </summary>
		protected static async Task<TelnetInterpreter> BuildAndWaitAsync(TelnetInterpreterBuilder builder)
		{
			var interpreter = await builder.BuildAsync();
			await Task.Delay(100);
			return interpreter;
		}

		/// <summary>
		/// Interprets a byte array and waits for processing to complete.
		/// </summary>
		protected static async Task InterpretAndWaitAsync(TelnetInterpreter interpreter, byte[] data)
		{
			await interpreter.InterpretByteArrayAsync(data);
			await interpreter.WaitForProcessingAsync();
		}

		/// <summary>
		/// Manually compares two byte arrays for equality.
		/// This is preferred over TUnit's IsEquivalentTo() for array comparisons because:
		/// - Provides clearer error messages showing the exact byte and index that differs
		/// - More reliable for byte-by-byte validation in protocol testing
		/// - Consistent behavior across different TUnit versions
		/// </summary>
		/// <param name="actual">The actual byte array to test (from test execution)</param>
		/// <param name="expected">The expected byte array (the correct value)</param>
		protected static async Task AssertByteArraysEqual(byte[] actual, byte[] expected)
		{
			await Assert.That(actual).IsNotNull();
			await Assert.That(actual.Length).IsEqualTo(expected.Length);
			
			for (int i = 0; i < expected.Length; i++)
			{
				await Assert.That(actual[i]).IsEqualTo(expected[i]);
			}
		}
	}
}
