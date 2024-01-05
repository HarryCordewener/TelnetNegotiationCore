using Serilog;

namespace TelnetNegotiationCore.UnitTests
{
	public class BaseTest
	{
		static BaseTest()
		{
			var log = new LoggerConfiguration()
				.Enrich.FromLogContext()
				.WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] ({TelnetMode}) {Message:lj}{NewLine}{Exception}")
				.MinimumLevel.Verbose()
				.CreateLogger();

			Log.Logger = log;
		}
	}
}
