using System;
using System.Threading.Tasks;
using TelnetNegotiationCore.Models;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// <see cref="State"/> is public and its values are implicit, so a member inserted into a region
/// renumbers every member after it -- and a plugin compiled against an earlier package, which carries
/// the numbers rather than the names, then configures a state other than the one it was written for.
/// </summary>
/// <remarks>
/// Pins the last member each release shipped. Any insertion ahead of it moves it, which a review can
/// miss (2.17.0's first draft filed its MCCP v1 states under the MCCP region) and this cannot. When a
/// release appends members, add a line for its new last member rather than changing an existing one.
/// </remarks>
public class StateNumberingTests
{
	[Test]
	public async Task EveryReleasedStateKeepsItsNumber()
	{
		await Assert.That(NumberOf("CompletingMXP")).IsEqualTo((short)190);     // last in 2.16.0
		await Assert.That(NumberOf("CompletingMCCP1")).IsEqualTo((short)192);   // last in 2.17.0
	}

	/// <summary>Looked up by name, the way the member is known to a plugin's author.</summary>
	private static short NumberOf(string member) => (short)Enum.Parse<State>(member);
}
