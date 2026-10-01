using Engram.Core;

namespace Engram.Core.Tests;

public class CodeSourceReaderBoundTests
{
    private sealed class CountingStream(long length) : Stream
    {
        public long Served { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var give = (int)Math.Min(count, length - Served);
            Array.Fill(buffer, (byte)'x', offset, give);
            Served += give;
            return give;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => Served; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void AFileThatGrewAfterItWasStatted_IsNeverReadPastTheLimitPlusOne()
    {
        var dir = Path.Combine(Path.GetTempPath(), "engram-bound-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        try
        {
            var file = Path.Combine(dir, "src", "a.cs");
            File.WriteAllText(file, "tiny");
            var stream = new CountingStream(10_000_000);
            var verdict = new FileFreshness.Verdict(FileFreshness.State.Fresh, TimeSpan.Zero, file, dir);

            var read = CodeSourceReader.Read(verdict, 1000, _ => stream);

            Assert.Equal("over 1000 bytes", read.Reason);
            Assert.Equal(1001, stream.Served);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
