using System;
using System.Collections.Generic;
using System.Linq;

namespace TelnetNegotiationCore.UnitTests.Fuzzing;

/// <summary>
/// Builds telnet streams out of weighted tokens rather than out of random bytes.
/// </summary>
/// <remarks>
/// <para>
/// Uniform noise essentially never produces a well-formed <c>IAC SB &lt;option&gt; … IAC SE</c>
/// frame, and frame handling is where the state machine lives, so a byte-level fuzzer would spend
/// its whole budget in the text path. Tokens put the generator where the states are.
/// </para>
/// <para>
/// Every payload grammar here comes from the option's RFC, not from this library's implementation
/// of it. A generator that read its grammar out of the code under test would agree with whatever
/// that code gets wrong. The citations are on each arm of <see cref="Payload"/>, and the full list
/// is in <c>docs/superpowers/specs/2026-09-12-telnet-fuzz-regression-design.md</c>.
/// </para>
/// </remarks>
internal static class TelnetTokens
{
	private const byte EOR = 239;
	private const byte SE = 240;
	private const byte NOP = 241;
	private const byte GA = 249;
	private const byte SB = 250;
	private const byte WILL = 251;
	private const byte WONT = 252;
	private const byte DO = 253;
	private const byte DONT = 254;
	private const byte IAC = 255;

	/// <summary>One piece of a generated stream, tagged so that tests can reason about the mix.</summary>
	internal sealed record Token(string Kind, byte[] Bytes);

	private static readonly byte[] Options =
	[
		1, 3, 24, 25, 31, 32, 33, 34, 35, 36, 37, 38, 39, 42, 69, 70, 85, 86, 87, 91, 201,
	];

	private static readonly byte[] Verbs = [WILL, WONT, DO, DONT];

	private static readonly byte[] Markers = [NOP, GA, EOR];

	/// <summary>A stream of between one and <paramref name="maxTokens"/> tokens.</summary>
	public static List<Token> Stream(Rng rng, int maxTokens)
	{
		var count = 1 + rng.Next(maxTokens);
		var tokens = new List<Token>(count);
		for (var i = 0; i < count; i++)
		{
			tokens.Add(NextToken(rng));
		}

		return tokens;
	}

	public static byte[] Flatten(IReadOnlyList<Token> tokens) =>
		tokens.SelectMany(t => t.Bytes).ToArray();

	public static Token NextToken(Rng rng) => rng.Next(100) switch
	{
		< 35 => new Token("frame", Frame(rng, rng.Pick(Options))),
		< 60 => new Token("text", Text(rng)),
		< 80 => new Token("mutated", Mutated(rng)),
		< 90 => new Token("verb", Verb(rng)),
		_ => new Token("adversarial", Adversarial(rng)),
	};

	/// <summary>A well-formed frame: <c>IAC SB option</c>, an escaped payload, <c>IAC SE</c>.</summary>
	private static byte[] Frame(Rng rng, byte option)
	{
		var body = new List<byte> { IAC, SB, option };
		body.AddRange(Escaped(Payload(rng, option)));
		body.Add(IAC);
		body.Add(SE);
		return [.. body];
	}

	/// <summary>
	/// Doubles every 255. RFC 855 requires it of any subnegotiation payload, and every grammar
	/// below restates it for its own field.
	/// </summary>
	private static List<byte> Escaped(IEnumerable<byte> payload)
	{
		var escaped = new List<byte>();
		foreach (var b in payload)
		{
			escaped.Add(b);
			if (b == IAC)
			{
				escaped.Add(IAC);
			}
		}

		return escaped;
	}

	/// <summary>
	/// The payload for one option, from its RFC. Returned unescaped: <see cref="Escaped"/> handles
	/// the 255 doubling for all of them.
	/// </summary>
	private static List<byte> Payload(Rng rng, byte option) => option switch
	{
		// NAWS, RFC 1073: width and height, two bytes each, network byte order, so a dimension
		// reaches 65535. Zero means "not being sent" and the receiver falls back to its own
		// default, which is a distinct case from a genuine zero.
		31 => [.. Sixteen(rng), .. Sixteen(rng)],

		// TERMINAL-TYPE, RFC 1091: IS=0 name, or SEND=1. The name is NVT ASCII, case-insensitive,
		// maximum 40 characters.
		24 => rng.Bool(50) ? [0, .. Ascii(rng, 40)] : [1],

		// TERMINAL-SPEED, RFC 1079: IS=0 "<transmit>,<receive>" as decimal ASCII with no leading
		// zeros and no spaces, or SEND=1.
		32 => rng.Bool(50) ? [0, .. Digits(rng), (byte)',', .. Digits(rng)] : [1],

		// TOGGLE-FLOW-CONTROL, RFC 1372: one sub-command byte and nothing else. OFF=0, ON=1,
		// RESTART-ANY=2, RESTART-XON=3. Codes outside that range must be silently ignored, so
		// generate past the range deliberately.
		33 => [(byte)rng.Next(6)],

		// LINEMODE, RFC 1184: MODE=1 with a mask, FORWARDMASK=2, or SLC=3 with triplets.
		34 => rng.Next(3) switch
		{
			// MODE mask bits: EDIT=1, TRAPSIG=2, MODE_ACK=4, SOFT_TAB=8, LIT_ECHO=16.
			0 => [1, (byte)rng.Next(32)],
			1 => [2, .. RandomBytes(rng, 1 + rng.Next(32))],
			_ => [3, .. SlcTriplets(rng)],
		},

		// X-DISPLAY-LOCATION, RFC 1096: IS=0 "<host>:<dispnum>[.<screennum>]", or SEND=1.
		35 => rng.Bool(50)
			? [0, .. Ascii(rng, 20), (byte)':', .. Digits(rng)]
			: [1],

		// ENVIRON (36, RFC 1408) and NEW-ENVIRON (39, RFC 1572) share their codes: IS=0 SEND=1
		// INFO=2, and VAR=0 VALUE=1 ESC=2 USERVAR=3.
		36 or 39 => EnvironPayload(rng),

		// AUTHENTICATION, RFC 2941: IS=0 SEND=1 REPLY=2 NAME=3, then two-octet type pairs.
		37 => [(byte)rng.Next(4), .. AuthPairs(rng)],

		// ENCRYPT, RFC 2946: IS=0 SUPPORT=1 REPLY=2 START=3 END=4 REQUEST-START=5 REQUEST-END=6
		// ENC_KEYID=7 DEC_KEYID=8. A START keyid is most significant byte first and at least one
		// byte long, with zero meaning the default key.
		38 => rng.Next(9) switch
		{
			3 => [3, .. RandomBytes(rng, 1 + rng.Next(8))],
			4 => [4],
			var command => [(byte)command, .. RandomBytes(rng, rng.Next(4))],
		},

		// CHARSET, RFC 2066: REQUEST=1 ACCEPTED=2 REJECTED=3 TTABLE-IS=4 TTABLE-REJECTED=5
		// TTABLE-ACK=6 TTABLE-NAK=7.
		42 => CharsetPayload(rng),

		// MSDP: VAR=1 VAL=2 TABLE_OPEN=3 TABLE_CLOSE=4 ARRAY_OPEN=5 ARRAY_CLOSE=6, nestable.
		69 => MsdpPayload(rng, 0),

		// MSSP: VAR=1 VAL=2 in alternating pairs.
		70 => MsspPayload(rng),

		// GMCP: a package name, a space, then JSON.
		201 => [.. Ascii(rng, 12), (byte)' ', .. GmcpJson(rng)],

		// MCCP v1's start marker is IAC SB 85 WILL SE, which is why it is honoured although the
		// option itself is never negotiated.
		85 => [WILL],

		// MCCP v2 and v3 markers carry no payload at all.
		86 or 87 => [],

		// MXP (91) and anything else: a short opaque payload.
		_ => [.. RandomBytes(rng, rng.Next(8))],
	};

	/// <summary>
	/// ENVIRON and NEW-ENVIRON. The escaping is two-layered and the most interesting grammar in the
	/// set: IAC doubles as usual, and within a name or a value each of VAR, VALUE, USERVAR and ESC
	/// is itself escaped by a preceding ESC.
	/// </summary>
	private static List<byte> EnvironPayload(Rng rng)
	{
		const byte Var = 0;
		const byte Value = 1;
		const byte Esc = 2;
		const byte UserVar = 3;

		var payload = new List<byte> { (byte)rng.Next(3) };

		var pairs = rng.Next(3);
		for (var i = 0; i < pairs; i++)
		{
			payload.Add(rng.Bool(50) ? Var : UserVar);
			payload.AddRange(EnvironEscaped(Ascii(rng, 8)));

			if (rng.Bool(70))
			{
				payload.Add(Value);
				payload.AddRange(EnvironEscaped(Ascii(rng, 8)));
			}
		}

		// An ESC as the final byte escapes nothing. RFC 1572 does not define it, so the only
		// defensible contract is that it must not wedge.
		if (rng.Bool(10))
		{
			payload.Add(Esc);
		}

		return payload;

		static List<byte> EnvironEscaped(IEnumerable<byte> name)
		{
			var escaped = new List<byte>();
			foreach (var b in name)
			{
				if (b is Var or Value or Esc or UserVar)
				{
					escaped.Add(Esc);
				}

				escaped.Add(b);
			}

			return escaped;
		}
	}

	/// <summary>
	/// RFC 2941's two-octet authentication-type pairs: the type, then modifier bits
	/// AUTH_WHO_MASK=0x01, AUTH_HOW_MASK=0x02, INI_CRED_FWD_MASK=0x08, ENCRYPT_MASK=0x14.
	/// </summary>
	private static List<byte> AuthPairs(Rng rng)
	{
		var pairs = new List<byte>();
		var count = rng.Next(4);
		for (var i = 0; i < count; i++)
		{
			pairs.Add((byte)rng.Next(16));
			pairs.Add((byte)rng.Next(32));
		}

		// An odd-length list of pairs is malformed, which is exactly why it is generated.
		if (rng.Bool(15))
		{
			pairs.Add((byte)rng.Next(16));
		}

		return pairs;
	}

	/// <summary>
	/// RFC 2066's CHARSET. A REQUEST is optionally prefixed "[TTABLE]&lt;version&gt;" with a
	/// non-zero version octet, then a charset list whose separator is chosen by the sender and may
	/// be any octet except IAC. That the separator is peer-controlled is the sharpest target in the
	/// whole set: the list has to parse when it is a semicolon, a space, a digit, a letter that
	/// also occurs inside a charset name, or zero.
	/// </summary>
	private static List<byte> CharsetPayload(Rng rng)
	{
		var command = 1 + rng.Next(7);
		if (command != 1)
		{
			return [(byte)command, .. Ascii(rng, 10)];
		}

		var payload = new List<byte> { 1 };

		if (rng.Bool(25))
		{
			payload.AddRange("[TTABLE]"u8);
			payload.Add((byte)(1 + rng.Next(3)));
		}

		var separators = new byte[] { (byte)';', (byte)' ', (byte)',', (byte)'A', (byte)'0', 0 };
		var separator = rng.Pick(separators);

		var names = 1 + rng.Next(3);
		for (var i = 0; i < names; i++)
		{
			payload.Add(separator);
			payload.AddRange(Ascii(rng, 8));
		}

		return payload;
	}

	/// <summary>
	/// MSDP, which is arbitrarily nestable, so unbalanced and nested payloads are the cases that
	/// matter. Depth is capped so the generator cannot recurse itself to death; the unbalanced
	/// cases come from the one-in-five chance of omitting the close, not from unbounded nesting.
	/// </summary>
	private static List<byte> MsdpPayload(Rng rng, int depth)
	{
		const byte Var = 1;
		const byte Val = 2;
		const byte TableOpen = 3;
		const byte TableClose = 4;
		const byte ArrayOpen = 5;
		const byte ArrayClose = 6;

		var payload = new List<byte> { Var };
		payload.AddRange(Ascii(rng, 8));
		payload.Add(Val);

		if (depth < 3 && rng.Bool(30))
		{
			var open = rng.Bool(50) ? TableOpen : ArrayOpen;
			payload.Add(open);
			payload.AddRange(MsdpPayload(rng, depth + 1));

			if (rng.Bool(80))
			{
				payload.Add(open == TableOpen ? TableClose : ArrayClose);
			}

			return payload;
		}

		payload.AddRange(Ascii(rng, 8));
		return payload;
	}

	private static List<byte> MsspPayload(Rng rng)
	{
		var payload = new List<byte>();
		var pairs = 1 + rng.Next(4);
		for (var i = 0; i < pairs; i++)
		{
			payload.Add(1);
			payload.AddRange(Ascii(rng, 10));
			payload.Add(2);
			payload.AddRange(Ascii(rng, 10));
		}

		return payload;
	}

	private static List<byte> GmcpJson(Rng rng) => rng.Next(4) switch
	{
		0 => [.. "{}"u8],
		1 => [.. "{\"a\":1}"u8],
		2 => [.. "[1,2,3]"u8],

		// Deliberately invalid JSON: a consumer's parser failing is its business, but the engine
		// must deliver the bytes and carry on either way.
		_ => [.. "{\"broken\":"u8],
	};

	/// <summary>
	/// RFC 1184's SLC triplets: function, then level in the low two bits with SLC_ACK=128,
	/// SLC_FLUSHIN=64 and SLC_FLUSHOUT=32 above it, then the character. Functions run SLC_SYNCH=1
	/// through SLC_EEOL=30.
	/// </summary>
	private static List<byte> SlcTriplets(Rng rng)
	{
		var triplets = new List<byte>();
		var count = 1 + rng.Next(4);
		for (var i = 0; i < count; i++)
		{
			triplets.Add((byte)(1 + rng.Next(30)));
			triplets.Add(rng.NextByte());
			triplets.Add(rng.NextByte());
		}

		// A truncated triplet is undefined by the RFC, so "must not wedge" is the only contract
		// available and the generator produces one on purpose.
		if (rng.Bool(20))
		{
			triplets.Add((byte)(1 + rng.Next(30)));
			if (rng.Bool(50))
			{
				triplets.Add(rng.NextByte());
			}
		}

		return triplets;
	}

	/// <summary>
	/// Plain text, weighted towards the line-ending cases the core machine branches on. RFC 854
	/// makes CR LF the line terminator and CR NUL a bare carriage return, and a real peer sends
	/// both, plus bare LF that no RFC blesses.
	/// </summary>
	private static byte[] Text(Rng rng) => rng.Next(10) switch
	{
		0 => [.. "hello\r\n"u8],
		1 => [.. "bare-lf\n"u8],
		2 => [.. "bare-cr\r"u8],
		3 => [.. "cr-nul\r"u8, 0],
		4 => [.. "\r\n"u8],
		5 => [(byte)'\n'],
		6 => [(byte)'\r'],
		7 => RandomHighBytes(rng, 1 + rng.Next(8)),
		8 => [],
		_ => Ascii(rng, 1 + rng.Next(16)),
	};

	/// <summary>A mutation of a well-formed frame: the slice where most bugs should live.</summary>
	private static byte[] Mutated(Rng rng)
	{
		var frame = Frame(rng, rng.Pick(Options));

		return rng.Next(6) switch
		{
			// Truncated at an arbitrary offset.
			0 => frame[..(1 + rng.Next(frame.Length))],

			// Missing its terminating SE.
			1 => frame[..^1],

			// Option byte replaced, including with an option nothing claims.
			2 => Replace(frame, 2, rng.NextByte()),

			// A stray IAC injected into the payload, which ends the frame early.
			3 => Insert(frame, 3 + rng.Next(Math.Max(1, frame.Length - 4)), IAC),

			// An SB nested inside, which no grammar permits.
			4 => Insert(frame, 3 + rng.Next(Math.Max(1, frame.Length - 4)), SB),

			// A random byte flipped anywhere at all.
			_ => Replace(frame, rng.Next(frame.Length), rng.NextByte()),
		};

		static byte[] Replace(byte[] source, int index, byte value)
		{
			var copy = source.ToArray();
			copy[index] = value;
			return copy;
		}

		static byte[] Insert(byte[] source, int index, byte value)
		{
			var copy = new List<byte>(source);
			copy.Insert(Math.Min(index, copy.Count), value);
			return [.. copy];
		}
	}

	/// <summary>
	/// A bare verb sequence, including the case <c>WillInterrupted</c> exists for: a fresh IAC
	/// where an option byte was expected abandons the pending negotiation and starts the new
	/// command where it actually begins.
	/// </summary>
	private static byte[] Verb(Rng rng) => rng.Next(5) switch
	{
		0 => [IAC, rng.Pick(Verbs), rng.Pick(Options)],
		1 => [IAC, rng.Pick(Verbs), rng.NextByte()],
		2 => [IAC, rng.Pick(Verbs)],
		3 => [IAC, rng.Pick(Verbs), IAC, rng.Pick(Verbs), rng.Pick(Options)],
		_ => [IAC, rng.Pick(Markers)],
	};

	private static byte[] Adversarial(Rng rng) => rng.Next(8) switch
	{
		0 => [IAC],
		1 => [IAC, IAC],
		2 => [IAC, SE],
		3 => [IAC, SB],
		4 => [IAC, SB, IAC, SE],
		5 => [IAC, rng.NextByte()],
		6 => RandomBytes(rng, 1 + rng.Next(16)),
		_ => [IAC, SB, rng.Pick(Options), IAC, SB, rng.Pick(Options), IAC, SE],
	};

	/// <summary>One NAWS dimension, weighted towards the values with meaning.</summary>
	private static byte[] Sixteen(Rng rng) => rng.Next(5) switch
	{
		0 => [0, 0],        // "not being sent"
		1 => [255, 255],    // 65535, and two IACs for the escaper to double
		2 => [0, 80],
		3 => [0, 24],
		_ => [rng.NextByte(), rng.NextByte()],
	};

	private static byte[] Ascii(Rng rng, int maxLength)
	{
		var length = rng.Next(maxLength + 1);
		var bytes = new byte[length];
		for (var i = 0; i < length; i++)
		{
			bytes[i] = (byte)(33 + rng.Next(94));
		}

		return bytes;
	}

	private static byte[] Digits(Rng rng)
	{
		var length = 1 + rng.Next(5);
		var bytes = new byte[length];
		for (var i = 0; i < length; i++)
		{
			bytes[i] = (byte)('0' + rng.Next(10));
		}

		return bytes;
	}

	private static byte[] RandomBytes(Rng rng, int length)
	{
		var bytes = new byte[length];
		for (var i = 0; i < length; i++)
		{
			bytes[i] = rng.NextByte();
		}

		return bytes;
	}

	/// <summary>High bytes, but never 255, so the caller stays in the text path.</summary>
	private static byte[] RandomHighBytes(Rng rng, int length)
	{
		var bytes = new byte[length];
		for (var i = 0; i < length; i++)
		{
			bytes[i] = (byte)(128 + rng.Next(127));
		}

		return bytes;
	}
}
