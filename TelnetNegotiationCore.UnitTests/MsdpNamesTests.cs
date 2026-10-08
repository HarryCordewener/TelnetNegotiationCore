using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using TelnetNegotiationCore.Gmcp;
using TelnetNegotiationCore.Handlers;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// <see cref="MsdpNames"/> against the specification's lists and the lists <see cref="MSDPServerModel"/> answers.
/// </summary>
public class MsdpNamesTests : BaseTest
{
	private static string[] Constants(Type type) =>
		type.GetFields(BindingFlags.Public | BindingFlags.Static)
			.Where(field => field.IsLiteral)
			.Select(field => (string)field.GetRawConstantValue()!)
			.ToArray();

	/// <summary>
	/// Every reportable variable the specification lists, and no others.
	/// </summary>
	[Test]
	public async Task ReportableNamesMatchTheSpecification()
	{
		string[] specification =
		[
			"ACCOUNT_NAME", "CHARACTER_NAME", "SERVER_ID", "SERVER_TIME", "SPECIFICATION",
			"AFFECTS", "ALIGNMENT", "EXPERIENCE", "EXPERIENCE_MAX", "EXPERIENCE_TNL", "EXPERIENCE_TNL_MAX",
			"HEALTH", "HEALTH_MAX", "LEVEL", "MANA", "MANA_MAX", "MONEY", "MOVEMENT", "MOVEMENT_MAX",
			"OPPONENT_LEVEL", "OPPONENT_HEALTH", "OPPONENT_HEALTH_MAX", "OPPONENT_NAME", "OPPONENT_STRENGTH",
			"ROOM", "VNUM", "NAME", "AREA", "COORDS", "X", "Y", "Z", "TERRAIN", "EXITS",
			"WORLD_TIME"
		];

		var names = new[]
		{
			typeof(MsdpNames.General), typeof(MsdpNames.Character), typeof(MsdpNames.Combat),
			typeof(MsdpNames.Mapping), typeof(MsdpNames.World)
		}.SelectMany(Constants).ToArray();

		await Assert.That(names).IsEquivalentTo(specification);
	}

	/// <summary>
	/// The commands, and the configurable variables with the one nonstandard alias.
	/// </summary>
	[Test]
	public async Task CommandsAndConfigurableNamesMatchTheSpecification()
	{
		await Assert.That(Constants(typeof(MsdpNames.Command)))
			.IsEquivalentTo(["LIST", "REPORT", "RESET", "SEND", "UNREPORT"]);
		await Assert.That(Constants(typeof(MsdpNames.Configurable)))
			.IsEquivalentTo(["CLIENT_NAME", "CLIENT_VERSION", "PLUGIN_ID", "CLIENT_ID"]);
		await Assert.That(MsdpNames.GmcpPackage).IsEqualTo("MSDP");
	}

	/// <summary>
	/// The lists named here are the lists the server model answers <c>LIST LISTS</c> with.
	/// </summary>
	[Test]
	public async Task ListNamesMatchTheServerModel()
	{
		var model = new MSDPServerModel(_ => default);

		await Assert.That(model.Lists.Keys.ToArray()).IsEquivalentTo(Constants(typeof(MsdpNames.List)));
	}
}
