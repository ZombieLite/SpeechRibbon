using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpeechRibbon;

internal static class ExternalBundleAssets
{
    private const int TrailerSize = 48;
    private const string BundleMagic = "SRBNDL01";
    private const string AssetsMagic = "SRASST01";

    public static Stream? TryOpen(string developmentFileName)
    {
        var bundlePath = Environment.GetEnvironmentVariable("SPEECHRIBBON_BUNDLE_PATH");
        if (string.IsNullOrWhiteSpace(bundlePath)) return null;
        try
        {
            var stream = new FileStream(bundlePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
            var entry = ReadManifest(stream).SingleOrDefault(item =>
                string.Equals(item.Name, Path.GetFileName(developmentFileName), StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                stream.Dispose();
                throw new SpeechRibbonException("INTERNAL_COMPONENT_MISSING", "Внутренний компонент отсутствует в контейнере SpeechRibbon.");
            }
            return new SegmentReadStream(stream, entry.Offset, entry.Length);
        }
        catch (SpeechRibbonException) { throw; }
        catch (Exception ex)
        {
            throw new SpeechRibbonException("INTERNAL_COMPONENT_CORRUPT", "Контейнер внутренних компонентов SpeechRibbon повреждён.", ex);
        }
    }

    private static IReadOnlyList<BundleEntry> ReadManifest(FileStream stream)
    {
        if (stream.Length <= TrailerSize * 2) throw new InvalidDataException("Bundle is too small.");
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        stream.Position = stream.Length - TrailerSize;
        var payloadLength = reader.ReadUInt64();
        reader.ReadBytes(32);
        if (System.Text.Encoding.ASCII.GetString(reader.ReadBytes(8)) != BundleMagic) throw new InvalidDataException("Bundle trailer is invalid.");
        var payloadStart = checked(stream.Length - TrailerSize - (long)payloadLength);
        var assetsTrailerStart = payloadStart - TrailerSize;
        if (assetsTrailerStart <= 0) throw new InvalidDataException("Assets trailer is absent.");
        stream.Position = assetsTrailerStart;
        var manifestLength = reader.ReadUInt64();
        var expectedManifestHash = reader.ReadBytes(32);
        if (System.Text.Encoding.ASCII.GetString(reader.ReadBytes(8)) != AssetsMagic || manifestLength is 0 or > 1_048_576)
            throw new InvalidDataException("Assets manifest trailer is invalid.");
        var manifestStart = checked(assetsTrailerStart - (long)manifestLength);
        if (manifestStart <= 0) throw new InvalidDataException("Assets manifest bounds are invalid.");
        stream.Position = manifestStart;
        var manifestBytes = reader.ReadBytes((int)manifestLength);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(manifestBytes), expectedManifestHash))
            throw new InvalidDataException("Assets manifest hash is invalid.");
        var manifest = JsonSerializer.Deserialize<BundleManifest>(manifestBytes) ?? throw new InvalidDataException("Assets manifest is invalid.");
        if (manifest.SchemaVersion != 1 || manifest.Entries.Count == 0) throw new InvalidDataException("Assets manifest schema is invalid.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name) || Path.GetFileName(entry.Name) != entry.Name || !names.Add(entry.Name) ||
                entry.Offset < 0 || entry.Length <= 0 || entry.Offset > manifestStart || entry.Length > manifestStart - entry.Offset ||
                entry.Sha256.Length != 64 || !entry.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("Assets manifest entry is invalid.");
        }
        return manifest.Entries;
    }

    private sealed class BundleManifest
    {
        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
        [JsonPropertyName("entries")] public List<BundleEntry> Entries { get; set; } = [];
    }

    private sealed class BundleEntry
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("offset")] public long Offset { get; set; }
        [JsonPropertyName("length")] public long Length { get; set; }
        [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    }

    private sealed class SegmentReadStream : Stream
    {
        private readonly FileStream _source;
        private readonly long _length;
        private long _position;

        public SegmentReadStream(FileStream source, long offset, long length)
        {
            _source = source;
            _length = length;
            _source.Position = offset;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _source.Read(buffer, offset, (int)Math.Min(count, _length - _position));
            _position += read;
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await _source.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _length - _position)], cancellationToken);
            _position += read;
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _source.Dispose();
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            await _source.DisposeAsync();
            GC.SuppressFinalize(this);
        }
    }
}
