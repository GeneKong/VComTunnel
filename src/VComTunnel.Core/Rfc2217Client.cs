namespace VComTunnel.Core;

public sealed class Rfc2217Client
{
    public const byte NotifyLineState = 106;
    public const byte NotifyModemState = 107;

    private const byte Iac = 255;
    private const byte Dont = 254;
    private const byte Do = 253;
    private const byte Wont = 252;
    private const byte Will = 251;
    private const byte Sb = 250;
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

    private const uint SerialDtrHandshake = 0x02;
    private const uint SerialCtsHandshake = 0x08;
    private const uint SerialDsrHandshake = 0x10;
    private const uint SerialDcdHandshake = 0x20;
    private const uint SerialAutoTransmit = 0x01;
    private const uint SerialAutoReceive = 0x02;
    private const uint SerialPurgeTxClear = 0x04;
    private const uint SerialPurgeRxClear = 0x08;

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
            .. BuildSetLineStateMask(0),
            .. BuildSetModemStateMask(255)
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
                            ParseSubnegotiation(_subnegotiation, events);
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
            BuildSetParity(MapParity(parity)),
            BuildSetStopSize(MapStopBits(stopBits)));
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
        var outbound = (controlHandshake & SerialDcdHandshake) != 0
            ? (byte)17
            : (controlHandshake & SerialDsrHandshake) != 0
                ? (byte)19
                : (controlHandshake & SerialCtsHandshake) != 0
                    ? (byte)3
                    : (flowReplace & SerialAutoTransmit) != 0
                        ? (byte)2
                        : (byte)1;
        var inbound = (controlHandshake & SerialDtrHandshake) != 0
            ? (byte)18
            : (flowReplace & SerialAutoReceive) != 0
                ? (byte)15
                : (byte)14;

        return Combine(BuildSetControl(outbound), BuildSetControl(inbound));
    }

    public static byte[] BuildPurge(uint purgeMask)
    {
        var purge = (purgeMask & (SerialPurgeRxClear | SerialPurgeTxClear)) switch
        {
            SerialPurgeRxClear => (byte)1,
            SerialPurgeTxClear => (byte)2,
            SerialPurgeRxClear | SerialPurgeTxClear => (byte)3,
            _ => (byte)0
        };

        return purge == 0 ? [] : BuildSubnegotiation(PurgeData, purge);
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

    private static byte MapParity(byte windowsParity)
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

    private static byte MapStopBits(byte windowsStopBits)
    {
        return windowsStopBits switch
        {
            0 => 1,
            1 => 3,
            2 => 2,
            _ => 1
        };
    }

    private static void AddNegotiationReply(List<byte> replies, byte command, byte option)
    {
        var accept = option is TelnetBinary or SuppressGoAhead or ComPortOption;
        var reply = command switch
        {
            Do => accept ? Will : Wont,
            Will => accept ? Do : Dont,
            _ => (byte)0
        };

        if (reply != 0)
        {
            replies.Add(Iac);
            replies.Add(reply);
            replies.Add(option);
        }
    }

    private static void ParseSubnegotiation(List<byte> data, List<Rfc2217Notification> events)
    {
        if (data.Count < 2 || data[0] != ComPortOption)
        {
            return;
        }

        var command = data[1];
        var payload = data.Skip(2).ToArray();
        events.Add(new Rfc2217Notification(command, payload));
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
