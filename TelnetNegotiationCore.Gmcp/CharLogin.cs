using System.Collections.Generic;

namespace TelnetNegotiationCore.Gmcp;

/// <summary>
/// <c>Char.Login.Default</c>: the sign-in methods the server accepts, most preferred first. Sent
/// "in response to Core.Supports.Set, or as soon as telnet negotiation completes", to a client
/// that listed <c>Char.Login</c>.
/// </summary>
/// <remarks>
/// <see cref="Version"/>, <see cref="ClientId"/>, <see cref="Scopes"/> and <see cref="NonceRequired"/>
/// belong to version 2, which Mudlet implements and announces as <c>Char.Login 2</c>. The client
/// signs in at the lower of the two versions. Mudlet reads the client-driven OAuth fields only over
/// TLS.
/// </remarks>
/// <param name="Types"><c>password-credentials</c>, <c>oauth</c>, or both.</param>
/// <param name="Location">The OpenID configuration URL. Required when <c>oauth</c> is offered.</param>
public sealed record LoginDefault(IReadOnlyList<string> Types, string? Location = null) : IGmcpMessage
{
	/// <summary>The <c>type</c> for an account name and password.</summary>
	public const string PasswordCredentials = "password-credentials";

	/// <summary>The <c>type</c> for an OAuth sign-in.</summary>
	public const string OAuth = "oauth";

	/// <summary>Offers an account name and password, the only flow the specification details.</summary>
	public static LoginDefault Password { get; } = new([PasswordCredentials]);

	/// <summary>The highest <c>Char.Login</c> version the server speaks. Absent means 1.</summary>
	public int? Version { get; init; }

	/// <summary>
	/// The OAuth client id the player's client signs in under, when the server is itself the OpenID
	/// provider and lets the client run the browser sign-in. Version 2.
	/// </summary>
	public string? ClientId { get; init; }

	/// <summary>The scopes the client asks for in that sign-in. Version 2.</summary>
	public IReadOnlyList<string>? Scopes { get; init; }

	/// <summary>True when the client must send a nonce with <see cref="LoginAuthCode"/>. Version 2.</summary>
	public bool? NonceRequired { get; init; }

	/// <inheritdoc />
	public string Package => GmcpPackages.CharLoginDefault;

	/// <inheritdoc />
	public string ToJson() => new JsonFieldWriter()
		.Add("version", Version)
		.Add("type", Types)
		.Add("location", Location)
		.Add("client_id", ClientId)
		.Add("scopes", Scopes)
		.Add("nonce_required", NonceRequired)
		.ToString();

	/// <summary>
	/// Reads a <c>Char.Login.Default</c> body. <c>nonce</c> is read for <see cref="NonceRequired"/>
	/// when <c>nonce_required</c> is absent, as Mudlet does for servers written against its older
	/// behaviour.
	/// </summary>
	public static bool TryParse(string? data, out LoginDefault message) =>
		JsonFieldReader.TryRead(data, fields => new LoginDefault(fields.Strings("type") ?? [], fields.String("location"))
		{
			Version = (int?)fields.Number("version"),
			ClientId = fields.String("client_id"),
			Scopes = fields.Strings("scopes"),
			NonceRequired = fields.Boolean("nonce_required") ?? fields.Boolean("nonce")
		}, out message);
}

/// <summary>
/// <c>Char.Login.Credentials</c>: the client's account name and password, after the server offered
/// <c>password-credentials</c>. A client that knows neither sends an empty object, which reads
/// with both null and <see cref="IsEmpty"/> true.
/// </summary>
/// <remarks>
/// <para>
/// The account may name a character after a colon, <c>myaccount:mycharacter</c>. The password
/// travels in the clear unless the connection runs over TLS. <see cref="ToString"/> leaves it out,
/// so a logged message does not record it.
/// </para>
/// <para>
/// Version 2 adds <see cref="Version"/> and <see cref="TokenStorage"/> to every client message, so
/// a version 2 hand-off to the game's own sign-in screen carries them and no account. It also adds
/// <see cref="Provider"/>: an account and a provider with no password asks to resume an OAuth
/// sign-in with that provider.
/// </para>
/// </remarks>
/// <param name="Account">The account or character name.</param>
/// <param name="Password">The password.</param>
public sealed record LoginCredentials(string? Account, string? Password) : IGmcpMessage
{
	/// <summary>True when the client sent no account, password or provider, meaning it does not know them.</summary>
	public bool IsEmpty => Account is null && Password is null && Provider is null;

	/// <summary>The OAuth provider to resume a sign-in with, such as <c>discord</c>. Version 2.</summary>
	public string? Provider { get; init; }

	/// <summary>The <c>Char.Login</c> version the client is speaking. Version 2.</summary>
	public int? Version { get; init; }

	/// <summary>True when the client would keep a <see cref="LoginToken"/> and replay it. Version 2.</summary>
	public bool? TokenStorage { get; init; }

	/// <inheritdoc />
	public string Package => GmcpPackages.CharLoginCredentials;

	/// <inheritdoc />
	public string ToJson() => new JsonFieldWriter()
		.Add("account", Account)
		.Add("password", Password)
		.Add("provider", Provider)
		.Add("version", Version)
		.Add("token_storage", TokenStorage)
		.ToString();

	/// <summary>Everything but the password.</summary>
	public override string ToString() =>
		$"{nameof(LoginCredentials)} {{ Account = {Account}, Provider = {Provider}, Version = {Version}, TokenStorage = {TokenStorage} }}";

	/// <summary>Reads a <c>Char.Login.Credentials</c> body.</summary>
	public static bool TryParse(string? data, out LoginCredentials message) =>
		JsonFieldReader.TryRead(data, fields => new LoginCredentials(fields.String("account"), fields.String("password"))
		{
			Provider = fields.String("provider"),
			Version = (int?)fields.Number("version"),
			TokenStorage = fields.Boolean("token_storage")
		}, out message);
}

/// <summary>
/// <c>Char.Login.Result</c>: whether the sign-in worked, and why not when it did not.
/// </summary>
/// <param name="Success">Whether the sign-in worked.</param>
/// <param name="Message">Required when it did not: a reason to show, such as "Invalid credentials".</param>
public sealed record LoginResult(bool Success, string? Message = null) : IGmcpMessage
{
	/// <inheritdoc />
	public string Package => GmcpPackages.CharLoginResult;

	/// <inheritdoc />
	public string ToJson() => new JsonFieldWriter().Add("success", Success).Add("message", Message).ToString();

	/// <summary>
	/// Reads a <c>Char.Login.Result</c> body. <c>success</c> "can be a string value for
	/// compatibility with aged MUD drivers", so <c>"true"</c> and <c>1</c> read as true.
	/// </summary>
	public static bool TryParse(string? data, out LoginResult message) =>
		JsonFieldReader.TryRead(data, fields => new LoginResult(fields.Boolean("success") ?? false, fields.String("message")), out message);
}

/// <summary>
/// <c>Char.Login.URL</c>: a web page the player signs in on, for a sign-in the server runs.
/// Version 2. Mudlet opens only <c>http</c> and <c>https</c> links.
/// </summary>
/// <param name="Url">The sign-in page.</param>
/// <param name="Provider">A name for the provider, such as <c>Discord</c>, which the client shows and remembers.</param>
public sealed record LoginUrl(string Url, string? Provider = null) : IGmcpMessage
{
	/// <inheritdoc />
	public string Package => GmcpPackages.CharLoginUrl;

	/// <inheritdoc />
	public string ToJson() => new JsonFieldWriter().Add("url", Url).Add("provider", Provider).ToString();

	/// <summary>Reads a <c>Char.Login.URL</c> body.</summary>
	public static bool TryParse(string? data, out LoginUrl message) =>
		JsonFieldReader.TryRead(data, fields => new LoginUrl(fields.String("url") ?? "", fields.String("provider")), out message)
		&& message.Url.Length > 0;
}

/// <summary>
/// <c>Char.Login.Token</c>: a token the client keeps and sends back in <see cref="LoginReconnect"/>
/// to sign in without a password. Version 2. Sending it again replaces the kept one.
/// </summary>
/// <remarks><see cref="ToString"/> leaves the token out.</remarks>
/// <param name="Account">The account the token signs in to.</param>
/// <param name="Token">The token.</param>
public sealed record LoginToken(string Account, string Token) : IGmcpMessage
{
	/// <summary>
	/// True when the token may only be replayed over TLS. Absent, the token keeps the transport it
	/// arrived on: one sent over TLS is replayed only over TLS.
	/// </summary>
	public bool? SecureOnly { get; init; }

	/// <inheritdoc />
	public string Package => GmcpPackages.CharLoginToken;

	/// <inheritdoc />
	public string ToJson() => new JsonFieldWriter().Add("account", Account).Add("token", Token).Add("secure_only", SecureOnly).ToString();

	/// <summary>Everything but the token.</summary>
	public override string ToString() => $"{nameof(LoginToken)} {{ Account = {Account}, SecureOnly = {SecureOnly} }}";

	/// <summary>Reads a <c>Char.Login.Token</c> body. Both the account and the token are required.</summary>
	public static bool TryParse(string? data, out LoginToken message) =>
		JsonFieldReader.TryRead(data, fields => new LoginToken(fields.String("account") ?? "", fields.String("token") ?? "")
		{
			SecureOnly = fields.Boolean("secure_only")
		}, out message)
		&& message.Account.Length > 0 && message.Token.Length > 0;
}

/// <summary>
/// <c>Char.Login.Reconnect</c>: signs in with a token the server sent in <see cref="LoginToken"/>.
/// Version 2. The server answers with <see cref="LoginResult"/>.
/// </summary>
/// <remarks><see cref="ToString"/> leaves the token out.</remarks>
/// <param name="Account">The account the token was issued for.</param>
/// <param name="Token">The token.</param>
public sealed record LoginReconnect(string Account, string Token) : IGmcpMessage
{
	/// <summary>The <c>Char.Login</c> version the client is speaking.</summary>
	public int? Version { get; init; }

	/// <summary>True when the client keeps tokens. A server must not require it here.</summary>
	public bool? TokenStorage { get; init; }

	/// <inheritdoc />
	public string Package => GmcpPackages.CharLoginReconnect;

	/// <inheritdoc />
	public string ToJson() => new JsonFieldWriter()
		.Add("account", Account)
		.Add("token", Token)
		.Add("version", Version)
		.Add("token_storage", TokenStorage)
		.ToString();

	/// <summary>Everything but the token.</summary>
	public override string ToString() => $"{nameof(LoginReconnect)} {{ Account = {Account}, Version = {Version} }}";

	/// <summary>Reads a <c>Char.Login.Reconnect</c> body. Both the account and the token are required.</summary>
	public static bool TryParse(string? data, out LoginReconnect message) =>
		JsonFieldReader.TryRead(data, fields => new LoginReconnect(fields.String("account") ?? "", fields.String("token") ?? "")
		{
			Version = (int?)fields.Number("version"),
			TokenStorage = fields.Boolean("token_storage")
		}, out message)
		&& message.Account.Length > 0 && message.Token.Length > 0;
}

/// <summary>
/// <c>Char.Login.AuthCode</c>: completes a sign-in the client ran in the browser against the
/// server's own OpenID provider, offered by <see cref="LoginDefault.ClientId"/>. The server
/// exchanges the code with PKCE and answers with <see cref="LoginResult"/>. Version 2. Mudlet sends
/// it only over TLS.
/// </summary>
/// <remarks><see cref="ToString"/> leaves out the code, the verifier and the nonce.</remarks>
/// <param name="Code">The authorization code.</param>
/// <param name="CodeVerifier">The PKCE code verifier.</param>
/// <param name="RedirectUri">The redirect URI the code was issued for.</param>
public sealed record LoginAuthCode(string Code, string CodeVerifier, string RedirectUri) : IGmcpMessage
{
	/// <summary>The nonce sent in the authorization request, for the server to check against the ID token.</summary>
	public string? Nonce { get; init; }

	/// <summary>The <c>Char.Login</c> version the client is speaking.</summary>
	public int? Version { get; init; }

	/// <summary>True when the client would keep a <see cref="LoginToken"/>.</summary>
	public bool? TokenStorage { get; init; }

	/// <inheritdoc />
	public string Package => GmcpPackages.CharLoginAuthCode;

	/// <inheritdoc />
	public string ToJson() => new JsonFieldWriter()
		.Add("code", Code)
		.Add("code_verifier", CodeVerifier)
		.Add("redirect_uri", RedirectUri)
		.Add("nonce", Nonce)
		.Add("version", Version)
		.Add("token_storage", TokenStorage)
		.ToString();

	/// <summary>Only the redirect URI and the version.</summary>
	public override string ToString() => $"{nameof(LoginAuthCode)} {{ RedirectUri = {RedirectUri}, Version = {Version} }}";

	/// <summary>Reads a <c>Char.Login.AuthCode</c> body. The code, verifier and redirect URI are required.</summary>
	public static bool TryParse(string? data, out LoginAuthCode message) =>
		JsonFieldReader.TryRead(data, fields => new LoginAuthCode(fields.String("code") ?? "", fields.String("code_verifier") ?? "", fields.String("redirect_uri") ?? "")
		{
			Nonce = fields.String("nonce"),
			Version = (int?)fields.Number("version"),
			TokenStorage = fields.Boolean("token_storage")
		}, out message)
		&& message.Code.Length > 0 && message.CodeVerifier.Length > 0 && message.RedirectUri.Length > 0;
}
