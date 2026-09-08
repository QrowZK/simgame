using System.Buffers.Binary;
using System.Text;

namespace Sim;

/// A malformed batch of command bytes. Thrown rather than returned, because
/// there is nothing a caller can do with half a command: under lockstep a peer
/// that cannot read a command cannot simulate the tick, and guessing is how a
/// desync becomes untraceable.
public sealed class CommandFormatException : Exception
{
    public CommandFormatException(string message) : base(message) { }
}

/// Commands to bytes and back (ADR 0037).
///
/// The transport (ADR 0035) carries an opaque `byte[]` on one reliable ordered
/// channel and never looks inside, so this encoding is the sim's to own and the
/// sim's to test. It is deliberately explicit little-endian rather than
/// anything reflective: `BinaryFormatter` and JSON both make the wire format a
/// consequence of a type's field order, and a field reordered during a refactor
/// would then be a silent protocol change between two builds that both claim
/// the same version.
///
/// Every read is bounds-checked and every enum is range-checked, so a corrupt
/// or hostile packet produces a `CommandFormatException` at the door instead of
/// a command with a nonsense kind halfway through a tick.
public static class CommandCodec
{
    /// Bumped whenever the layout below changes. It is separate from the save
    /// version and from the network protocol string on purpose: those change
    /// for their own reasons, and a peer that can read a save is not thereby a
    /// peer that can read a command.
    /// **2** since ADR 0040 added `Load` and `Take`. The byte layout did not
    /// change -- only the set of kind bytes that decode -- but a peer on format
    /// 1 must refuse the whole batch at the door rather than only the first
    /// batch that happens to contain a load, which would leave it running a
    /// world it could not see the whole of.
    public const byte Format = 2;

    /// Guards against a mistaken framing more than against corruption -- a
    /// packet from some other channel decoded as commands would otherwise
    /// produce a plausible-looking batch.
    private const ushort Magic = 0x5343; // "SC", sim command.

    /// Names are short data ids (`iron_plate`, `smelt_iron_plate`). The cap is
    /// a refusal rather than a truncation: a truncated name is a command that
    /// means something different on the other peer.
    public const int MaxNameLength = 255;

    /// One batch is one tick's worth from one peer. Bigger than any hand can
    /// produce and small enough that a bad length prefix cannot make a decoder
    /// allocate a gigabyte.
    public const int MaxCommands = 4096;

    public static byte[] Encode(IReadOnlyList<PlayerCommand> commands)
    {
        if (commands.Count > MaxCommands)
            throw new CommandFormatException(
                $"{commands.Count} commands is more than the {MaxCommands} a batch may carry");

        var buffer = new List<byte>(16 + commands.Count * 40);
        Span<byte> scratch = stackalloc byte[8];

        BinaryPrimitives.WriteUInt16LittleEndian(scratch, Magic);
        buffer.Add(scratch[0]);
        buffer.Add(scratch[1]);
        buffer.Add(Format);

        BinaryPrimitives.WriteInt32LittleEndian(scratch, commands.Count);
        for (var i = 0; i < 4; i++) buffer.Add(scratch[i]);

        foreach (var command in commands)
        {
            BinaryPrimitives.WriteInt64LittleEndian(scratch, command.Tick);
            for (var i = 0; i < 8; i++) buffer.Add(scratch[i]);

            WriteInt(buffer, scratch, command.PlayerId);
            WriteInt(buffer, scratch, command.Sequence);
            buffer.Add((byte)command.Kind);
            WriteInt(buffer, scratch, command.X);
            WriteInt(buffer, scratch, command.Y);
            WriteInt(buffer, scratch, command.Amount);
            buffer.Add((byte)command.Facing);
            WriteName(buffer, command.Item);
            WriteName(buffer, command.Recipe);
        }

        return buffer.ToArray();
    }

    public static byte[] Encode(PlayerCommand command) => Encode(new[] { command });

    public static List<PlayerCommand> Decode(ReadOnlySpan<byte> bytes)
    {
        var at = 0;

        if (bytes.Length < 7)
            throw new CommandFormatException($"a batch is at least 7 bytes; got {bytes.Length}");

        var magic = BinaryPrimitives.ReadUInt16LittleEndian(Take(bytes, ref at, 2));
        if (magic != Magic)
            throw new CommandFormatException($"not a command batch (magic 0x{magic:X4})");

        var format = Take(bytes, ref at, 1)[0];
        if (format != Format)
            throw new CommandFormatException(
                $"command format {format}, this build speaks {Format}");

        var count = BinaryPrimitives.ReadInt32LittleEndian(Take(bytes, ref at, 4));
        if (count < 0 || count > MaxCommands)
            throw new CommandFormatException($"a batch of {count} commands is not credible");

        var commands = new List<PlayerCommand>(count);
        for (var i = 0; i < count; i++)
        {
            var tick = BinaryPrimitives.ReadInt64LittleEndian(Take(bytes, ref at, 8));
            var playerId = ReadInt(bytes, ref at);
            var sequence = ReadInt(bytes, ref at);
            var kind = (CommandKind)Take(bytes, ref at, 1)[0];
            if (!Enum.IsDefined(kind))
                throw new CommandFormatException($"command kind {(byte)kind} is not one of ours");

            var x = ReadInt(bytes, ref at);
            var y = ReadInt(bytes, ref at);
            var amount = ReadInt(bytes, ref at);
            var facing = (Direction)Take(bytes, ref at, 1)[0];
            if (!Enum.IsDefined(facing))
                throw new CommandFormatException($"direction {(byte)facing} is not one of ours");

            var item = ReadName(bytes, ref at);
            var recipe = ReadName(bytes, ref at);

            commands.Add(new PlayerCommand(tick, playerId, sequence, kind,
                                           x, y, amount, facing, item, recipe));
        }

        if (at != bytes.Length)
            throw new CommandFormatException(
                $"{bytes.Length - at} bytes left over after {count} commands");

        return commands;
    }

    public static List<PlayerCommand> Decode(byte[] bytes) => Decode(bytes.AsSpan());

    private static void WriteInt(List<byte> buffer, Span<byte> scratch, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(scratch, value);
        for (var i = 0; i < 4; i++) buffer.Add(scratch[i]);
    }

    private static void WriteName(List<byte> buffer, string name)
    {
        // ASCII rather than UTF-8: every id in `data/` is lower-case ASCII, and
        // a length in bytes that is not a length in characters is a class of
        // bug this format has no reason to carry. A name that is not ASCII is
        // refused rather than mangled.
        foreach (var c in name)
            if (c > 0x7F)
                throw new CommandFormatException($"'{name}' is not an ascii data id");

        if (name.Length > MaxNameLength)
            throw new CommandFormatException(
                $"'{name}' is longer than the {MaxNameLength} bytes a name may take");

        buffer.Add((byte)name.Length);
        for (var i = 0; i < name.Length; i++) buffer.Add((byte)name[i]);
    }

    private static string ReadName(ReadOnlySpan<byte> bytes, ref int at)
    {
        var length = Take(bytes, ref at, 1)[0];
        if (length == 0) return "";
        return Encoding.ASCII.GetString(Take(bytes, ref at, length));
    }

    private static int ReadInt(ReadOnlySpan<byte> bytes, ref int at)
        => BinaryPrimitives.ReadInt32LittleEndian(Take(bytes, ref at, 4));

    private static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> bytes, ref int at, int count)
    {
        if (at + count > bytes.Length)
            throw new CommandFormatException(
                $"wanted {count} bytes at {at}, the batch is {bytes.Length} long");

        var slice = bytes.Slice(at, count);
        at += count;
        return slice;
    }
}
