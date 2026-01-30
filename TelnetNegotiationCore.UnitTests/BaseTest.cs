using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using System;
using System.Linq;
using System.Threading.Tasks;

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
		/// Manual byte array comparison helper to work around potential TUnit IsEquivalentTo issues
		/// </summary>
		protected static async Task AssertByteArraysEqual(byte[] actual, byte[] expected, string message = null)
		{
			var msg = message ?? "Byte arrays should be equal";
			
			if (actual == null) throw new Exception($"{msg}: actual array is null");
			if (expected == null) throw new Exception($"{msg}: expected array is null");
			
			if (actual.Length != expected.Length)
			{
				throw new Exception($"{msg}: Length mismatch. Expected {expected.Length} but got {actual.Length}");
			}
			
			for (int i = 0; i < expected.Length; i++)
			{
				if (actual[i] != expected[i])
				{
					throw new Exception($"{msg}: Byte at index {i} differs. Expected {expected[i]} but got {actual[i]}");
				}
			}
			
			// If we get here, arrays are equal
			await Task.CompletedTask;
		}
	}
}
