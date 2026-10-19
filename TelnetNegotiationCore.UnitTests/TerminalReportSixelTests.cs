using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Protocols;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

public class TerminalReportSixelTests : BaseTest
{
	[Test]
	public async Task MissingAndEmptyAttributesLeaveSixelUnknown()
	{
		await Assert.That(new TerminalReport().Sixel).IsNull();
		await Assert.That(new TerminalReport { DeviceAttributes = Array.Empty<int>() }.Sixel).IsNull();
	}

	[Test]
	[Arguments("1;2", false)]
	[Arguments("1;4", false)]
	[Arguments("4;6", false)]
	[Arguments("6", false)]
	[Arguments("7", false)]
	[Arguments("62;4;22", true)]
	[Arguments("63;4;22", true)]
	[Arguments("64;4;22", true)]
	[Arguments("65;4;22", true)]
	[Arguments("62;22", false)]
	[Arguments("63;22", false)]
	[Arguments("64;22", false)]
	[Arguments("65;22", false)]
	[Arguments("62", false)]
	[Arguments("12;4", null)]
	[Arguments("99;4", null)]
	[Arguments("0;4", null)]
	public async Task DeviceFamilyControlsSixelInterpretation(string attributes, bool? expected)
	{
		var reports = 0;
		var submitted = 0;
		var queries = new TerminalQueryProtocol().OnTerminalReport(_ =>
		{
			reports++;
			return default;
		});
		await using var server = await BuildAndWaitAsync(new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server).UseLogger(logger)
			.OnSubmit((_, _, _) => { submitted++; return default; })
			.OnNegotiation(_ => default).AddPlugin(queries));

		await InterpretAndWaitAsync(server, Encoding.ASCII.GetBytes("\u001b[?" + attributes + "c\r\n"));

		await Assert.That(queries.Report.Sixel).IsEqualTo(expected);
		await Assert.That(queries.Report.DeviceAttributes).IsEquivalentTo(attributes.Split(';').Select(int.Parse).ToArray());
		await Assert.That(reports).IsEqualTo(1);
		await Assert.That(submitted).IsEqualTo(0);
	}
}

