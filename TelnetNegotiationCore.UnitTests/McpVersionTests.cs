#nullable enable
using System;
using System.Threading.Tasks;
using TelnetNegotiationCore.Models;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// <see cref="McpVersion"/>: two non-negative integers written <c>major.minor</c>.
/// </summary>
public class McpVersionTests : BaseTest
{
	/// <summary>
	/// A negative component is refused at construction rather than at the far end of the wire.
	/// </summary>
	/// <remarks>
	/// The grammar has no sign in it, so a negative version cannot be read back: it would go out as
	/// <c>-1.0</c>, and the peer's parser -- this library's own included -- rejects it. Advertising a
	/// range a peer cannot parse is not a version disagreement, it is a malformed message, and the
	/// place to stop it is where the value is made.
	/// </remarks>
	[Test]
	public async Task ANegativeComponentIsRefused()
	{
		await Assert.That(() => new McpVersion(-1, 0)).Throws<ArgumentOutOfRangeException>();
		await Assert.That(() => new McpVersion(1, -1)).Throws<ArgumentOutOfRangeException>();
	}

	/// <summary>Zero is a version, and the default is 0.0 rather than a refusal.</summary>
	[Test]
	public async Task ZeroIsAllowed()
	{
		await Assert.That(new McpVersion(0, 0).ToString()).IsEqualTo("0.0");
		await Assert.That(default(McpVersion).ToString()).IsEqualTo("0.0");
	}
}
