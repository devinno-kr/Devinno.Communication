namespace Devinno.Communication.Secs.Gem;

/// <summary>
/// Known values of the GEM acknowledge codes (ack). An ack is an open <see cref="byte"/> value, so values not listed here are exchanged as they are.
/// </summary>
/// <remarks>
/// The SEMI E5 original could not be read, so only values confirmed from secsgem and equipment vendors' GEM manuals are included. HCACK 5 and 6 exist only in secsgem.
/// The STRACK values (S2F44) come from a single public source (secsgem) and could not be checked against the SEMI E30 original.
/// Example: in a remote command handler, <c>e.Hcack = GemAck.HcackAcceptedLater;</c>
/// </remarks>
public static class GemAck
{
    /// <summary>COMMACK 0: communication establishment accepted (S1F14)</summary>
    public const byte CommackAccepted = 0;
    /// <summary>COMMACK 1: communication establishment denied, try again (S1F14)</summary>
    public const byte CommackDenied = 1;

    /// <summary>OFLACK 0: OFF-LINE request acknowledged (S1F16)</summary>
    public const byte OflackAccepted = 0;

    /// <summary>ONLACK 0: ON-LINE request acknowledged (S1F18)</summary>
    public const byte OnlackAccepted = 0;
    /// <summary>ONLACK 1: ON-LINE not allowed (S1F18)</summary>
    public const byte OnlackNotAllowed = 1;
    /// <summary>ONLACK 2: already ON-LINE (S1F18)</summary>
    public const byte OnlackAlreadyOnline = 2;

    /// <summary>EAC 0: EC change accepted (S2F16)</summary>
    public const byte EacAccepted = 0;
    /// <summary>EAC 1: unknown ECID (S2F16)</summary>
    public const byte EacUnknownConstant = 1;
    /// <summary>EAC 2: cannot be changed now (busy) (S2F16)</summary>
    public const byte EacBusy = 2;
    /// <summary>EAC 3: value is out of range or has the wrong format (S2F16)</summary>
    public const byte EacOutOfRange = 3;

    /// <summary>TIACK 0: time setting accepted (S2F32)</summary>
    public const byte TiackAccepted = 0;
    /// <summary>TIACK 1: time setting failed (S2F32)</summary>
    public const byte TiackError = 1;

    /// <summary>DRACK 0: report definition accepted (S2F34)</summary>
    public const byte DrackAccepted = 0;
    /// <summary>DRACK 1: no space (S2F34)</summary>
    public const byte DrackNoSpace = 1;
    /// <summary>DRACK 2: format error (S2F34)</summary>
    public const byte DrackInvalidFormat = 2;
    /// <summary>DRACK 3: RPTID already defined (S2F34)</summary>
    public const byte DrackAlreadyDefined = 3;
    /// <summary>DRACK 4: unknown VID (S2F34)</summary>
    public const byte DrackUnknownVariable = 4;

    /// <summary>LRACK 0: link accepted (S2F36)</summary>
    public const byte LrackAccepted = 0;
    /// <summary>LRACK 1: no space (S2F36)</summary>
    public const byte LrackNoSpace = 1;
    /// <summary>LRACK 2: format error (S2F36)</summary>
    public const byte LrackInvalidFormat = 2;
    /// <summary>LRACK 3: the CEID already has a link (S2F36)</summary>
    public const byte LrackAlreadyLinked = 3;
    /// <summary>LRACK 4: unknown CEID (S2F36)</summary>
    public const byte LrackUnknownEvent = 4;
    /// <summary>LRACK 5: unknown RPTID (S2F36)</summary>
    public const byte LrackUnknownReport = 5;

    /// <summary>ERACK 0: enable/disable change accepted (S2F38)</summary>
    public const byte ErackAccepted = 0;
    /// <summary>ERACK 1: denied (unknown CEID) (S2F38)</summary>
    public const byte ErackUnknownEvent = 1;

    /// <summary>HCACK 0: command accepted and executed (S2F42)</summary>
    public const byte HcackAccepted = 0;
    /// <summary>HCACK 1: unknown command (S2F42)</summary>
    public const byte HcackInvalidCommand = 1;
    /// <summary>HCACK 2: cannot be performed now (S2F42)</summary>
    public const byte HcackCannotPerform = 2;
    /// <summary>HCACK 3: parameter error (S2F42)</summary>
    public const byte HcackParameterInvalid = 3;
    /// <summary>HCACK 4: accepted, will finish later (completion is reported by an event) (S2F42)</summary>
    public const byte HcackAcceptedLater = 4;
    /// <summary>HCACK 5: already in that state (S2F42, a value found only in secsgem)</summary>
    public const byte HcackAlreadyInCondition = 5;
    /// <summary>HCACK 6: no such target (S2F42, a value found only in secsgem)</summary>
    public const byte HcackNoSuchObject = 6;

    /// <summary>CPACK 1: unknown parameter name (S2F42)</summary>
    public const byte CpackUnknownName = 1;
    /// <summary>CPACK 2: invalid value (S2F42)</summary>
    public const byte CpackIllegalValue = 2;
    /// <summary>CPACK 3: invalid format (S2F42)</summary>
    public const byte CpackIllegalFormat = 3;

    /// <summary>ACKC5 0: accepted (S5F2, S5F4)</summary>
    public const byte Ackc5Accepted = 0;
    /// <summary>ACKC5 1: error (for example, unknown ALID) (S5F4)</summary>
    public const byte Ackc5Error = 1;

    /// <summary>ACKC6 0: accepted (S6F12)</summary>
    public const byte Ackc6Accepted = 0;

    /// <summary>ACKC10 0: accepted, displayed (S10F2, S10F4, S10F6)</summary>
    public const byte Ackc10Accepted = 0;
    /// <summary>ACKC10 1: not displayed (S10F2, S10F4, S10F6)</summary>
    public const byte Ackc10NotDisplayed = 1;
    /// <summary>ACKC10 2: no terminal (S10F2, S10F4, S10F6)</summary>
    public const byte Ackc10TerminalNotAvailable = 2;

    /// <summary>GRANT 0: granted (S2F40, S6F6)</summary>
    public const byte GrantGranted = 0;

    /// <summary>Bit 8 (0x80) of ALCD: alarm occurred (SET). The lower 7 bits are the alarm category</summary>
    public const byte AlcdSet = 0x80;
    /// <summary>Bit 8 (0x80) of ALED: alarm reporting enabled (S5F3)</summary>
    public const byte AledEnable = 0x80;

    /// <summary>RSPACK 0: spooling reset accepted (S2F44)</summary>
    public const byte RspackAccepted = 0;
    /// <summary>RSPACK 1: spooling reset rejected, see the per-stream STRACK (S2F44)</summary>
    public const byte RspackRejected = 1;

    /// <summary>STRACK 1: spooling not allowed for the stream (S2F44)</summary>
    public const byte StrackNotAllowed = 1;
    /// <summary>STRACK 2: unknown stream (S2F44)</summary>
    public const byte StrackUnknownStream = 2;
    /// <summary>STRACK 3: unknown function (S2F44)</summary>
    public const byte StrackUnknownFunction = 3;
    /// <summary>STRACK 4: secondary (even) function, which is never spooled (S2F44)</summary>
    public const byte StrackSecondaryFunction = 4;

    /// <summary>RSDC 0: transmit the spooled messages (S6F23)</summary>
    public const byte RsdcTransmit = 0;
    /// <summary>RSDC 1: purge the spooled messages (S6F23)</summary>
    public const byte RsdcPurge = 1;

    /// <summary>RSDA 0: request accepted (S6F24)</summary>
    public const byte RsdaAccepted = 0;
    /// <summary>RSDA 1: busy, try again (S6F24)</summary>
    public const byte RsdaBusy = 1;
    /// <summary>RSDA 2: no spooled data (S6F24)</summary>
    public const byte RsdaNoData = 2;
}
