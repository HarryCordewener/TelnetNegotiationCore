using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TelnetNegotiationCore.Builders;
using TelnetNegotiationCore.Interpreters;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Protocols;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// Drives real MSSP subnegotiation bytes into a client interpreter and checks what the library
/// reports back against the specification at https://mudhalla.net/tintin/protocols/mssp/.
/// </summary>
public class MSSPSpecificationTests : BaseTest
{
	private static byte[] Var(string name) =>
		new[] { (byte)Trigger.MSSP_VAR }.Concat(Encoding.ASCII.GetBytes(name)).ToArray();

	private static byte[] Val(string value) =>
		new[] { (byte)Trigger.MSSP_VAL }.Concat(Encoding.ASCII.GetBytes(value)).ToArray();

	private static byte[] Subnegotiation(params byte[][] fields) =>
		new[] { (byte)Trigger.IAC, (byte)Trigger.SB, (byte)Trigger.MSSP }
			.Concat(fields.SelectMany(field => field))
			.Concat(new[] { (byte)Trigger.IAC, (byte)Trigger.SE })
			.ToArray();

	/// <summary>
	/// Negotiates MSSP as a client, feeds it one server subnegotiation built from
	/// <paramref name="fields"/>, and returns the config handed to the OnMSSP callback.
	/// </summary>
	private static async Task<MSSPConfig> ReceiveAsync(params byte[][] fields)
	{
		MSSPConfig received = null;

		var client = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Client)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(_ => ValueTask.CompletedTask)
			.AddPlugin<MSSPProtocol>()
				.OnMSSP(config =>
				{
					received = config;
					return ValueTask.CompletedTask;
				})
			.BuildAsync();

		await InterpretAndWaitAsync(client, new byte[] { (byte)Trigger.IAC, (byte)Trigger.WILL, (byte)Trigger.MSSP });
		await InterpretAndWaitAsync(client, Subnegotiation(fields));
		await PollUntilAsync(() => received != null);

		await client.DisposeAsync();

		await Assert.That(received).IsNotNull();
		return received;
	}

	/// <summary>
	/// "It's also possible to attach several values to a single variable by using MSSP_VAL more than
	/// once, with the default value reported last."
	/// </summary>
	[Test]
	public async Task RepeatedValuesUnderOneVariableAreKeptSeparate()
	{
		var config = await ReceiveAsync(Var("PORT"), Val("80"), Val("23"), Val("4201"));

		await Assert.That(config.Variables["PORT"]).IsEquivalentTo(new[] { "80", "23", "4201" });
		await Assert.That(config.Variables.Default("PORT")).IsEqualTo("4201");
		await Assert.That(config.Port).IsEqualTo(4201);
	}

	/// <summary>
	/// "The same variable can be send more than once with different values, in which case the last
	/// reported value should be used as the default value." Both spellings of an array mean the same
	/// thing, so this must agree with the test above.
	/// </summary>
	[Test]
	public async Task RepeatingAVariableAppendsToItsValues()
	{
		var config = await ReceiveAsync(
			Var("PORT"), Val("80"),
			Var("PORT"), Val("23"),
			Var("PORT"), Val("4201"));

		await Assert.That(config.Variables["PORT"]).IsEquivalentTo(new[] { "80", "23", "4201" });
		await Assert.That(config.Variables.Count).IsEqualTo(1);
		await Assert.That(config.Port).IsEqualTo(4201);
	}

	/// <summary>
	/// REFERRAL is "a list of other MSSP enabled MUDs for the crawler to check using the host port
	/// format and array notation" -- it is nothing but an array, and it is the variable a crawler
	/// runs on.
	/// </summary>
	[Test]
	public async Task ReferralKeepsEveryEntry()
	{
		var config = await ReceiveAsync(
			Var("REFERRAL"),
			Val("example.org 4000"),
			Val("mud.example.net 23"),
			Val("2001:db8::1 4201"));

		await Assert.That(config.Referral).IsNotNull();
		await Assert.That(config.Referral.ToArray()).IsEquivalentTo(new[]
		{
			"example.org 4000",
			"mud.example.net 23",
			"2001:db8::1 4201"
		});
		await Assert.That(config.Variables["REFERRAL"].Count).IsEqualTo(3);
	}

	/// <summary>
	/// REFERRAL spelled as a repeated variable rather than repeated values.
	/// </summary>
	[Test]
	public async Task ReferralKeepsEveryEntryWhenSpelledAsRepeatedVariables()
	{
		var config = await ReceiveAsync(
			Var("REFERRAL"), Val("example.org 4000"),
			Var("REFERRAL"), Val("mud.example.net 23"));

		await Assert.That(config.Referral.ToArray()).IsEquivalentTo(new[]
		{
			"example.org 4000",
			"mud.example.net 23"
		});
	}

	/// <summary>
	/// The Protocols, Commercial and Hiring variables are all "1 or 0" on the wire.
	/// </summary>
	[Test]
	public async Task BooleansBindFromTheirWireForm()
	{
		var config = await ReceiveAsync(
			Var("ANSI"), Val("1"),
			Var("UTF-8"), Val("1"),
			Var("VT100"), Val("0"),
			Var("XTERM 256 COLORS"), Val("1"),
			Var("PAY TO PLAY"), Val("0"),
			Var("HIRING CODERS"), Val("1"));

		await Assert.That(config.Ansi).IsTrue();
		await Assert.That(config.UTF_8).IsTrue();
		await Assert.That(config.VT100).IsFalse();
		await Assert.That(config.XTerm_256_Colors).IsTrue();
		await Assert.That(config.Pay_To_Play).IsFalse();
		await Assert.That(config.Hiring_Coders).IsTrue();

		await Assert.That(config.Variables.Flag("ANSI")).IsTrue();
		await Assert.That(config.Variables.Flag("PAY TO PLAY")).IsFalse();
	}

	/// <summary>
	/// A variable outside the specification's tables must survive: MSSP exists so servers can
	/// describe themselves, and codebases invent their own names.
	/// </summary>
	[Test]
	public async Task AnUnknownVariableSurvives()
	{
		var config = await ReceiveAsync(
			Var("NAME"), Val("Test MUD"),
			Var("SSH"), Val("2222"),
			Var("WORLD ORIGINALITY"), Val("All Original"));

		await Assert.That(config.Name).IsEqualTo("Test MUD");

		await Assert.That(config.Variables["SSH"]).IsEquivalentTo(new[] { "2222" });
		await Assert.That(config.Variables["WORLD ORIGINALITY"]).IsEquivalentTo(new[] { "All Original" });

		await Assert.That(config.Extended.ContainsKey("SSH")).IsTrue();
		await Assert.That(((IReadOnlyList<string>)config.Extended["SSH"])[0]).IsEqualTo("2222");

		await Assert.That(config.Variables.UnofficialNames.ToArray())
			.IsEquivalentTo(new[] { "SSH", "WORLD ORIGINALITY" });
		await Assert.That(config.Variables.OfficialNames.ToArray()).IsEquivalentTo(new[] { "NAME" });
	}

	/// <summary>
	/// CHARSET is an official Generic variable and is array-capable: "You can report multiple
	/// charsets using the array format, the preferred / default charset last."
	/// </summary>
	[Test]
	public async Task CharsetSurvivesAsAnOfficialArrayVariable()
	{
		var config = await ReceiveAsync(Var("CHARSET"), Val("ASCII"), Val("ISO-8859-1"), Val("UTF-8"));

		await Assert.That(config.Charset).IsNotNull();
		await Assert.That(config.Charset.ToArray()).IsEquivalentTo(new[] { "ASCII", "ISO-8859-1", "UTF-8" });
		await Assert.That(config.Variables.Default("CHARSET")).IsEqualTo("UTF-8");
		await Assert.That(MSSPVariables.IsOfficial("CHARSET")).IsTrue();
	}

	/// <summary>
	/// "As many programming languages have difficulties with variable names which contain spaces
	/// clients and crawlers can substitute spaces with underscores as the recommended solution."
	/// A server using that substitution means the same variable.
	/// </summary>
	[Test]
	public async Task UnderscoresInVariableNamesMeanSpaces()
	{
		var config = await ReceiveAsync(
			Var("CRAWL_DELAY"), Val("11"),
			Var("MINIMUM_AGE"), Val("18"),
			Var("XTERM_TRUE_COLORS"), Val("1"),
			Var("PAY_FOR_PERKS"), Val("0"));

		await Assert.That(config.Crawl_Delay).IsEqualTo(11);
		await Assert.That(config.Minimum_Age).IsEqualTo("18");
		await Assert.That(config.XTerm_True_Colors).IsTrue();
		await Assert.That(config.Pay_For_Perks).IsFalse();

		// Stored under the specification's own spaced spelling, and reachable by either spelling.
		await Assert.That(config.Variables.Keys.ToArray()).IsEquivalentTo(new[]
		{
			"CRAWL DELAY", "MINIMUM AGE", "XTERM TRUE COLORS", "PAY FOR PERKS"
		});
		await Assert.That(config.Variables["CRAWL_DELAY"]).IsEquivalentTo(new[] { "11" });
		await Assert.That(config.Variables["crawl delay"]).IsEquivalentTo(new[] { "11" });
	}

	/// <summary>
	/// A variable spelled both ways in one payload is one variable, not two.
	/// </summary>
	[Test]
	public async Task TheTwoSpellingsOfANameAreOneVariable()
	{
		var config = await ReceiveAsync(
			Var("CRAWL_DELAY"), Val("1"),
			Var("CRAWL DELAY"), Val("23"));

		await Assert.That(config.Variables.Count).IsEqualTo(1);
		await Assert.That(config.Variables["CRAWL DELAY"]).IsEquivalentTo(new[] { "1", "23" });
		await Assert.That(config.Crawl_Delay).IsEqualTo(23);
	}

	/// <summary>
	/// "CRAWL DELAY: Preferred minimum number of hours between crawls. Send -1 to use the crawler's
	/// default." -1 is a meaningful answer and must reach the consumer, not be swallowed.
	/// </summary>
	[Test]
	public async Task CrawlDelayOfMinusOneReachesTheConsumer()
	{
		var config = await ReceiveAsync(Var("CRAWL DELAY"), Val("-1"));

		await Assert.That(config.Crawl_Delay).IsEqualTo(-1);
		await Assert.That(config.Variables.Integer("CRAWL DELAY")).IsEqualTo(-1);
	}

	/// <summary>
	/// A whole realistic report: nothing is lost, and wire order is preserved.
	/// </summary>
	[Test]
	public async Task ACompleteReportIsPreservedInWireOrder()
	{
		var config = await ReceiveAsync(
			Var("NAME"), Val("Test MUD"),
			Var("PLAYERS"), Val("52"),
			Var("UPTIME"), Val("1234567890"),
			Var("PORT"), Val("80"), Val("23"), Val("4201"),
			Var("CHARSET"), Val("ASCII"), Val("UTF-8"),
			Var("REFERRAL"), Val("example.org 4000"),
			Var("CRAWL DELAY"), Val("-1"),
			Var("ANSI"), Val("1"),
			Var("DISCORD"), Val("https://discord.gg/example"),
			Var("SSH"), Val("2222"));

		await Assert.That(config.Variables.Keys.ToArray()).IsEquivalentTo(new[]
		{
			"NAME", "PLAYERS", "UPTIME", "PORT", "CHARSET", "REFERRAL", "CRAWL DELAY", "ANSI",
			"DISCORD", "SSH"
		});

		await Assert.That(config.Name).IsEqualTo("Test MUD");
		await Assert.That(config.Players).IsEqualTo(52);
		await Assert.That(config.Uptime).IsEqualTo(1234567890);
		await Assert.That(config.Port).IsEqualTo(4201);
		await Assert.That(config.Discord).IsEqualTo("https://discord.gg/example");
		await Assert.That(config.Ansi).IsTrue();
		await Assert.That(config.Crawl_Delay).IsEqualTo(-1);
		await Assert.That(config.Variables["PORT"].Count).IsEqualTo(3);
		await Assert.That(config.Variables["CHARSET"].Count).IsEqualTo(2);
		await Assert.That(config.Extended.ContainsKey("SSH")).IsTrue();
	}

	/// <summary>
	/// "The value can be an empty string unless a numeric value is expected."
	/// </summary>
	[Test]
	public async Task AnEmptyValueIsAValue()
	{
		var config = await ReceiveAsync(
			Var("INTERMUD"), Val(""),
			Var("NAME"), Val("Test MUD"));

		await Assert.That(config.Variables.ContainsKey("INTERMUD")).IsTrue();
		await Assert.That(config.Variables["INTERMUD"]).IsEquivalentTo(new[] { string.Empty });
		await Assert.That(config.Name).IsEqualTo("Test MUD");
	}

	/// <summary>
	/// A payload whose last field is a variable name with no MSSP_VAL is malformed, but must not
	/// wedge the state machine or lose the variables that came before it.
	/// </summary>
	[Test]
	public async Task ATrailingVariableWithNoValueDoesNotLoseTheReport()
	{
		var config = await ReceiveAsync(
			Var("NAME"), Val("Test MUD"),
			Var("GAMEPLAY"));

		await Assert.That(config.Name).IsEqualTo("Test MUD");
		await Assert.That(config.Variables.ContainsKey("GAMEPLAY")).IsTrue();
		await Assert.That(config.Variables["GAMEPLAY"].Count).IsEqualTo(0);
	}

	/// <summary>
	/// A received report re-sent by a server is byte-identical in content: arrays keep their values
	/// and unknown variables keep their names.
	/// </summary>
	[Test]
	public async Task AReceivedReportRoundTripsBackOntoTheWire()
	{
		var received = await ReceiveAsync(
			Var("NAME"), Val("Test MUD"),
			Var("PORT"), Val("80"), Val("23"), Val("4201"),
			Var("REFERRAL"), Val("example.org 4000"), Val("mud.example.net 23"),
			Var("SSH"), Val("2222"));

		byte[] sent = null;

		var server = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(data =>
			{
				var bytes = data.ToArray();
				if (bytes.Length > 3 && bytes[1] == (byte)Trigger.SB) sent = bytes;
				return ValueTask.CompletedTask;
			})
			.AddPlugin<MSSPProtocol>()
			.BuildAsync();

		server.PluginManager!.GetPlugin<MSSPProtocol>()!.SetMSSPConfig(() => received);

		await InterpretAndWaitAsync(server, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MSSP });
		await PollUntilAsync(() => sent != null);

		await Assert.That(sent).IsNotNull();
		var reparsed = Parse(sent);

		await Assert.That(reparsed["PORT"]).IsEquivalentTo(new[] { "80", "23", "4201" });
		await Assert.That(reparsed["REFERRAL"]).IsEquivalentTo(new[] { "example.org 4000", "mud.example.net 23" });
		await Assert.That(reparsed["SSH"]).IsEquivalentTo(new[] { "2222" });
		await Assert.That(reparsed["NAME"]).IsEquivalentTo(new[] { "Test MUD" });

		// Every variable exactly once, even though NAME and PORT also have typed properties.
		await Assert.That(reparsed.Count).IsEqualTo(4);

		await server.DisposeAsync();
	}

	/// <summary>
	/// A configuration built by hand still sends exactly what it always did -- the variable map is
	/// empty, so the typed properties and Extended remain the only source.
	/// </summary>
	[Test]
	public async Task AHandBuiltConfigurationStillSendsItsPropertiesAndExtended()
	{
		byte[] sent = null;

		var server = await new TelnetInterpreterBuilder()
			.UseMode(TelnetInterpreter.TelnetMode.Server)
			.UseLogger(logger)
			.OnSubmit(NoOpSubmitCallback)
			.OnNegotiation(data =>
			{
				var bytes = data.ToArray();
				if (bytes.Length > 3 && bytes[1] == (byte)Trigger.SB) sent = bytes;
				return ValueTask.CompletedTask;
			})
			.AddPlugin<MSSPProtocol>()
			.BuildAsync();

		server.PluginManager!.GetPlugin<MSSPProtocol>()!.SetMSSPConfig(() => new MSSPConfig
		{
			Name = "Test MUD",
			Players = 42,
			Ansi = true,
			Gameplay = ["Adventure", "Roleplaying"],
			Extended = new Dictionary<string, dynamic> { { "CustomField", "CustomValue" } }
		});

		await InterpretAndWaitAsync(server, new byte[] { (byte)Trigger.IAC, (byte)Trigger.DO, (byte)Trigger.MSSP });
		await PollUntilAsync(() => sent != null);

		var reparsed = Parse(sent);
		await Assert.That(reparsed["NAME"]).IsEquivalentTo(new[] { "Test MUD" });
		await Assert.That(reparsed["PLAYERS"]).IsEquivalentTo(new[] { "42" });
		await Assert.That(reparsed["ANSI"]).IsEquivalentTo(new[] { "1" });
		await Assert.That(reparsed["GAMEPLAY"]).IsEquivalentTo(new[] { "Adventure", "Roleplaying" });
		await Assert.That(reparsed["CUSTOMFIELD"]).IsEquivalentTo(new[] { "CustomValue" });

		await server.DisposeAsync();
	}

	/// <summary>
	/// The name substitution is a property of the vocabulary, not just of parsing.
	/// </summary>
	[Test]
	public async Task CanonicalizeFoldsUnderscoresAndCase()
	{
		await Assert.That(MSSPVariables.Canonicalize("crawl_delay")).IsEqualTo("CRAWL DELAY");
		await Assert.That(MSSPVariables.Canonicalize("  minimum   age ")).IsEqualTo("MINIMUM AGE");
		await Assert.That(MSSPVariables.Canonicalize("XTERM_256_COLORS")).IsEqualTo("XTERM 256 COLORS");
		await Assert.That(MSSPVariables.Canonicalize("utf-8")).IsEqualTo("UTF-8");
		await Assert.That(MSSPVariables.IsOfficial("PAY_TO_PLAY")).IsTrue();
		await Assert.That(MSSPVariables.IsOfficial("PUEBLO")).IsFalse();
		await Assert.That(MSSPVariables.IsOfficial("MSP")).IsFalse();
		await Assert.That(MSSPVariables.IsKnown("PUEBLO")).IsTrue();
		await Assert.That(MSSPVariables.IsKnown("SSH")).IsFalse();
	}

	/// <summary>
	/// Reads an <c>IAC SB MSSP ... IAC SE</c> payload back into an ordered name to values map.
	/// </summary>
	private static MSSPVariableCollection Parse(byte[] subnegotiation)
	{
		var parsed = new MSSPVariableCollection();
		var field = new List<byte>();
		string variable = null;
		var isValue = false;

		void Flush()
		{
			if (field.Count == 0 && !isValue) return;
			var text = Encoding.ASCII.GetString(field.ToArray());
			field.Clear();
			if (isValue)
			{
				if (variable != null) parsed.Add(variable, text);
				return;
			}

			if (text.Length == 0) return;
			variable = text;
			parsed.Declare(text);
		}

		// Skip IAC SB MSSP, stop before the trailing IAC SE.
		for (var i = 3; i < subnegotiation.Length - 2; i++)
		{
			switch (subnegotiation[i])
			{
				case (byte)Trigger.MSSP_VAR:
					Flush();
					isValue = false;
					break;
				case (byte)Trigger.MSSP_VAL:
					Flush();
					isValue = true;
					break;
				default:
					field.Add(subnegotiation[i]);
					break;
			}
		}

		Flush();
		return parsed;
	}
}
