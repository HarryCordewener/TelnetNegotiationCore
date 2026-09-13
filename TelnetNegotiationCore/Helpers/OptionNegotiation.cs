using System.Threading.Tasks;
using TelnetNegotiationCore.Models;
using TelnetNegotiationCore.Plugins;

namespace TelnetNegotiationCore.Helpers;

/// <summary>
/// The answer a telnet option negotiation is owed, in one place.
/// </summary>
/// <remarks>
/// <para>
/// RFC 1143: "A TELNET implementation MUST refuse (DONT/WONT) a request to enable an option for
/// which it does not comply with the appropriate protocol specification." Silence is not one of the
/// choices — a peer that asked is entitled to an answer, and without one it waits.
/// </para>
/// <para>
/// Which verb answers which is RFC 854's pairing, and it is asymmetric: a <c>DO</c> asks this side to
/// enable the option, so it is answered <c>WILL</c> or <c>WONT</c>; a <c>WILL</c> announces that the
/// peer is enabling it, so it is answered <c>DO</c> or <c>DONT</c>. A <c>DONT</c> or <c>WONT</c> is
/// itself a refusal and gets no answer — refusing a refusal is not an exchange, and RFC 1143 warns
/// that answering one invites a loop.
/// </para>
/// <para>
/// This exists because that pairing was written out by hand in every place that needed it — the
/// interpreter's own unclaimed-option refusal and each protocol that gates a wrong-direction verb —
/// and a rule restated in six places is a rule with six chances to be wrong.
/// </para>
/// <para>
/// Note what this deliberately does <em>not</em> decide: whether an answer is owed at all. A side
/// that announced <c>WILL</c> and then receives <c>DO</c> is being agreed with, not asked, and
/// RFC 1143 has it ignore that rather than answer and loop. Only the caller knows which of those it
/// is looking at.
/// </para>
/// </remarks>
internal static class OptionNegotiation
{
    /// <summary>
    /// The frame answering <paramref name="verb"/> for <paramref name="option"/>, or null when no
    /// answer is owed.
    /// </summary>
    /// <param name="honour">Whether this side will enable, or accept the peer enabling, the option.</param>
    /// <param name="verb">The verb received: <c>DO</c>, <c>DONT</c>, <c>WILL</c> or <c>WONT</c>.</param>
    /// <param name="option">The option the verb was about.</param>
    /// <returns>
    /// <c>IAC WILL/WONT option</c> for a <c>DO</c>, <c>IAC DO/DONT option</c> for a <c>WILL</c>, and
    /// null for a <c>DONT</c>, a <c>WONT</c>, or anything else.
    /// </returns>
    public static byte[]? AnswerFor(bool honour, byte verb, byte option) => verb switch
    {
        (byte)Trigger.DO =>
            [(byte)Trigger.IAC, honour ? (byte)Trigger.WILL : (byte)Trigger.WONT, option],

        (byte)Trigger.WILL =>
            [(byte)Trigger.IAC, honour ? (byte)Trigger.DO : (byte)Trigger.DONT, option],

        _ => null,
    };

    /// <summary>
    /// Sends the answer <paramref name="verb"/> is owed, if any.
    /// </summary>
    /// <param name="honour">Whether this side will enable, or accept the peer enabling, the option.</param>
    /// <param name="verb">The verb received.</param>
    /// <param name="option">The option the verb was about.</param>
    /// <param name="context">The protocol's context, which carries the negotiation sink.</param>
    public static ValueTask AnswerAsync(bool honour, byte verb, byte option, IProtocolContext context)
    {
        var answer = AnswerFor(honour, verb, option);

        return answer is null ? default : context.SendNegotiationAsync(answer);
    }
}
