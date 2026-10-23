using System.Buffers.Text;
using System.Net.Http.Headers;
using System.Text;
using PSWSMan.Connection;

using PSWSMan.Lib;

namespace cntremotepwshell;

/// <summary>
/// Builds and parses the multipart/encrypted MIME framing WinRM uses for HTTP message encryption.
/// </summary>
/// <remarks>
/// The .NET MultipartContent format is just different enough from what WinRM uses that the payload is built by
/// hand. Each chunk is:
/// <code>
/// --Encrypted Boundary\r\n
/// Content-Type: application/HTTP-SPNEGO-session-encrypted\r\n
/// OriginalContent: type=application/soap+xml;charset=UTF-8;Length=123\r\n
/// --Encrypted Boundary\r\n
/// Content-Type: application/octet-stream\r\n
/// [block built by the encryption context]
/// </code>
/// Chunks follow each other directly and the payload ends with a Single <c>--Encrypted Boundary--\r\n</c>.
/// </remarks>
internal static class WSManMessageEncryption
{
    public const string Boundary = "Encrypted Boundary";
    public const string MultipartEncrypted = "multipart/encrypted";
    public const string MultipartMultiEncrypted = "multipart/x-multi-encrypted";

    private static readonly byte[] s_newLine = "\r\n"u8.ToArray();
    private static readonly byte[] s_lengthLabel = "Length="u8.ToArray();

    /// <summary>Wraps the plaintext into the encrypted MIME body and sets the Content-Type header.</summary>
    public static ByteArrayContent Wrap(ReadOnlySpan<byte> message, IWSManEncryptionContext encryptor)
    {
        int chunkSize = encryptor.MaxEncryptionChunkSize == -1 ? message.Length : encryptor.MaxEncryptionChunkSize;
        if (chunkSize <= 0)
        {
            chunkSize = Math.Max(message.Length, 1);
        }

        int chunkCount = Math.Max(1, (message.Length + chunkSize - 1) / chunkSize);
        var chunks = new Chunk[chunkCount];

        ReadOnlySpan<byte> remaining = message;
        for (int i = 0; i < chunks.Length; i++)
        {
            int length = Math.Min(remaining.Length, chunkSize);
            ReadOnlyMemory<byte> block = encryptor.WrapWinRM(remaining[..length], out int paddingLength);
            chunks[i] = new(block, length + paddingLength);
            remaining = remaining[length..];
        }

        string protocol = encryptor.EncryptionProtocol;
        byte[] body = BuildBody(protocol, chunks);
        var content = new ByteArrayContent(body);
        string subType = chunks.Length == 1 ? MultipartEncrypted : MultipartMultiEncrypted;
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(
            $"{subType};protocol=\"{protocol}\";boundary=\"{Boundary}\"");
        return content;
    }

    /// <summary>Unwraps an encrypted MIME payload in place and returns the plaintext length.</summary>
    public static int Unwrap(Span<byte> payload, IWSManEncryptionContext encryptor)
    {
        int boundaryEnd = payload.IndexOf(s_newLine);
        if (boundaryEnd == -1)
        {
            throw new WSManException("Invalid WinRM response - failed to find MIME boundary");
        }

        ReadOnlySpan<byte> boundary = payload[..boundaryEnd].ToArray();
        int position = boundaryEnd + 2;
        int written = 0;

        while (payload.Length - position > 2)
        {
            int next = payload[position..].IndexOf(boundary);
            if (next == -1)
            {
                throw new WSManException("Invalid WinRM response - missing metadata boundary");
            }

            int expectedLength = ParseOriginalLength(payload.Slice(position, next));
            position += next + boundary.Length + 2;

            next = payload[position..].IndexOf(s_newLine);
            if (next == -1)
            {
                throw new WSManException("Invalid WinRM response - missing octet-stream header");
            }
            position += next + 2;

            next = payload[position..].IndexOf(boundary);
            if (next == -1 || next < 4)
            {
                throw new WSManException("Invalid WinRM response - missing data boundary");
            }

            Span<byte> block = payload.Slice(position, next);
            position += next + boundary.Length + 2;

            Span<byte> plaintext = encryptor.UnwrapWinRM(block);
            if (plaintext.Length != expectedLength)
            {
                throw new WSManException("Mismatched WinRM response length");
            }

            plaintext.CopyTo(payload.Slice(written, plaintext.Length));
            written += plaintext.Length;
        }

        return written;
    }

    private static byte[] BuildBody(string protocol, Chunk[] chunks)
    {
        byte[] headerStart = Encoding.UTF8.GetBytes($"--{Boundary}\r\nContent-Type: ");
        byte[] headerMiddle = Encoding.UTF8.GetBytes(
            $"\r\nOriginalContent: type={WMRemoteClient.ContentType};Length=");
        byte[] headerEnd = Encoding.UTF8.GetBytes(
            $"\r\n--{Boundary}\r\nContent-Type: application/octet-stream\r\n");
        byte[] terminator = Encoding.UTF8.GetBytes($"--{Boundary}--\r\n");
        byte[] protocolBytes = Encoding.UTF8.GetBytes(protocol);

        int fixedHeaderLength = headerStart.Length + protocolBytes.Length + headerMiddle.Length + headerEnd.Length;
        int length = terminator.Length;
        foreach (Chunk chunk in chunks)
        {
            length += fixedHeaderLength + DigitCount(chunk.OriginalLength) + chunk.Block.Length;
        }

        byte[] body = new byte[length];
        int position = 0;
        foreach (Chunk chunk in chunks)
        {
            headerStart.CopyTo(body, position);
            position += headerStart.Length;
            protocolBytes.CopyTo(body, position);
            position += protocolBytes.Length;
            headerMiddle.CopyTo(body, position);
            position += headerMiddle.Length;
            position += Encoding.UTF8.GetBytes(chunk.OriginalLength.ToString(), body.AsSpan(position));
            headerEnd.CopyTo(body, position);
            position += headerEnd.Length;
            chunk.Block.Span.CopyTo(body.AsSpan(position));
            position += chunk.Block.Length;
        }

        terminator.CopyTo(body, position);
        return body;
    }

    private static int ParseOriginalLength(ReadOnlySpan<byte> metadata)
    {
        for (int i = 0; i <= metadata.Length - s_lengthLabel.Length; i++)
        {
            ReadOnlySpan<byte> candidate = metadata.Slice(i, s_lengthLabel.Length);
            if (!Ascii.EqualsIgnoreCase(candidate, s_lengthLabel))
            {
                continue;
            }

            ReadOnlySpan<byte> digits = metadata[(i + s_lengthLabel.Length)..];
            if (Utf8Parser.TryParse(digits, out int length, out int _)
                && length >= 0
                && digits.Slice(0, length.ToString().Length).IndexOfAnyExcept("0123456789"u8) == -1)
            {
                return length;
            }

            break;
        }

        throw new WSManException("Invalid WinRM response - failed to find plaintext length");
    }

    private static int DigitCount(int value)
    {
        int digits = 1;
        while (value >= 10)
        {
            value /= 10;
            digits++;
        }

        return digits;
    }

    private readonly record struct Chunk(ReadOnlyMemory<byte> Block, int OriginalLength);
}
