using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using System;

namespace TelnetNegotiationCore.UnitTests
{
	public class BaseTest
	{
		static internal Microsoft.Extensions.Logging.ILogger logger;

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
	}
}
