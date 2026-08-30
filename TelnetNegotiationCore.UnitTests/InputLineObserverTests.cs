#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// The assembled-line hook that protocols carried as text register on: it sees each line before the
/// host application does, and decides what -- if anything -- the application is handed.
/// </summary>
/// <remarks>
/// Consuming a line is not enough for every text protocol. MCP's quoting rule says a line the peer
/// sent as <c>#$"foo</c> must reach the application as <c>foo</c>: neither delivered as-is nor
/// dropped, but rewritten. So an observer answers with the line to carry on with, or with
/// <see langword="null"/> to consume it.
/// </remarks>
public class InputLineObserverTests : BaseTest
{
	private static async Task<(TelnetInterpreter Interpreter, List<string> Submitted)> BuildAsync(
		params Func<byte[], Encoding, ValueTask<byte[]?>>[] observers)
	{
		var submitted = new List<string>();

		var interpreter = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit((data, encoding, _) =>
			{
				lock (submitted) submitted.Add(encoding.GetString(data));
				return ValueTask.CompletedTask;
			})
			.OnNegotiation(_ => ValueTask.CompletedTask)
			.BuildAsync();

		foreach (var observer in observers)
		{
			interpreter.RegisterInputLineObserver(observer);
		}

		return (interpreter, submitted);
	}

	/// <summary>
	/// An observer that answers with different bytes replaces the line: the application sees what the
	/// observer returned, not what arrived.
	/// </summary>
	[Test]
	public async Task AnObserverCanRewriteTheLineTheApplicationSees()
	{
		var (interpreter, submitted) = await BuildAsync(
			(line, encoding) => ValueTask.FromResult<byte[]?>(encoding.GetBytes("rewritten:" + encoding.GetString(line))));

		await InterpretAndWaitAsync(interpreter, Encoding.UTF8.GetBytes("hello\r\n"));

		await Assert.That(submitted).IsEquivalentTo(new[] { "rewritten:hello" });
	}

	/// <summary>
	/// An observer answering null takes the line out of the stream: the host application never sees
	/// it, and neither does any observer registered after it.
	/// </summary>
	[Test]
	public async Task AnObserverThatAnswersNullConsumesTheLine()
	{
		var seenBySecond = new List<string>();

		var (interpreter, submitted) = await BuildAsync(
			(line, encoding) => ValueTask.FromResult<byte[]?>(encoding.GetString(line) == "mine" ? null : line),
			(line, encoding) =>
			{
				lock (seenBySecond) seenBySecond.Add(encoding.GetString(line));
				return ValueTask.FromResult<byte[]?>(line);
			});

		await InterpretAndWaitAsync(interpreter, Encoding.UTF8.GetBytes("mine\r\nyours\r\n"));

		await Assert.That(submitted).IsEquivalentTo(new[] { "yours" });
		await Assert.That(seenBySecond).IsEquivalentTo(new[] { "yours" });
	}

	/// <summary>
	/// Observers run in registration order and each sees what the one before it returned, so a
	/// rewrite is visible to everything downstream rather than only to the application.
	/// </summary>
	[Test]
	public async Task AnObserverSeesWhatTheOneBeforeItReturned()
	{
		var (interpreter, submitted) = await BuildAsync(
			(line, encoding) => ValueTask.FromResult<byte[]?>(encoding.GetBytes(encoding.GetString(line) + "-first")),
			(line, encoding) => ValueTask.FromResult<byte[]?>(encoding.GetBytes(encoding.GetString(line) + "-second")));

		await InterpretAndWaitAsync(interpreter, Encoding.UTF8.GetBytes("line\r\n"));

		await Assert.That(submitted).IsEquivalentTo(new[] { "line-first-second" });
	}
}
