using System.Collections.Generic;

namespace TelnetNegotiationCore.Gmcp;

/// <summary>
/// <c>Char.Login.Default</c>: the sign-in methods the server accepts, most preferred first. Sent
/// "in response to Core.Supports.Set, or as soon as telnet negotiation completes", to a client
/// that listed <c>Char.Login</c>.
/// </summary>
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

	/// <inheritdoc />
	public string Package => GmcpPackages.CharLoginDefault;

	/// <inheritdoc />
	public string ToJson() => new JsonFieldWriter().Add("type", Types).Add("location", Location).ToString();

	/// <summary>Reads a <c>Char.Login.Default</c> body.</summary>
	public static bool TryParse(string? data, out LoginDefault message) =>
		JsonFieldReader.TryRead(data, fields => new LoginDefault(fields.Strings("type") ?? [], fields.String("location")), out message);
}

/// <summary>
/// <c>Char.Login.Credentials</c>: the client's account name and password, after the server offered
/// <c>password-credentials</c>. A client that knows neither sends an empty object, which reads
/// with both null and <see cref="IsEmpty"/> true.
/// </summary>
/// <remarks>
/// The account may name a character after a colon, <c>myaccount:mycharacter</c>. The password
/// travels in the clear unless the connection runs over TLS. <see cref="ToString"/> leaves it out,
/// so a logged message does not record it.
/// </remarks>
/// <param name="Account">The account or character name.</param>
/// <param name="Password">The password.</param>
public sealed record LoginCredentials(string? Account, string? Password) : IGmcpMessage
{
	/// <summary>True when the client sent neither, meaning it does not know them.</summary>
	public bool IsEmpty => Account is null && Password is null;

	/// <inheritdoc />
	public string Package => GmcpPackages.CharLoginCredentials;

	/// <inheritdoc />
	public string ToJson() => new JsonFieldWriter().Add("account", Account).Add("password", Password).ToString();

	/// <summary>The account, without the password.</summary>
	public override string ToString() => $"{nameof(LoginCredentials)} {{ Account = {Account} }}";

	/// <summary>Reads a <c>Char.Login.Credentials</c> body.</summary>
	public static bool TryParse(string? data, out LoginCredentials message) =>
		JsonFieldReader.TryRead(data, fields => new LoginCredentials(fields.String("account"), fields.String("password")), out message);
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
