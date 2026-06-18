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
    private const byte Dont = 254;
    private const byte Do = 253;
    private const byte Wont = 252;
    private const byte Will = 251;
    private const byte Sb = 250;
    private const byte Nop = 241;
    private const byte Se = 240;
    private const byte TelnetBinary = 0;
    private const byte SuppressGoAhead = 3;
    private const byte ComPortOption = 44;

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
    private const byte LineStateErrorMask = 0x1E;
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
    private const uint SerialErrorBreak = 0x00000001;
    private const uint SerialErrorFraming = 0x00000002;
    private const uint SerialErrorOverrun = 0x00000004;
    private const uint SerialErrorParity = 0x00000010;

    private ParserState _state;
    private SubnegotiationState _subState;
    private byte _command;
    private readonly List<byte> _subnegotiation = [];

    public byte[] BuildInitialNegotiation()
    {
        return
        [
            Iac, Will, ComPortOption,
            Iac, Do, ComPortOption,
            Iac, Will, TelnetBinary,
            Iac, Do, TelnetBinary,
            Iac, Will, SuppressGoAhead,
            Iac, Do, SuppressGoAhead,
            .. BuildSetLineStateMask(LineStateErrorMask),
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
            new(AckSetLineStateMask, [LineStateErrorMask], AllowPayloadBitSubset: true),
            new(AckSetModemStateMask, [ModemStateMask], AllowPayloadBitSubset: true)
        ];
    }

    public Rfc2217Frame ProcessNetworkBytes(byte[] buffer, int length)
    {
        var serial = new List<byte>(length);
        var replies = new List<byte>();
        var events = new List<Rfc2217Notification>();

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

        return new Rfc2217Frame(serial.ToArray(), replies.ToArray(), events.ToArray());
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

    public static bool IsCompatibleSetControlAcceptedValue(byte requested, byte accepted)
    {
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

    public static byte MapPurge(uint purgeMask)
    {
        return (purgeMask & (SerialPurgeRxClear | SerialPurgeTxClear)) switch
        {
            SerialPurgeRxClear => (byte)1,
            SerialPurgeTxClear => (byte)2,
            SerialPurgeRxClear | SerialPurgeTxClear => (byte)3,
            _ => (byte)0
        };
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

    private static void AddNegotiationReply(List<byte> replies, byte command, byte option)
    {
        var accept = option is TelnetBinary or SuppressGoAhead or ComPortOption;
        var reply = command switch
        {
            Do => accept ? Will : Wont,
            Dont => Wont,
            Will => accept ? Do : Dont,
            Wont => Dont,
            _ => (byte)0
        };

        if (reply != 0)
        {
            replies.Add(Iac);
            replies.Add(reply);
            replies.Add(option);
        }
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

public sealed record Rfc2217Frame(byte[] SerialData, byte[] Replies, IReadOnlyList<Rfc2217Notification> Notifications);

public sealed record Rfc2217Notification(byte Command, byte[] Payload);

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
