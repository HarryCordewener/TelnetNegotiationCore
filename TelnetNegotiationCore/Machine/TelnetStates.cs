using StateAlchemist;

namespace TelnetNegotiationCore.Machine;

// The telnet core as a state tree. What is a member of Models.State today is a type here, and where it sits says
// how long its data lives: the option byte a subnegotiation is about belongs to the subnegotiation and goes when
// it ends, which is the field on the interpreter it replaces.
//
// Connected
// └── Accepting ── Idle [initial], ReadingCharacters, DoNothing, GoAhead
// ├── StartNegotiation
// ├── Willing, Refusing, Do, Dont
// └── SubNegotiation ── ReadingOption [initial], EndSubNegotiation

/// <summary>The connection. Lives as long as the machine does, which is what the window size wants.</summary>
public struct Connected : IRootState
{
    /// <summary>The client's last reported window width. RFC 1073's default until it reports one.</summary>
    public int Width;

    /// <summary>The client's last reported window height.</summary>
    public int Height;
}

/// <summary>
/// Ordinary input. Its children are the places a byte is text rather than a command, so the transitions declared
/// here apply in all of them — which is what <c>SubstateOf(State.Accepting)</c> says today.
/// </summary>
[Initial]
public struct Accepting : IState<Connected>
{
}

/// <summary>Between lines.</summary>
[Initial]
public struct Idle : IState<Accepting>
{
}

/// <summary>Part-way through a line.</summary>
public struct ReadingCharacters : IState<Accepting>
{
}

/// <summary>IAC NOP arrived. Nothing to do, and the next byte is text again.</summary>
public struct DoNothing : IState<Accepting>
{
}

/// <summary>IAC GA arrived: the 1983 prompt marker. Whether it means anything is the plugins' business.</summary>
public struct GoAhead : IState<Accepting>
{
}

/// <summary>IAC arrived, and what follows says what it was.</summary>
public struct StartNegotiation : IState<Connected>
{
}

/// <summary>IAC WILL: the next byte is the option it is about.</summary>
public struct Willing : IState<Connected>
{
}

/// <summary>IAC WONT.</summary>
public struct Refusing : IState<Connected>
{
}

/// <summary>IAC DO.</summary>
public struct Do : IState<Connected>
{
}

/// <summary>IAC DONT.</summary>
public struct Dont : IState<Connected>
{
}

/// <summary>IAC SB: a subnegotiation, until IAC SE.</summary>
public struct SubNegotiation : IState<Connected>
{
    /// <summary>The option this subnegotiation is about. Cleared when it ends, because it belongs to it.</summary>
    public byte Option;
}

/// <summary>Reading the option byte the subnegotiation is about.</summary>
[Initial]
public struct ReadingOption : IState<SubNegotiation>
{
}

/// <summary>The option is known; a protocol's own module reads the payload from here.</summary>
public struct SubNegotiating : IState<SubNegotiation>
{
}

/// <summary>IAC inside a subnegotiation: IAC SE ends it, IAC IAC is a literal 255.</summary>
public struct EndSubNegotiation : IState<SubNegotiation>
{
}
