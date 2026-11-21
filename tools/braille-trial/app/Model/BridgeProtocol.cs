using System.Globalization;

namespace JJBrailleTrial.Model;

/// <summary>
/// The three posted messages of design section 4.4, and how their two numbers
/// are packed. Registered by name with RegisterWindowMessage on both sides;
/// posted, never sent. Numbers only, and packed with multiplication rather than
/// shifts so the JAWS script language, which has no shift operator, can build
/// them the same way.
///
/// Hello    wParam = reader * 65536 + protocol version
///          lParam = display rows * 65536 + display columns
/// Route    wParam = layout number the reader used
///          lParam = field ordinal * 65536 + character code, where the
///                   character code is 0 for "not known" and otherwise the
///                   0-based character within the field plus one
/// Command  wParam = command (1 = switch view)
///          lParam = reader
///
/// Only the low 32 bits of each parameter are read; JAWS passes 32-bit ints.
/// </summary>
public static class BridgeProtocol
{
    public const string HelloMessageName = "JJFlexBraille_Hello";
    public const string RouteMessageName = "JJFlexBraille_Route";
    public const string CommandMessageName = "JJFlexBraille_Command";

    public const int ProtocolVersion = 1;

    public const int ReaderUnknown = 0;
    public const int ReaderNvda = 1;
    public const int ReaderJaws = 2;

    public const int CommandSwitchView = 1;

    public static string ReaderName(int reader) => reader switch
    {
        ReaderNvda => "NVDA",
        ReaderJaws => "JAWS",
        _ => "reader " + reader.ToString(CultureInfo.InvariantCulture),
    };

    public static HelloMessage DecodeHello(ulong wParam, ulong lParam)
    {
        uint w = (uint)wParam;
        uint l = (uint)lParam;
        return new HelloMessage((int)(w >> 16), (int)(w & 0xFFFF), (int)(l >> 16), (int)(l & 0xFFFF));
    }

    public static RouteMessage DecodeRoute(ulong wParam, ulong lParam)
    {
        uint w = (uint)wParam;
        uint l = (uint)lParam;
        int code = (int)(l & 0xFFFF);
        return new RouteMessage((int)w, (int)(l >> 16), code == 0 ? null : code - 1);
    }

    public static CommandMessage DecodeCommand(ulong wParam, ulong lParam)
        => new((int)(uint)wParam, (int)(uint)lParam);

    public static (uint WParam, uint LParam) EncodeHello(int reader, int version, int rows, int columns)
        => ((uint)(reader * 65536 + version), (uint)(rows * 65536 + columns));

    public static (uint WParam, uint LParam) EncodeRoute(int layout, int ordinal, int? charPos)
        => ((uint)layout, (uint)(ordinal * 65536 + (charPos is int c ? c + 1 : 0)));

    /// <summary>
    /// Decide whether a route may be acted on, design section 3.5's safety
    /// rules that can be decided without a window: the layout number must be
    /// the current one and the ordinal must name a field. A character outside
    /// the field's text is treated as "not known" and says so.
    /// </summary>
    public static RouteDecision DecideRoute(RouteMessage route, TrialLine line)
    {
        var inv = CultureInfo.InvariantCulture;
        if (route.Layout != line.Layout)
        {
            return new RouteDecision(false,
                "stale layout " + route.Layout.ToString(inv) + ", current is " + line.Layout.ToString(inv),
                route.Ordinal, route.CharPos, null);
        }
        if (!TrialLine.IsKnownOrdinal(route.Ordinal))
        {
            return new RouteDecision(false, "no field with ordinal " + route.Ordinal.ToString(inv),
                route.Ordinal, route.CharPos, null);
        }
        var field = line.GetField(route.Ordinal);
        if (route.CharPos is int c && (c < 0 || c >= field.Text.Length))
        {
            return new RouteDecision(true,
                "character " + c.ToString(inv) + " is outside " + field.Label + " (length " + field.Text.Length.ToString(inv) + "), treated as not known",
                route.Ordinal, null, field.Kind);
        }
        return new RouteDecision(true, "", route.Ordinal, route.CharPos, field.Kind);
    }
}

public sealed record HelloMessage(int Reader, int Version, int Rows, int Columns);

/// <param name="CharPos">0-based character within the field, or null for "not known".</param>
public sealed record RouteMessage(int Layout, int Ordinal, int? CharPos);

public sealed record CommandMessage(int Command, int Reader);
