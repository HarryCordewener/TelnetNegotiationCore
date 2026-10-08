using System.Text.Json.Nodes;
using System.Threading.Tasks;
using TelnetNegotiationCore.Gmcp;
using TUnit.Core;

namespace TelnetNegotiationCore.UnitTests;

/// <summary>
/// The GMCP messages Mudlet handles beyond the published specifications, against the keys Mudlet
/// reads and writes (src/TMedia.cpp, src/GMCPAuthenticator.cpp, src/ctelnet.cpp, src/TLuaInterpreter.cpp).
/// </summary>
public class GmcpMudletPackageTests : BaseTest
{
	[Test]
	public async Task MediaPauseUsesTheKeysMudletReads()
	{
		var pause = new MediaPause { Name = "rain.mp3", Type = MediaType.Music, Tag = "weather", Priority = 40, Key = "rain" };

		await AssertJsonEqual(pause.ToJson(), """{"name":"rain.mp3","type":"music","tag":"weather","priority":40,"key":"rain"}""");
		await Assert.That(pause.Package).IsEqualTo("Client.Media.Pause");
		await Assert.That(MediaPause.TryParse("""{"key":"rain","type":"MUSIC"}""", out var read)).IsTrue();
		await Assert.That(read).IsEqualTo(new MediaPause { Key = "rain", Type = MediaType.Music });
	}

	/// <summary>
	/// <c>{"baseui": false}</c> declines Mudlet's starter UI; the plain form is a version line and a URL line.
	/// </summary>
	[Test]
	public async Task ClientGuiReadsBaseUiAndThePlainForm()
	{
		await AssertJsonEqual(ClientGui.DeclineBaseUi.ToJson(), """{"baseui":false}""");
		await Assert.That(ClientGui.TryParse("""{"baseui":"false"}""", out var declined)).IsTrue();
		await Assert.That(declined.BaseUi).IsFalse();

		await Assert.That(ClientGui.TryParse("39\r\nhttp://www.stickmud.com/mudwww/StickMUD.mpackage", out var plain)).IsTrue();
		await Assert.That(plain).IsEqualTo(new ClientGui("39", "http://www.stickmud.com/mudwww/StickMUD.mpackage"));
		await Assert.That(ClientGui.TryParse("39", out _)).IsFalse();
		await Assert.That(ClientGui.TryParse("{}", out _)).IsFalse();
	}

	/// <summary>
	/// Version 2 fields, and the older <c>nonce</c> key Mudlet still reads when <c>nonce_required</c> is absent.
	/// </summary>
	[Test]
	public async Task LoginDefaultCarriesVersionTwoFields()
	{
		var offer = new LoginDefault([LoginDefault.OAuth], "https://example.com/.well-known/openid-configuration")
		{
			Version = 2,
			ClientId = "mud-client",
			Scopes = ["openid", "profile"],
			NonceRequired = true
		};

		await AssertJsonEqual(offer.ToJson(), """
			{"version":2,"type":["oauth"],"location":"https://example.com/.well-known/openid-configuration",
			 "client_id":"mud-client","scopes":["openid","profile"],"nonce_required":true}
			""");

		await Assert.That(LoginDefault.TryParse("""{"version":"2","type":["oauth"],"nonce":"true"}""", out var read)).IsTrue();
		await Assert.That(read.Version).IsEqualTo(2);
		await Assert.That(read.NonceRequired).IsTrue();
		await Assert.That(LoginDefault.TryParse("""{"type":["oauth"],"nonce_required":false,"nonce":true}""", out var preferred)).IsTrue();
		await Assert.That(preferred.NonceRequired).IsFalse();
	}

	/// <summary>
	/// The three shapes Mudlet sends: credentials, a version 2 hand-off with no account, and a provider resume.
	/// </summary>
	[Test]
	public async Task CredentialsReadEveryShapeMudletSends()
	{
		await Assert.That(LoginCredentials.TryParse("""{"account":"Olad","password":"hunter2","version":2,"token_storage":true}""", out var credentials)).IsTrue();
		await Assert.That(credentials.Version).IsEqualTo(2);
		await Assert.That(credentials.TokenStorage).IsTrue();

		await Assert.That(LoginCredentials.TryParse("""{"version":2,"token_storage":true}""", out var handOff)).IsTrue();
		await Assert.That(handOff.IsEmpty).IsTrue();

		await Assert.That(LoginCredentials.TryParse("""{"account":"Olad","provider":"discord","version":2,"token_storage":true}""", out var resume)).IsTrue();
		await Assert.That(resume.Provider).IsEqualTo("discord");
		await Assert.That(resume.Password).IsNull();
		await Assert.That(resume.IsEmpty).IsFalse();

		await AssertJsonEqual(new LoginCredentials("Olad", null) { Provider = "discord", Version = 2 }.ToJson(),
			"""{"account":"Olad","provider":"discord","version":2}""");
	}

	[Test]
	public async Task LoginUrlNeedsAUrl()
	{
		await AssertJsonEqual(new LoginUrl("https://example.com/login", "Discord").ToJson(), """{"url":"https://example.com/login","provider":"Discord"}""");
		await Assert.That(LoginUrl.TryParse("""{"url":"https://example.com/login"}""", out var url)).IsTrue();
		await Assert.That(url.Provider).IsNull();
		await Assert.That(LoginUrl.TryParse("""{"provider":"Discord"}""", out _)).IsFalse();
	}

	/// <summary>
	/// A token and a reconnect round-trip, need both fields, and keep the token out of <c>ToString</c>.
	/// </summary>
	[Test]
	public async Task TokensRoundTripAndStayOutOfLogs()
	{
		var token = new LoginToken("Olad", "s3cret") { SecureOnly = true };
		await AssertJsonEqual(token.ToJson(), """{"account":"Olad","token":"s3cret","secure_only":true}""");
		await Assert.That(LoginToken.TryParse(token.ToJson(), out var readToken)).IsTrue();
		await Assert.That(readToken).IsEqualTo(token);
		await Assert.That(token.ToString()).DoesNotContain("s3cret");
		await Assert.That(LoginToken.TryParse("""{"account":"Olad"}""", out _)).IsFalse();

		var reconnect = new LoginReconnect("Olad", "s3cret") { Version = 2, TokenStorage = true };
		await AssertJsonEqual(reconnect.ToJson(), """{"account":"Olad","token":"s3cret","version":2,"token_storage":true}""");
		await Assert.That(LoginReconnect.TryParse(reconnect.ToJson(), out var readReconnect)).IsTrue();
		await Assert.That(readReconnect).IsEqualTo(reconnect);
		await Assert.That(reconnect.ToString()).DoesNotContain("s3cret");
		await Assert.That(LoginReconnect.TryParse("""{"token":"s3cret"}""", out _)).IsFalse();
	}

	[Test]
	public async Task AuthCodeRoundTripsAndStaysOutOfLogs()
	{
		var code = new LoginAuthCode("abc", "verifier", "http://127.0.0.1:5000/callback") { Nonce = "n0nce", Version = 2, TokenStorage = true };

		await AssertJsonEqual(code.ToJson(), """
			{"code":"abc","code_verifier":"verifier","redirect_uri":"http://127.0.0.1:5000/callback",
			 "nonce":"n0nce","version":2,"token_storage":true}
			""");
		await Assert.That(LoginAuthCode.TryParse(code.ToJson(), out var read)).IsTrue();
		await Assert.That(read).IsEqualTo(code);
		await Assert.That(code.ToString()).DoesNotContain("abc");
		await Assert.That(code.ToString()).DoesNotContain("verifier");
		await Assert.That(code.ToString()).DoesNotContain("n0nce");
		await Assert.That(LoginAuthCode.TryParse("""{"code":"abc","redirect_uri":"x"}""", out _)).IsFalse();
	}

	/// <summary>
	/// Mudlet opens the composer only when both <c>title</c> and <c>text</c> are present.
	/// </summary>
	[Test]
	public async Task ComposerEditNeedsTitleAndText()
	{
		await AssertJsonEqual(new ComposerEdit("Board post").ToJson(), """{"title":"Board post","text":""}""");
		await Assert.That(ComposerEdit.TryParse("""{"title":"Board post","text":"Hello"}""", out var edit)).IsTrue();
		await Assert.That(edit).IsEqualTo(new ComposerEdit("Board post", "Hello"));
		await Assert.That(ComposerEdit.TryParse("""{"title":"Board post"}""", out _)).IsFalse();
	}

	/// <summary>
	/// The body is a JSON string, left out when empty, and Mudlet leaves tabs unescaped.
	/// </summary>
	[Test]
	public async Task ComposerSetBufferIsAJsonString()
	{
		await Assert.That(new ComposerSetBuffer("line one\nsaid \"hi\"").ToJson()).IsEqualTo(JsonValue.Create("line one\nsaid \"hi\"").ToJsonString());
		await Assert.That(new ComposerSetBuffer("").ToJson()).IsEqualTo("");

		await Assert.That(ComposerSetBuffer.TryParse("\"line one\\nsaid \\\"hi\\\"\"", out var escaped)).IsTrue();
		await Assert.That(escaped.Text).IsEqualTo("line one\nsaid \"hi\"");
		await Assert.That(ComposerSetBuffer.TryParse("\"a\tb\"", out var tab)).IsTrue();
		await Assert.That(tab.Text).IsEqualTo("a\tb");
		await Assert.That(ComposerSetBuffer.TryParse("", out var empty)).IsTrue();
		await Assert.That(empty.Text).IsEqualTo("");
		await Assert.That(ComposerSetBuffer.TryParse("""{"text":"x"}""", out _)).IsFalse();
	}

	private static async Task AssertJsonEqual(string actual, string expected)
	{
		await Assert.That(JsonNode.DeepEquals(JsonNode.Parse(actual), JsonNode.Parse(expected))).IsTrue();
	}
}
