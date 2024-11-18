using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using System.Threading.Tasks;

namespace TelnetNegotiationCore.TestServer;

public class Startup
{
	// This method gets called by the runtime. Use this method to add services to the container.
	// For more information on how to configure your application, visit https://go.microsoft.com/fwlink/?LinkID=398940
	public static void ConfigureServices(IServiceCollection services)
	{
		services.AddLogging(logging =>
		{
			logging.ClearProviders();
			logging.AddSerilog();
			logging.SetMinimumLevel(LogLevel.Debug);
		});
		services.BuildServiceProvider();
	}

	// This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
	public static void Configure(IApplicationBuilder app, IHostingEnvironment _) => app.Run(async _ => await Task.CompletedTask);
}