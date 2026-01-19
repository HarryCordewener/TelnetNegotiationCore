using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OneOf;
using Stateless;
using TelnetNegotiationCore.Models;

namespace TelnetNegotiationCore.Interpreters;

/// <summary>
/// Implements RFC 2066: 
///
/// RFC 2066: http://www.faqs.org/rfcs/rfc2066.html
/// </summary>
public partial class TelnetInterpreter
{
#pragma warning disable CS0414 // Field is assigned but never used in this partial - used in CharsetProtocol
	/// <summary>
	/// Internal Charset Byte State
	/// </summary>
	private byte[] _charsetByteState = [];

	/// <summary>
	/// Internal Charset Byte Index Value
	/// </summary>
	private int _charsetByteIndex = 0;

	/// <summary>
	/// Internal Accepted Charset Byte State
	/// </summary>
	private byte[] _acceptedCharsetByteState = [];

	/// <summary>
	/// Internal Accepted Charset Byte Index Value
	/// </summary>
	private int _acceptedCharsetByteIndex = 0;

	private bool _charsetOffered = false;
#pragma warning restore CS0414

	private Func<IEnumerable<EncodingInfo>> AllowedEncodings { get; set; } = Encoding.GetEncodings;

	private Func<IEnumerable<EncodingInfo>, IOrderedEnumerable<Encoding>> _charsetOrder = x 
		=> x.Select(y => y.GetEncoding()).OrderBy(z => z.EncodingName);

	private Func<Encoding, ValueTask>? SignalCharsetChangeAsync { get; set; }

	private Lazy<byte[]> SupportedCharacterSets { get; }

	/// <summary>
	/// Gets the charset order, preferring plugin config over interpreter config
	/// </summary>
	private IOrderedEnumerable<Encoding> GetCharsetOrder(IEnumerable<EncodingInfo> encodings)
	{
		var charsetPlugin = PluginManager?.GetPlugin<Protocols.CharsetProtocol>();
		if (charsetPlugin != null && charsetPlugin.IsEnabled && charsetPlugin.CharsetOrder != null)
		{
			var ordered = charsetPlugin.CharsetOrder.Reverse().ToList();
			return encodings.Select(y => y.GetEncoding()).OrderByDescending(z => ordered.IndexOf(z));
		}
		return _charsetOrder(encodings);
	}

	/// <summary>
	/// Sets the CharacterSet Order
	/// </summary>
	/// <exception cref="ArgumentOutOfRangeException">codepage is less than zero or greater than 65535.</exception>
	/// <exception cref="ArgumentException">codepage is not supported by the underlying platform.</exception>
	/// <exception cref="NotSupportedException">codepage is not supported by the underlying platform.</exception>
	public IEnumerable<Encoding> CharsetOrder
	{
		init
		{
			var ordered = value.Reverse().ToList();
			_charsetOrder = x => x.Select(y => y.GetEncoding()).OrderByDescending(z => ordered.IndexOf(z));
		}
	}

	/// <summary>
	/// Form the Character Set output, based on the system Encodings at the time of connection startup.
	/// </summary>
	/// <returns>A byte array representing the charset offering.</returns>
	private byte[] CharacterSets()
	{
		return [(byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.CHARSET, (byte)Trigger.REQUEST,
						.. ascii.GetBytes($";{string.Join(";", GetCharsetOrder(AllowedEncodings()).Select(x => x.WebName))}"),
						(byte)Trigger.IAC, (byte)Trigger.SE];
	}
}
