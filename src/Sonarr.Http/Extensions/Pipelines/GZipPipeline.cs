using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Nancy;
using Nancy.Bootstrapper;
using NLog;
using NzbDrone.Common.Extensions;

namespace Sonarr.Http.Extensions.Pipelines
{
    public class GzipCompressionPipeline : IRegisterNancyPipeline
    {
        private readonly Logger _logger;

        public int Order => 0;

        public GzipCompressionPipeline(Logger logger)
        {
            _logger = logger;
        }

        public void Register(IPipelines pipelines)
        {
            pipelines.AfterRequest.AddItemToEndOfPipeline(CompressResponse);
        }

        private class ExceptionSafeGZipStream : GZipStream
        {
            private class ExceptionCatchingStream : Stream
            {
                private Stream _innerStream;
                public Exception Exception;

                public ExceptionCatchingStream(Stream stream)
                {
                    _innerStream = stream;
                }
                public override bool CanRead => _innerStream.CanRead;
                public override bool CanSeek => _innerStream.CanSeek;
                public override bool CanWrite => _innerStream.CanWrite;
                public override long Length => _innerStream.Length;

                public override long Position { get => _innerStream.Position; set => _innerStream.Position = value; }

                public override void Flush()
                {
                    _innerStream.Flush();
                }

                public override int Read(byte[] buffer, int offset, int count)
                {
                    return _innerStream.Read(buffer, offset, count);
                }

                public override long Seek(long offset, SeekOrigin origin)
                {
                    return _innerStream.Seek(offset, origin);
                }

                public override void SetLength(long value)
                {
                    _innerStream.SetLength(value);
                }

                public override void Write(byte[] buffer, int offset, int count)
                {
                    try
                    {
                        _innerStream.Write(buffer, offset, count);
                    }
                    catch (Exception ex)
                    {
                        if (Exception != null)
                            Exception = ex;
                    }
                }
            }

            private Stream _innerStream;
            private ExceptionCatchingStream _catcher;

            private Stream WrapInner(Stream innerStream)
            {
                _innerStream = innerStream;
                _catcher = new ExceptionCatchingStream(innerStream);
                return _catcher;
            }

            public ExceptionSafeGZipStream(Stream stream, CompressionMode mode)
                : base(new ExceptionCatchingStream(stream), CompressionMode.Compress, true)
            {
                _innerStream = stream;
                _catcher = base.BaseStream as ExceptionCatchingStream;
            }

            public override void Write(byte[] array, int offset, int count)
            {
                base.Write(array, offset, count);

                if (_catcher.Exception != null)
                {
                    var ex = _catcher.Exception;
                    _catcher.Exception = null;
                    throw ex;
                }
            }
        }

        private class ThrowingStream : Stream
        {
            private long _position;

            public override bool CanRead => false;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => _position;

            public override long Position { get => _position; set => throw new NotSupportedException(); }

            public override void Flush()
            {

            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (_position + count > 10000)
                    throw new IOException("Some Error");

                _position += count;
            }
        }

        private void CompressResponse(NancyContext context)
        {
            var request = context.Request;
            var response = context.Response;

            try
            {
                if (
                   response.Contents != Response.NoBody
                && !response.ContentType.Contains("image")
                && !response.ContentType.Contains("font")
                && request.Headers.AcceptEncoding.Any(x => x.Contains("gzip"))
                && !AlreadyGzipEncoded(response)
                && !ContentLengthIsTooSmall(response))
                {
                    var contents = response.Contents;

                    response.Headers["Content-Encoding"] = "gzip";
                    response.Contents = responseStream =>
                    {
                        using (var gzip = new ExceptionSafeGZipStream(responseStream, CompressionMode.Compress))
                        using (var buffered = new BufferedStream(gzip, 8192))
                        {
                            contents.Invoke(buffered);
                        }
                    };
                }
            }

            catch (Exception ex)
            {
                _logger.Error(ex, "Unable to gzip response");
                throw;
            }
        }

        private static bool ContentLengthIsTooSmall(Response response)
        {
            var contentLength = response.Headers.GetValueOrDefault("Content-Length");

            if (contentLength != null && long.Parse(contentLength) < 1024)
            {
                return true;
            }

            return false;
        }

        private static bool AlreadyGzipEncoded(Response response)
        {
            var contentEncoding = response.Headers.GetValueOrDefault("Content-Encoding");

            if (contentEncoding == "gzip")
            {
                return true;
            }

            return false;
        }
    }
}
