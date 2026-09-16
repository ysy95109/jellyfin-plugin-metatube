namespace Jellyfin.Plugin.MetaTube.Helpers;

/// <summary>Registered with Emby's response disposal list; stream disposal also owns the HTTP response.</summary>
internal sealed class ResponseOwnedStream(Stream stream, HttpResponseMessage response) : Stream
{
    private HttpResponseMessage _owner = response;
    public override bool CanRead => stream.CanRead;
    public override bool CanSeek => stream.CanSeek;
    public override bool CanWrite => stream.CanWrite;
    public override long Length => stream.Length;
    public override long Position { get => stream.Position; set => stream.Position = value; }
    public override void Flush() => stream.Flush();
    public override int Read(byte[] buffer, int offset, int count)
    {
        try { return stream.Read(buffer, offset, count); }
        catch { Dispose(); throw; }
    }
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
    {
        try { return await stream.ReadAsync(buffer, offset, count, token); }
        catch { Dispose(); throw; }
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        try { return await stream.ReadAsync(buffer, token); }
        catch { Dispose(); throw; }
    }
    public override long Seek(long offset, SeekOrigin origin) => stream.Seek(offset, origin);
    public override void SetLength(long value) => stream.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => stream.Write(buffer, offset, count);
    protected override void Dispose(bool disposing)
    {
        if (disposing) Interlocked.Exchange(ref _owner, null)?.Dispose();
        base.Dispose(disposing);
    }
}
