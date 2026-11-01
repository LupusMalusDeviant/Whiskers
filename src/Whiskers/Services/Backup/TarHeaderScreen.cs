using System.Text;

namespace Whiskers.Services.Backup;

/// <summary>
/// Reads the tar structure as it passes to <see cref="System.Formats.Tar.TarReader"/> and refuses a header the reader
/// would act on unsafely (R1-32): the reader allocates a buffer of a PAX record's or a GNU long name's declared size
/// before it reads it, so a header that claims 2 GiB costs 2 GiB however short the data. Each 512-byte header block is
/// checked as its last byte goes through, before the reader has it: only regular files, directories without data,
/// PAX records and GNU long names are allowed, and the last two at most <paramref name="maxHeaderBytes"/> long. A PAX
/// <c>size</c> record is applied to the next entry, so the screen stays aligned with the reader.
/// </summary>
internal sealed class TarHeaderScreen(Stream inner, long maxHeaderBytes) : Stream
{
    private const int BlockSize = 512;
    private readonly byte[] _header = new byte[BlockSize];
    private int _headerFill;
    private long _skip;                 // data and padding of the current entry still to pass
    private MemoryStream? _pax;         // the data of a PAX record being collected
    private long _paxRemaining;
    private long _paxPadding;
    private long? _sizeOverride;        // "size" from the last PAX record, for the next entry

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        Screen(buffer.AsSpan(offset, read));
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var read = await inner.ReadAsync(buffer, ct);
        Screen(buffer.Span[..read]);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    private void Screen(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            if (_pax is not null && _paxRemaining > 0)
            {
                var take = (int)Math.Min(_paxRemaining, data.Length);
                _pax.Write(data[..take]);
                _paxRemaining -= take;
                data = data[take..];
                if (_paxRemaining == 0)
                {
                    _sizeOverride = PaxSize(_pax.ToArray()) ?? _sizeOverride;
                    _pax = null;
                    _skip = _paxPadding;
                }
                continue;
            }
            if (_skip > 0)
            {
                var take = (int)Math.Min(_skip, data.Length);
                _skip -= take;
                data = data[take..];
                continue;
            }
            var fill = Math.Min(BlockSize - _headerFill, data.Length);
            data[..fill].CopyTo(_header.AsSpan(_headerFill));
            _headerFill += fill;
            data = data[fill..];
            if (_headerFill == BlockSize)
            {
                _headerFill = 0;
                OnHeader();
            }
        }
    }

    private void OnHeader()
    {
        if (_header.All(b => b == 0)) return;   // end-of-archive blocks
        var type = (char)_header[156];
        var size = ParseSize(_header.AsSpan(124, 12));
        switch (type)
        {
            case 'x' or 'g' or 'L' or 'K':
                if (size > maxHeaderBytes)
                    throw new InvalidDataException($"Refusing an archive with a {size}-byte extended header.");
                if (type == 'x')
                {
                    _pax = new MemoryStream((int)size);
                    _paxRemaining = size;
                    _paxPadding = Padding(size);
                    if (size == 0) { _pax = null; _skip = 0; }
                }
                else _skip = size + Padding(size);
                return;
            case '0' or '\0' or '5':
                var effective = _sizeOverride ?? size;
                _sizeOverride = null;
                if (type == '5' && effective != 0)
                    throw new InvalidDataException("Refusing an archive with a directory entry that carries data.");
                if (effective < 0)
                    throw new InvalidDataException("Refusing an archive with an entry of invalid size.");
                _skip = effective + Padding(effective);
                return;
            default:
                throw new InvalidDataException($"Refusing tar entry type '{type}' — only regular files and directories are permitted.");
        }
    }

    private static long Padding(long size) => (BlockSize - size % BlockSize) % BlockSize;

    // Octal digits, or base-256 when the first byte has its high bit set (GNU/star for sizes of 8 GiB and more).
    private static long ParseSize(ReadOnlySpan<byte> field)
    {
        if ((field[0] & 0x80) != 0)
        {
            if ((field[0] & 0x40) != 0) return -1;   // negative
            long value = field[0] & 0x3F;
            foreach (var b in field[1..])
            {
                if (value > (long.MaxValue >> 8)) return long.MaxValue;
                value = (value << 8) | b;
            }
            return value;
        }
        long octal = 0;
        foreach (var b in field)
        {
            if (b is 0 or (byte)' ') { if (octal > 0) break; continue; }
            if (b is < (byte)'0' or > (byte)'7') throw new InvalidDataException("Refusing an archive with a malformed size field.");
            if (octal > (long.MaxValue >> 3)) return long.MaxValue;
            octal = (octal << 3) | (long)(b - '0');
        }
        return octal;
    }

    // PAX records: "<length> <key>=<value>\n". Only "size" matters here; a malformed record set is refused.
    private static long? PaxSize(byte[] records)
    {
        long? size = null;
        var pos = 0;
        while (pos < records.Length)
        {
            var space = Array.IndexOf(records, (byte)' ', pos);
            if (space < 0 || !int.TryParse(Encoding.ASCII.GetString(records, pos, space - pos), out var length)
                || length <= space - pos || pos + length > records.Length)
                throw new InvalidDataException("Refusing an archive with a malformed extended header.");
            var record = Encoding.UTF8.GetString(records, space + 1, pos + length - space - 2);
            var eq = record.IndexOf('=');
            if (eq > 0 && record[..eq] == "size")
            {
                if (!long.TryParse(record[(eq + 1)..], out var parsed) || parsed < 0)
                    throw new InvalidDataException("Refusing an archive with a malformed size record.");
                size = parsed;
            }
            pos += length;
        }
        return size;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
