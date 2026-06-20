using System.Text;

namespace VComTunnel.Core;

public sealed class Rfc2217Client
{
    public static readonly TimeSpan RecommendedCommandAckTimeout = TimeSpan.FromSeconds(2);

    public const byte Signature = 0;
    public const byte AckSetBaudRate = 101;
    public const byte AckSetDataSize = 102;
    public const byte AckSetParity = 103;
    public const byte AckSetStopSize = 104;
    public const byte AckSetControl = 105;
    public const byte NotifyLineState = 106;
    public const byte NotifyModemState = 107;
    public const byte FlowControlSuspend = 108;
    public const byte FlowControlResume = 109;
    public const byte AckSetLineStateMask = 110;
    public const byte AckSetModemStateMask = 111;
    public const byte AckPurgeData = 112;

    private const byte Iac = 255;
    public const byte TelnetCommandDont = 254;
    public const byte TelnetCommandDo = 253;
    public const byte TelnetCommandWont = 252;
    public const byte TelnetCommandWill = 251;
    public const byte TelnetOptionBinary = 0;
    public const byte TelnetOptionEcho = 1;
    public const byte TelnetOptionSuppressGoAhead = 3;
    public const byte TelnetOptionComPortControl = 44;

    private const byte Dont = TelnetCommandDont;
    private const byte Do = TelnetCommandDo;
    private const byte Wont = TelnetCommandWont;
    private const byte Will = TelnetCommandWill;
    private const byte Sb = 250;
    private const byte Nop = 241;
    private const byte Se = 240;
    private const byte TelnetBinary = TelnetOptionBinary;
    private const byte Echo = TelnetOptionEcho;
    private const byte SuppressGoAhead = TelnetOptionSuppressGoAhead;
    private const byte ComPortOption = TelnetOptionComPortControl;

    private const byte SetBaudRate = 1;
    private const byte SetDataSize = 2;
    private const byte SetParity = 3;
    private const byte SetStopSize = 4;
    private const byte SetControl = 5;
    private const byte SetLineStateMask = 10;
    private const byte SetModemStateMask = 11;
    private const byte PurgeData = 12;
    private const byte LocalFlowControlSuspend = 8;
    private const byte LocalFlowControlResume = 9;
    private const byte LineStateMask = 0xFF;
    private const byte ModemStateMask = 0xFF;
    private const string ClientSignature = "VComTunnel";

    private const uint SerialDtrHandshake = 0x02;
    private const uint SerialCtsHandshake = 0x08;
    private const uint SerialDsrHandshake = 0x10;
    private const uint SerialDcdHandshake = 0x20;
    private const uint SerialAutoTransmit = 0x01;
    private const uint SerialAutoReceive = 0x02;
    private const uint SerialRtsHandshake = 0x80;
    private const uint SerialPurgeTxClear = 0x04;
    private const uint SerialPurgeRxClear = 0x08;
    private const uint SerialCtsState = 0x00000010;
    private const uint SerialDsrState = 0x00000020;
    private const uint SerialRiState = 0x00000040;
    private const uint SerialDcdState = 0x00000080;
    private const uint SerialEvCts = 0x00000008;
    private const uint SerialEvDsr = 0x00000010;
    private const uint SerialEvRlsd = 0x00000020;
    private const uint SerialEvRing = 0x00000100;
    private const uint SerialEvRxChar = 0x00000001;
    private const uint SerialEvTxEmpty = 0x00000004;
    private const uint SerialEvBreak = 0x00000040;
    private const uint SerialEvErr = 0x00000080;
    private const uint SerialErrorBreak = 0x00000001;
    private const uint SerialErrorFraming = 0x00000002;
    private const uint SerialErrorOverrun = 0x00000004;
    private const uint SerialErrorParity = 0x00000010;

    private ParserState _state;
    private SubnegotiationState _subState;
    private byte _command;
    private readonly List<byte> _subnegotiation = [];
    private readonly HashSet<byte> _usEnabledOptions = [];
    private readonly HashSet<byte> _peerEnabledOptions = [];

    public byte[] BuildInitialNegotiation()
    {
        MarkInitialNegotiationRequested();
        return
        [
            Iac, Will, ComPortOption,
            Iac, Do, ComPortOption,
            Iac, Will, TelnetBinary,
            Iac, Do, TelnetBinary,
            Iac, Will, SuppressGoAhead,
            Iac, Do, SuppressGoAhead,
            .. BuildSetLineStateMask(LineStateMask),
            .. BuildSetModemStateMask(ModemStateMask)
        ];
    }

    public static byte[] BuildTelnetNop()
    {
        return [Iac, Nop];
    }

    public static byte[] BuildLocalFlowControlSuspend()
    {
        return BuildSubnegotiation(LocalFlowControlSuspend);
    }

    public static byte[] BuildLocalFlowControlResume()
    {
        return BuildSubnegotiation(LocalFlowControlResume);
    }

    public static byte[] BuildSignature(string signature)
    {
        return BuildSubnegotiation(Signature, Encoding.ASCII.GetBytes(signature));
    }

    public static Rfc2217ExpectedAck[] BuildInitialExpectedAcks()
    {
        return
        [
            new(AckSetLineStateMask, [LineStateMask], AllowPayloadBitSubset: true),
            new(AckSetModemStateMask, [ModemStateMask], AllowPayloadBitSubset: true)
        ];
    }

    public Rfc2217Frame ProcessNetworkBytes(byte[] buffer, int length)
    {
        var serial = new List<byte>(length);
        var replies = new List<byte>();
        var events = new List<Rfc2217Notification>();
        var options = new List<Rfc2217TelnetOptionEvent>();

        for (var i = 0; i < length; i++)
        {
            var value = buffer[i];
            switch (_state)
            {
                case ParserState.Data:
                    if (value == Iac)
                    {
                        _state = ParserState.Iac;
                    }
                    else
                    {
                        serial.Add(value);
                    }
                    break;

                case ParserState.Iac:
                    if (value == Iac)
                    {
                        serial.Add(Iac);
                        _state = ParserState.Data;
                    }
                    else if (value is Do or Dont or Will or Wont)
                    {
                        _command = value;
                        _state = ParserState.Option;
                    }
                    else if (value == Sb)
                    {
                        _subnegotiation.Clear();
                        _subState = SubnegotiationState.Data;
                        _state = ParserState.Subnegotiation;
                    }
                    else
                    {
                        _state = ParserState.Data;
                    }
                    break;

                case ParserState.Option:
                    AddNegotiationReply(replies, _command, value);
                    options.Add(BuildTelnetOptionEvent(_command, value));
                    _state = ParserState.Data;
                    break;

                case ParserState.Subnegotiation:
                    if (_subState == SubnegotiationState.Iac)
                    {
                        if (value == Iac)
                        {
                            _subnegotiation.Add(Iac);
                            _subState = SubnegotiationState.Data;
                        }
                        else if (value == Se)
                        {
                            ParseSubnegotiation(_subnegotiation, replies, events);
                            _state = ParserState.Data;
                            _subState = SubnegotiationState.Data;
                        }
                        else
                        {
                            _subState = SubnegotiationState.Data;
                        }
                    }
                    else if (value == Iac)
                    {
                        _subState = SubnegotiationState.Iac;
                    }
                    else
                    {
                        _subnegotiation.Add(value);
                    }
                    break;
            }
        }

        return new Rfc2217Frame(serial.ToArray(), replies.ToArray(), events.ToArray(), options.ToArray());
    }

    public static byte[] EscapeSerialData(byte[] buffer, int offset, int length)
    {
        var escaped = new List<byte>(length);
        for (var i = 0; i < length; i++)
        {
            var value = buffer[offset + i];
            escaped.Add(value);
            if (value == Iac)
            {
                escaped.Add(Iac);
            }
        }

        return escaped.ToArray();
    }

    public static bool RequiresSerialDataEscaping(byte[] buffer, int offset, int length)
    {
        for (var i = 0; i < length; i++)
        {
            if (buffer[offset + i] == Iac)
            {
                return true;
            }
        }

        return false;
    }

    public static byte[] BuildSetBaudRate(uint baudRate)
    {
        return BuildSubnegotiation(SetBaudRate,
            (byte)(baudRate >> 24),
            (byte)(baudRate >> 16),
            (byte)(baudRate >> 8),
            (byte)baudRate);
    }

    public static byte[] BuildSetLineControl(byte stopBits, byte parity, byte wordLength)
    {
        return Combine(
            BuildSetDataSize(wordLength),
            BuildSetParity(MapWindowsParityToRfc2217(parity)),
            BuildSetStopSize(MapWindowsStopBitsToRfc2217(stopBits)));
    }

    public static byte[] BuildQuerySerialSettings()
    {
        return Combine(
            BuildSetBaudRate(0),
            BuildSetDataSize(0),
            BuildSetParity(0),
            BuildSetStopSize(0));
    }

    public static byte[] BuildQueryControlState()
    {
        return Combine(
            BuildSetControl(0),
            BuildSetControl(4),
            BuildSetControl(7),
            BuildSetControl(10),
            BuildSetControl(13));
    }

    public static byte[] BuildStartupStatusQuery()
    {
        return Combine(
            BuildQuerySerialSettings(),
            BuildQueryControlState());
    }

    public static byte[] BuildSetModemControl(bool? dtr, bool? rts)
    {
        var frames = new List<byte[]>();
        if (dtr is not null)
        {
            frames.Add(BuildSetControl(dtr.Value ? (byte)8 : (byte)9));
        }

        if (rts is not null)
        {
            frames.Add(BuildSetControl(rts.Value ? (byte)11 : (byte)12));
        }

        return Combine(frames.ToArray());
    }

    public static byte[] BuildSetBreak(bool enabled)
    {
        return BuildSetControl(enabled ? (byte)5 : (byte)6);
    }

    public static byte[] BuildSetHandflow(uint controlHandshake, uint flowReplace)
    {
        return Combine(
            BuildSetControl(MapOutboundFlowControl(controlHandshake, flowReplace)),
            BuildSetControl(MapInboundFlowControl(controlHandshake, flowReplace)));
    }

    public static byte[] BuildPurge(uint purgeMask)
    {
        var purge = MapPurge(purgeMask);

        return purge == 0 ? [] : BuildSubnegotiation(PurgeData, purge);
    }

    public static bool IsCommandAck(byte command)
    {
        return command is AckSetBaudRate
            or AckSetDataSize
            or AckSetParity
            or AckSetStopSize
            or AckSetControl
            or AckSetLineStateMask
            or AckSetModemStateMask
            or AckPurgeData;
    }

    public static bool IsAcceptedSerialSettingAck(byte command)
    {
        return command is AckSetBaudRate
            or AckSetDataSize
            or AckSetParity
            or AckSetStopSize;
    }

    public static bool IsAcceptedSerialSetting(Rfc2217Notification notification)
    {
        if (!IsAcceptedSerialSettingAck(notification.Command))
        {
            return false;
        }

        return notification.Command == AckSetBaudRate
            ? notification.Payload.Length == 4 && ReadUInt32Payload(notification.Payload) != 0
            : notification.Payload.Length == 1 && notification.Payload[0] != 0;
    }

    public static bool IsFlowControlCommand(byte command)
    {
        return command is FlowControlSuspend or FlowControlResume;
    }

    public static bool IsSupportedTelnetOption(byte option)
    {
        return CanEnableLocalTelnetOption(option) || CanEnablePeerTelnetOption(option);
    }

    public static bool IsCompatibleSetControlAcceptedValue(byte requested, byte accepted)
    {
        if (IsSetControlQueryValue(requested))
        {
            return IsCompatibleSetControlQueryResponse(requested, accepted);
        }

        return IsOutboundFlowControlValue(requested) && IsOutboundFlowControlValue(accepted)
            || IsInboundFlowControlValue(requested) && IsInboundFlowControlValue(accepted);
    }

    public static byte MapWindowsParityToRfc2217(byte windowsParity)
    {
        return windowsParity switch
        {
            0 => 1,
            1 => 2,
            2 => 3,
            3 => 4,
            4 => 5,
            _ => 1
        };
    }

    public static byte MapRfc2217ParityToWindows(byte rfc2217Parity)
    {
        return rfc2217Parity switch
        {
            1 => 0,
            2 => 1,
            3 => 2,
            4 => 3,
            5 => 4,
            _ => 0
        };
    }

    public static byte MapWindowsStopBitsToRfc2217(byte windowsStopBits)
    {
        return windowsStopBits switch
        {
            0 => 1,
            1 => 3,
            2 => 2,
            _ => 1
        };
    }

    public static byte MapRfc2217StopBitsToWindows(byte rfc2217StopBits)
    {
        return rfc2217StopBits switch
        {
            1 => 0,
            2 => 2,
            3 => 1,
            _ => 0
        };
    }

    public static byte[] BuildUInt32Payload(uint value)
    {
        return
        [
            (byte)(value >> 24),
            (byte)(value >> 16),
            (byte)(value >> 8),
            (byte)value
        ];
    }

    public static uint ReadUInt32Payload(byte[] value)
    {
        if (value.Length < 4)
        {
            return 0;
        }

        return ((uint)value[0] << 24)
            | ((uint)value[1] << 16)
            | ((uint)value[2] << 8)
            | value[3];
    }

    public static byte MapOutboundFlowControl(uint controlHandshake, uint flowReplace)
    {
        return (controlHandshake & SerialDcdHandshake) != 0
            ? (byte)17
            : (controlHandshake & SerialDsrHandshake) != 0
                ? (byte)19
                : (controlHandshake & SerialCtsHandshake) != 0
                    ? (byte)3
                    : (flowReplace & SerialAutoTransmit) != 0
                        ? (byte)2
                        : (byte)1;
    }

    public static byte MapInboundFlowControl(uint controlHandshake, uint flowReplace)
    {
        if ((controlHandshake & SerialDtrHandshake) != 0)
        {
            return 18;
        }

        if ((flowReplace & SerialRtsHandshake) != 0)
        {
            return 16;
        }

        return (flowReplace & SerialAutoReceive) != 0 ? (byte)15 : (byte)14;
    }

    public static bool IsOutboundFlowControlValue(byte value)
    {
        return value is 1 or 2 or 3 or 17 or 19;
    }

    public static bool IsInboundFlowControlValue(byte value)
    {
        return value is 14 or 15 or 16 or 18;
    }

    public static bool IsSetControlQueryValue(byte value)
    {
        return value is 0 or 4 or 7 or 10 or 13;
    }

    public static bool IsCompatibleSetControlQueryResponse(byte query, byte response)
    {
        return query switch
        {
            0 => IsOutboundFlowControlValue(response),
            4 => response is 5 or 6,
            7 => response is 8 or 9,
            10 => response is 11 or 12,
            13 => IsInboundFlowControlValue(response),
            _ => false
        };
    }

    public static string DescribeSetControlValue(byte value)
    {
        return value switch
        {
            0 => "outbound-flow query",
            1 => "outbound-flow none",
            2 => "outbound-flow xon-xoff",
            3 => "outbound-flow cts",
            4 => "break query",
            5 => "break on",
            6 => "break off",
            7 => "dtr query",
            8 => "dtr on",
            9 => "dtr off",
            10 => "rts query",
            11 => "rts on",
            12 => "rts off",
            13 => "inbound-flow query",
            14 => "inbound-flow none",
            15 => "inbound-flow xon-xoff",
            16 => "inbound-flow rts",
            17 => "outbound-flow dcd",
            18 => "inbound-flow dtr",
            19 => "outbound-flow dsr",
            _ => $"unknown {value}"
        };
    }

    public static byte MapPurge(uint purgeMask)
    {
        var purge = 0;
        if ((purgeMask & SerialPurgeRxClear) != 0) purge |= 1;
        if ((purgeMask & SerialPurgeTxClear) != 0) purge |= 2;
        return (byte)purge;
    }

    public static uint MapNotifyModemStateToWindowsStatus(byte value)
    {
        uint result = 0;
        if ((value & 0x10) != 0) result |= SerialCtsState;
        if ((value & 0x20) != 0) result |= SerialDsrState;
        if ((value & 0x40) != 0) result |= SerialRiState;
        if ((value & 0x80) != 0) result |= SerialDcdState;
        return result;
    }

    public static uint MapNotifyModemStateToWindowsEvents(byte value)
    {
        uint result = 0;
        if ((value & 0x01) != 0) result |= SerialEvCts;
        if ((value & 0x02) != 0) result |= SerialEvDsr;
        if ((value & 0x04) != 0) result |= SerialEvRing;
        if ((value & 0x08) != 0) result |= SerialEvRlsd;
        return result;
    }

    public static uint MapNotifyLineStateToWindowsErrors(byte value)
    {
        uint result = 0;
        if ((value & 0x02) != 0) result |= SerialErrorOverrun;
        if ((value & 0x04) != 0) result |= SerialErrorParity;
        if ((value & 0x08) != 0) result |= SerialErrorFraming;
        if ((value & 0x10) != 0) result |= SerialErrorBreak;
        return result;
    }

    public static uint MapNotifyLineStateToWindowsEvents(byte value)
    {
        uint result = 0;
        if ((value & 0x01) != 0) result |= SerialEvRxChar;
        if ((value & 0x60) != 0) result |= SerialEvTxEmpty;
        if ((value & 0x9E) != 0) result |= SerialEvErr;
        if ((value & 0x10) != 0) result |= SerialEvBreak;
        return result;
    }

    private static byte[] BuildSetDataSize(byte value) => BuildSubnegotiation(SetDataSize, value);
    private static byte[] BuildSetParity(byte value) => BuildSubnegotiation(SetParity, value);
    private static byte[] BuildSetStopSize(byte value) => BuildSubnegotiation(SetStopSize, value);
    private static byte[] BuildSetControl(byte value) => BuildSubnegotiation(SetControl, value);
    private static byte[] BuildSetLineStateMask(byte value) => BuildSubnegotiation(SetLineStateMask, value);
    private static byte[] BuildSetModemStateMask(byte value) => BuildSubnegotiation(SetModemStateMask, value);

    private static byte[] BuildSubnegotiation(byte command, params byte[] payload)
    {
        var frame = new List<byte>(payload.Length + 5)
        {
            Iac,
            Sb,
            ComPortOption,
            command
        };

        foreach (var value in payload)
        {
            frame.Add(value);
            if (value == Iac)
            {
                frame.Add(Iac);
            }
        }

        frame.Add(Iac);
        frame.Add(Se);
        return frame.ToArray();
    }

    private void MarkInitialNegotiationRequested()
    {
        foreach (var option in new[] { TelnetBinary, SuppressGoAhead, ComPortOption })
        {
            _usEnabledOptions.Add(option);
            _peerEnabledOptions.Add(option);
        }
    }

    private void AddNegotiationReply(List<byte> replies, byte command, byte option)
    {
        var reply = (byte)0;
        switch (command)
        {
            case Do:
                reply = CanEnableLocalTelnetOption(option)
                    ? _usEnabledOptions.Add(option) ? Will : (byte)0
                    : Wont;
                break;
            case Dont:
                reply = _usEnabledOptions.Remove(option) ? Wont : (byte)0;
                break;
            case Will:
                reply = CanEnablePeerTelnetOption(option)
                    ? _peerEnabledOptions.Add(option) ? Do : (byte)0
                    : Dont;
                break;
            case Wont:
                reply = _peerEnabledOptions.Remove(option) ? Dont : (byte)0;
                break;
        }

        if (reply != 0)
        {
            replies.Add(Iac);
            replies.Add(reply);
            replies.Add(option);
        }
    }

    private static Rfc2217TelnetOptionEvent BuildTelnetOptionEvent(byte command, byte option)
    {
        var accepted = command switch
        {
            Do => CanEnableLocalTelnetOption(option),
            Will => CanEnablePeerTelnetOption(option),
            _ => false
        };
        return new Rfc2217TelnetOptionEvent(command, option, accepted);
    }

    private static bool CanEnableLocalTelnetOption(byte option)
    {
        return option is TelnetBinary or SuppressGoAhead or ComPortOption;
    }

    private static bool CanEnablePeerTelnetOption(byte option)
    {
        return option is TelnetBinary or SuppressGoAhead or ComPortOption or Echo;
    }

    private static void ParseSubnegotiation(List<byte> data, List<byte> replies, List<Rfc2217Notification> events)
    {
        if (data.Count < 2 || data[0] != ComPortOption)
        {
            return;
        }

        var command = data[1];
        var payload = data.Skip(2).ToArray();
        if (command == Signature && payload.Length == 0)
        {
            replies.AddRange(BuildSignature(ClientSignature));
        }

        if (!IsValidKnownSubnegotiation(command, payload.Length))
        {
            return;
        }

        events.Add(new Rfc2217Notification(command, payload));
    }

    private static bool IsValidKnownSubnegotiation(byte command, int payloadLength)
    {
        return command switch
        {
            Signature => true,
            AckSetBaudRate => payloadLength == 4,
            AckSetDataSize
                or AckSetParity
                or AckSetStopSize
                or AckSetControl
                or NotifyLineState
                or NotifyModemState
                or AckSetLineStateMask
                or AckSetModemStateMask
                or AckPurgeData => payloadLength == 1,
            FlowControlSuspend or FlowControlResume => payloadLength == 0,
            _ => true
        };
    }

    private static byte[] Combine(params byte[][] frames)
    {
        var length = frames.Sum(frame => frame.Length);
        if (length == 0)
        {
            return [];
        }

        var combined = new byte[length];
        var offset = 0;
        foreach (var frame in frames)
        {
            Buffer.BlockCopy(frame, 0, combined, offset, frame.Length);
            offset += frame.Length;
        }

        return combined;
    }

    private enum ParserState
    {
        Data,
        Iac,
        Option,
        Subnegotiation
    }

    private enum SubnegotiationState
    {
        Data,
        Iac
    }
}

public sealed record Rfc2217Frame(
    byte[] SerialData,
    byte[] Replies,
    IReadOnlyList<Rfc2217Notification> Notifications,
    IReadOnlyList<Rfc2217TelnetOptionEvent> TelnetOptions);

public sealed class Rfc2217LocalFlowControlState
{
    private int _suspendDepth;

    public int SuspendDepth => _suspendDepth;

    public Rfc2217LocalFlowControlAction Apply(bool suspend)
    {
        if (suspend)
        {
            if (_suspendDepth < int.MaxValue)
            {
                _suspendDepth++;
            }

            return _suspendDepth == 1
                ? Rfc2217LocalFlowControlAction.Suspend
                : Rfc2217LocalFlowControlAction.None;
        }

        if (_suspendDepth == 0)
        {
            return Rfc2217LocalFlowControlAction.None;
        }

        _suspendDepth--;
        return _suspendDepth == 0
            ? Rfc2217LocalFlowControlAction.Resume
            : Rfc2217LocalFlowControlAction.None;
    }
}

public enum Rfc2217LocalFlowControlAction
{
    None,
    Suspend,
    Resume
}

public sealed record Rfc2217Notification(byte Command, byte[] Payload);

public sealed record Rfc2217TelnetOptionEvent(byte Command, byte Option, bool Accepted)
{
    public bool Rejected => !Accepted;

    public string Describe()
    {
        return $"{CommandName(Command)} {OptionName(Option)} {(Accepted ? "accepted" : "rejected")}";
    }

    private static string CommandName(byte command)
    {
        return command switch
        {
            Rfc2217Client.TelnetCommandDo => "DO",
            Rfc2217Client.TelnetCommandDont => "DONT",
            Rfc2217Client.TelnetCommandWill => "WILL",
            Rfc2217Client.TelnetCommandWont => "WONT",
            _ => command.ToString()
        };
    }

    private static string OptionName(byte option)
    {
        return option switch
        {
            Rfc2217Client.TelnetOptionComPortControl => "COM-PORT-OPTION",
            Rfc2217Client.TelnetOptionBinary => "BINARY",
            Rfc2217Client.TelnetOptionEcho => "ECHO",
            Rfc2217Client.TelnetOptionSuppressGoAhead => "SUPPRESS-GO-AHEAD",
            _ => $"option-{option}"
        };
    }
}

public sealed record Rfc2217ExpectedAck(
    byte Command,
    byte[] Payload,
    bool AllowPayloadBitSubset = false,
    bool AllowAcceptedValue = false)
{
    public bool Matches(Rfc2217Notification notification)
    {
        if (notification.Command != Command)
        {
            return false;
        }

        if (Payload.SequenceEqual(notification.Payload))
        {
            return true;
        }

        return AllowPayloadBitSubset &&
            notification.Payload.Length == Payload.Length &&
            Payload.Zip(notification.Payload).All(pair => (pair.Second & ~pair.First) == 0);
    }

    public bool IsSameCommand(Rfc2217Notification notification)
    {
        return notification.Command == Command;
    }

    public bool MatchesAcceptedValue(Rfc2217Notification notification)
    {
        return AllowAcceptedValue
            && notification.Command == Command
            && Rfc2217Client.IsAcceptedSerialSetting(notification);
    }

    public bool MatchesAcceptedSetControlValue(Rfc2217Notification notification)
    {
        return AllowAcceptedValue
            && Command == Rfc2217Client.AckSetControl
            && notification.Command == Command
            && Payload.Length == 1
            && notification.Payload.Length == 1
            && Rfc2217Client.IsCompatibleSetControlAcceptedValue(Payload[0], notification.Payload[0]);
    }

    public string Describe()
    {
        return $"{Command} [{ToHex(Payload)}]";
    }

    public static string Describe(Rfc2217Notification notification)
    {
        return $"{notification.Command} [{ToHex(notification.Payload)}]";
    }

    private static string ToHex(byte[] bytes)
    {
        return bytes.Length == 0 ? "-" : Convert.ToHexString(bytes);
    }
}
