using System.Collections.Generic;
using System.Threading.Tasks;
using TelnetNegotiationCore.Gmcp;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// <see cref="GmcpBatch"/>: changes between ticks go out once, and unchanged data not at all.
/// </summary>
public class GmcpBatchTests
{
	[Test]
	public async Task AFlushSendsTheLatestDataOfEachPackageOnce()
	{
		var (batch, sent) = Batch();

		for (var hp = 90; hp >= 50; hp -= 10)
		{
			batch.Set(new CharVitals { Hp = hp, MaxHp = 100 });
		}

		batch.Set("Room.Info", """{"num":1,"name":"Square"}""");

		await Assert.That(sent.Count).IsEqualTo(0);

		await batch.FlushAsync();

		await Assert.That(string.Join("|", sent)).IsEqualTo(string.Join("|", new[]
		{
			"Char.Vitals " + """{"hp":50,"maxhp":100}""",
			"Room.Info " + """{"num":1,"name":"Square"}"""
		}));
	}

	[Test]
	public async Task UnchangedDataIsNotSentAgain()
	{
		var (batch, sent) = Batch();

		batch.Set("Char.Vitals", """{"hp":50}""");
		await batch.FlushAsync();
		await batch.FlushAsync();

		batch.Set("Char.Vitals", """{"hp":50}""");
		await batch.FlushAsync();

		batch.Set("char.vitals", """{"hp":40}""");
		await batch.FlushAsync();

		await Assert.That(string.Join("|", sent)).IsEqualTo(string.Join("|", new[]
		{
			"Char.Vitals " + """{"hp":50}""",
			"char.vitals " + """{"hp":40}"""
		}));
	}

	[Test]
	public async Task ForgetSendsEverythingAgain()
	{
		var (batch, sent) = Batch();

		batch.Set("Char.Vitals", """{"hp":50}""");
		await batch.FlushAsync();

		batch.Forget();
		batch.Set("Char.Vitals", """{"hp":50}""");
		await batch.FlushAsync();

		await Assert.That(sent.Count).IsEqualTo(2);
	}

	private static (GmcpBatch Batch, List<string> Sent) Batch()
	{
		var sent = new List<string>();
		var batch = new GmcpBatch((package, data) =>
		{
			sent.Add(package + " " + data);
			return default;
		});
		return (batch, sent);
	}
}
