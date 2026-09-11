using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// Captures formatted log output at every level, Trace included, so a test can assert on what was
/// reported, and passes each entry on to <paramref name="inner"/>.
/// </summary>
internal sealed class CapturingLogger(ILogger inner) : ILogger
{
	private readonly List<(LogLevel Level, string Message)> _entries = [];

	public IDisposable BeginScope<TState>(TState state) where TState : notnull => null!;

	public bool IsEnabled(LogLevel logLevel) => true;

	public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
		Func<TState, Exception, string> formatter)
	{
		var message = formatter(state, exception);
		lock (_entries)
		{
			_entries.Add((logLevel, message));
		}

		inner.Log(logLevel, eventId, state, exception, formatter);
	}

	public List<string> Entries(LogLevel level)
	{
		lock (_entries)
		{
			return _entries.Where(x => x.Level == level).Select(x => x.Message).ToList();
		}
	}
}
