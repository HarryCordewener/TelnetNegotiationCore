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

	/// <summary>
	/// A send that throws leaves its package pending, so the next flush sends it, unless a newer
	/// value replaced it.
	/// </summary>
	[Test]
	public async Task AFailedSendIsTriedAgainOnTheNextFlush()
	{
		var sent = new List<string>();
		var fail = true;
		var batch = new GmcpBatch((package, data) =>
		{
			if (fail && package == "Room.Info")
			{
				fail = false;
				throw new System.IO.IOException("connection reset");
			}

			sent.Add(package + " " + data);
			return default;
		});

		batch.Set("Char.Vitals", """{"hp":50}""");
		batch.Set("Room.Info", """{"num":1}""");
		batch.Set("Comm.Channel.Text", """{"channel":"say"}""");

		await Assert.That(async () => await batch.FlushAsync()).Throws<System.IO.IOException>();
		await Assert.That(string.Join("|", sent)).IsEqualTo("Char.Vitals " + """{"hp":50}""");

		batch.Set("Comm.Channel.Text", """{"channel":"tell"}""");
		await batch.FlushAsync();

		await Assert.That(string.Join("|", sent)).IsEqualTo(string.Join("|", new[]
		{
			"Char.Vitals " + """{"hp":50}""",
			"Room.Info " + """{"num":1}""",
			"Comm.Channel.Text " + """{"channel":"tell"}"""
		}));
	}

	/// <summary>
	/// A flush that starts while another is sending waits for it, so packages go out in flush order.
	/// </summary>
	[Test]
	public async Task FlushesDoNotOverlap()
	{
		var sent = new List<string>();
		var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		var first = true;
		var batch = new GmcpBatch(async (package, data) =>
		{
			if (first)
			{
				first = false;
				await release.Task;
			}

			lock (sent)
			{
				sent.Add(data);
			}
		});

		batch.Set("Char.Vitals", """{"hp":50}""");
		var older = batch.FlushAsync().AsTask();

		batch.Set("Char.Vitals", """{"hp":40}""");
		var newer = batch.FlushAsync().AsTask();

		await Task.Delay(50);
		await Assert.That(sent.Count).IsEqualTo(0);

		release.SetResult(true);
		await Task.WhenAll(older, newer);

		await Assert.That(string.Join("|", sent)).IsEqualTo("""{"hp":50}|{"hp":40}""");
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
