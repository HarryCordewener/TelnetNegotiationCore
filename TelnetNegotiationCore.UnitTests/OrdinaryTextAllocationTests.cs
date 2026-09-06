using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// What an ordinary byte of text costs to interpret.
/// </summary>
/// <remarks>
/// Every input byte used to fire the Stateless state machine, at ~3.3 KB a fire: 3,635 bytes
/// allocated to read one byte. Nothing leaked -- the live set was flat -- so it stayed hidden until
/// someone went looking for why a memory graph would not settle.
/// </remarks>
public class OrdinaryTextAllocationTests : BaseTest
{
	/// <summary>
	/// Loose on purpose: this catches the return of a per-byte transition, which is an order of
	/// magnitude. Currently ~173 bytes per byte on net10.0 and ~190 on net8.0, against 3,635 before.
	/// </summary>
	private const int CeilingBytesPerByte = 700;

	private const int Bytes = 200_000;

	/// <summary>
	/// Alone: GC.GetTotalAllocatedBytes counts the whole process, and the per-thread counter is no
	/// use here because the bytes are interpreted on the channel-draining task rather than the test
	/// thread. Beside the rest of the suite this read 1,383 and failed.
	/// </summary>
	[Test]
	[NotInParallel]
	public async Task ATextByteDoesNotCostAStateMachineTransition()
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

		// 78 columns and CR LF: the terminators are named triggers and still fire the machine, so this
		// is a realistic mix rather than a best case.
		var line = Encoding.ASCII.GetBytes(new string('x', 78) + "\r\n");

		// Warm: JIT and first-fire costs are not what a steady stream pays.
		for (var i = 0; i < 4000; i++)
		{
			await interpreter.InterpretAsync(line[i % line.Length]);
		}

		await interpreter.WaitForProcessingAsync(maxWaitMs: 5000, additionalDelayMs: 50);

		var before = GC.GetTotalAllocatedBytes(precise: true);

		for (var i = 0; i < Bytes; i++)
		{
			await interpreter.InterpretAsync(line[i % line.Length]);
		}

		await interpreter.WaitForProcessingAsync(maxWaitMs: 30_000, additionalDelayMs: 100);

		var perByte = (GC.GetTotalAllocatedBytes(precise: true) - before) / (double)Bytes;

		await Assert.That(perByte).IsLessThan(CeilingBytesPerByte);

		// Still read the lines, so this cannot pass by doing less work.
		await Assert.That(submitted.Count).IsGreaterThan(2000);

		await interpreter.DisposeAsync();
	}

	/// <summary>
	/// The bytes the shortcut does not take still go through the machine.
	/// </summary>
	/// <remarks>
	/// The shortcut is only correct because it handles one configured re-entry and nothing else, so
	/// the boundary is what needs showing: negotiation still negotiates, an escaped IAC is still one
	/// byte of data, and a line still ends at its terminator.
	/// </remarks>
	[Test]
	public async Task WhatTheShortcutDoesNotHandleStillGoesThroughTheMachine()
	{
		// Bytes, not a decoded string: 0xFF is not representable in the default encoding, so decoding
		// would turn the byte under test into a replacement character before the assertion saw it.
		var submitted = new List<byte[]>();
		var negotiated = new List<byte[]>();

		var interpreter = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit((data, _, _) =>
			{
				lock (submitted) submitted.Add((byte[])data.Clone());
				return ValueTask.CompletedTask;
			})
			.OnNegotiation(data =>
			{
				lock (negotiated) negotiated.Add(data.ToArray());
				return ValueTask.CompletedTask;
			})
			.BuildAsync();

		// Text, a negotiation, more text, an escaped IAC, a terminator: covers entering and leaving the
		// state the shortcut runs in, not just sitting in it.
		var stream = new List<byte>();
		stream.AddRange(Encoding.ASCII.GetBytes("before"));
		stream.AddRange([255, 251, 31]);                       // IAC WILL NAWS
		stream.AddRange(Encoding.ASCII.GetBytes("after"));
		stream.AddRange([255, 255]);                           // an escaped IAC: one 0xFF of data
		stream.AddRange(Encoding.ASCII.GetBytes("end\r\n"));

		await InterpretAndWaitAsync(interpreter, [.. stream]);
		await PollUntilAsync(() => submitted.Count > 0);

		await Assert.That(submitted.Count).IsEqualTo(1);

		// Negotiation gone from the line, escaped IAC survives as one 0xFF, nothing else moved.
		var expected = new List<byte>();
		expected.AddRange(Encoding.ASCII.GetBytes("beforeafter"));
		expected.Add(255);
		expected.AddRange(Encoding.ASCII.GetBytes("end"));

		await AssertByteArraysEqual(submitted[0], expected.ToArray());

		await interpreter.DisposeAsync();
	}

	/// <summary>
	/// What a transition subscriber sees, which is not every byte.
	/// </summary>
	/// <remarks>
	/// TelnetStateMachine is public and ProtocolContext hands the same machine to plugins, so
	/// subscribing to OnTransitioned is something a caller can do -- and the shortcut means ordinary
	/// text does not reach it. Stateless publishes no way to ask whether a handler is registered, so
	/// this cannot be detected and turned off; it is documented on the property instead, and pinned
	/// here so the documented behaviour and the real one cannot drift apart silently.
	/// </remarks>
	[Test]
	public async Task ATransitionSubscriberSeesTheLineBoundariesAndNotEveryTextByte()
	{
		var submitted = new List<byte[]>();
		var transitions = 0;

		var interpreter = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit((data, _, _) =>
			{
				lock (submitted) submitted.Add((byte[])data.Clone());
				return ValueTask.CompletedTask;
			})
			.OnNegotiation(_ => ValueTask.CompletedTask)
			.BuildAsync();

		interpreter.TelnetStateMachine.OnTransitioned(_ => Interlocked.Increment(ref transitions));

		// 78 characters and a terminator.
		await InterpretAndWaitAsync(interpreter, Encoding.ASCII.GetBytes(new string('x', 78) + "\r\n"));
		await PollUntilAsync(() => submitted.Count > 0);

		// The line arrives whole, so nothing was dropped.
		await Assert.That(submitted.Count).IsEqualTo(1);
		await Assert.That(submitted[0].Length).IsEqualTo(78);

		// And the subscriber saw the boundaries rather than one transition per character: entering
		// ReadingCharacters, and the terminator's move to Act. Far fewer than the 80 bytes fed.
		await Assert.That(transitions).IsLessThan(10);

		await interpreter.DisposeAsync();
	}
}
