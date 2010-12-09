﻿using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;

namespace ZLibNet
{
	/// <summary>Provides methods and properties used to compress and decompress streams.</summary>
	unsafe public class ZLibStream : Stream
	{
		//		private const int BufferSize = 16384;

		long pBytesIn = 0;
		long pBytesOut = 0;
		bool pSuccess;
//		uint pCrcValue = 0;
		const int WORK_DATA_SIZE = 0x1000;
		byte[] pWorkData = new byte[WORK_DATA_SIZE];
		int pWorkDataPos = 0;

		private Stream pStream;
		private CompressionMode pMode;
		private z_stream pZstream = new z_stream();
		bool pLeaveOpen;

		public ZLibStream(Stream stream, CompressionMode mode)
			: this(stream, mode, CompressionLevel.Default)
		{
		}

		public ZLibStream(Stream stream, CompressionMode mode, bool leaveOpen):
			this(stream, mode, CompressionLevel.Default, leaveOpen)
		{
		}

		public ZLibStream(Stream stream, CompressionMode mode, CompressionLevel level) :
			this(stream, mode, level, false)
		{
		}

		/// <summary>Initializes a new instance of the GZipStream class using the specified stream and CompressionMode value.</summary>
		/// <param name="stream">The stream to compress or decompress.</param>
		/// <param name="mode">One of the CompressionMode values that indicates the action to take.</param>
		public ZLibStream(Stream stream, CompressionMode mode, CompressionLevel level, bool leaveOpen)
		{
			this.pLeaveOpen = leaveOpen;
			this.pStream = stream;
			this.pMode = mode;

			int ret;
			fixed (z_stream* z = &this.pZstream)
			{
				if (this.pMode == CompressionMode.Compress)
					ret = ZLib.deflateInit(z, (int)level, ZLib.ZLibVersion, Marshal.SizeOf(typeof(z_stream)));
				else
					ret = ZLib.inflateInit(z, ZLibOpenType.Both, ZLib.ZLibVersion, Marshal.SizeOf(typeof(z_stream)));
			}

			if (ret != ZLibReturnCode.Ok)
				throw new ZLibException(ret);

			pSuccess = true;
		}

		/// <summary>GZipStream destructor. Cleans all allocated resources.</summary>
		~ZLibStream()
		{
			this.Dispose(false);
		}


		/// <summary>
		/// Stream.Close() ->   this.Dispose(true); + GC.SuppressFinalize(this);
		/// Stream.Dispose() ->  this.Close();
		/// </summary>
		/// <param name="disposing"></param>
		protected override void Dispose(bool disposing)
		{
			try
			{
				try
				{
					if (disposing) //managed stuff
					{
						if (this.pStream != null)
						{
							//managed stuff
							if (this.pMode == CompressionMode.Compress && pSuccess)
							{
								Flush();
//								this.pStream.Flush();
							}
							if (!pLeaveOpen)
								this.pStream.Close();
							this.pStream = null;
						}
					}
				}
				finally
				{
					//unmanaged stuff
					FreeUnmanagedResources();
				}

			}
			finally
			{
				base.Dispose(disposing);
			}
		}

		// Finished, free the resources used.
		private void FreeUnmanagedResources()
		{
			fixed (z_stream* zstreamPtr = &pZstream)
			{
				if (this.pMode == CompressionMode.Compress)
					ZLib.deflateEnd(zstreamPtr);
				else
					ZLib.inflateEnd(zstreamPtr);
			}
		}

		private bool IsReading()
		{
			return this.pMode == CompressionMode.Decompress;
		}
		private bool IsWriting()
		{
			return this.pMode == CompressionMode.Compress;
		}

		/// <summary>Reads a number of decompressed bytes into the specified byte array.</summary>
		/// <param name="array">The array used to store decompressed bytes.</param>
		/// <param name="offset">The location in the array to begin reading.</param>
		/// <param name="count">The number of bytes decompressed.</param>
		/// <returns>The number of bytes that were decompressed into the byte array. If the end of the stream has been reached, zero or the number of bytes read is returned.</returns>
		public override int Read(byte[] buffer, int offset, int count)
		{
			if (pMode == CompressionMode.Compress)
				throw new NotSupportedException("Can't read on a compress stream!");

			int readLen = 0;
			if (pWorkDataPos != -1)
			{
				fixed (byte* workDataPtr = &pWorkData[0], bufferPtr = &buffer[0])
				{
					pZstream.next_in = &workDataPtr[pWorkDataPos];
					pZstream.next_out = &bufferPtr[offset];
					pZstream.avail_out = (uint)count;

					while (pZstream.avail_out != 0)
					{
						if (pZstream.avail_in == 0)
						{
							pWorkDataPos = 0;
							pZstream.next_in = workDataPtr;
							pZstream.avail_in = (uint)pStream.Read(pWorkData, 0, WORK_DATA_SIZE);
							pBytesIn += pZstream.avail_in;
						}

						uint inCount = pZstream.avail_in;
						uint outCount = pZstream.avail_out;

						int zlibError;
						fixed (z_stream* zstreamPtr = &pZstream)
							zlibError = ZLib.inflate(zstreamPtr, ZLibFlush.NoFlush); // flush method for inflate has no effect

						pWorkDataPos += (int)(inCount - pZstream.avail_in);
						readLen += (int)(outCount - pZstream.avail_out);

						if (zlibError == ZLibReturnCode.StreamEnd)
						{
							pWorkDataPos = -1; // magic for StreamEnd
							break;
						}
						else if (zlibError != ZLibReturnCode.Ok)
						{
							pSuccess = false;
							throw new ZLibException(zlibError, pZstream.lasterrormsg);
						}
					}

//					pCrcValue = crc32(pCrcValue, &bufferPtr[offset], (uint)readLen);
					pBytesOut += readLen;
				}

			}
			return readLen;
		}


		/// <summary>This property is not supported and always throws a NotSupportedException.</summary>
		/// <param name="array">The array used to store compressed bytes.</param>
		/// <param name="offset">The location in the array to begin reading.</param>
		/// <param name="count">The number of bytes compressed.</param>
		public override void Write(byte[] buffer, int offset, int count)
		{
			if (pMode == CompressionMode.Decompress)
				throw new NotSupportedException("Can't write on a decompression stream!");

			pBytesIn += count;

			fixed (byte* writePtr = pWorkData, bufferPtr = buffer)
			{
				pZstream.next_in = &bufferPtr[offset];
				pZstream.avail_in = (uint)count;
				pZstream.next_out = &writePtr[pWorkDataPos];
				pZstream.avail_out = (uint)(WORK_DATA_SIZE - pWorkDataPos);

				//				pCrcValue = crc32(pCrcValue, &bufferPtr[offset], (uint)count);

				while (pZstream.avail_in != 0)
				{
					if (pZstream.avail_out == 0)
					{
						//rar logikk, men det betyr vel bare at den kun skriver hvis buffer ble fyllt helt,
						//dvs halvfyllt buffer vil kun skrives ved flush
						pStream.Write(pWorkData, 0, (int)WORK_DATA_SIZE);
						pBytesOut += WORK_DATA_SIZE;
						pWorkDataPos = 0;
						pZstream.next_out = writePtr;
						pZstream.avail_out = WORK_DATA_SIZE;
					}

					uint outCount = pZstream.avail_out;

					int zlibError;
					fixed (z_stream* zstreamPtr = &pZstream)
						zlibError = ZLib.deflate(zstreamPtr, ZLibFlush.NoFlush);

					pWorkDataPos += (int)(outCount - pZstream.avail_out);

					if (zlibError != ZLibReturnCode.Ok)
					{
						pSuccess = false;
						throw new ZLibException(zlibError, pZstream.lasterrormsg);
					}

				}
			}
		}

		/// <summary>Flushes the contents of the internal buffer of the current GZipStream object to the underlying stream.</summary>
		public override void Flush()
		{
			if (pMode == CompressionMode.Decompress)
				throw new NotSupportedException("Can't flush a decompression stream.");

			fixed (byte* workDataPtr = pWorkData)
			{
				pZstream.next_in = (byte*)0;
				pZstream.avail_in = 0;
				pZstream.next_out = &workDataPtr[pWorkDataPos];
				pZstream.avail_out = (uint)(WORK_DATA_SIZE - pWorkDataPos);

				int zlibError = ZLibReturnCode.Ok;
				while (zlibError != ZLibReturnCode.StreamEnd)
				{
					if (pZstream.avail_out != 0)
					{
						uint outCount = pZstream.avail_out;
						fixed (z_stream* zstreamPtr = &pZstream)
							zlibError = ZLib.deflate(zstreamPtr, ZLibFlush.Finish);

						pWorkDataPos += (int)(outCount - pZstream.avail_out);
						if (zlibError == ZLibReturnCode.StreamEnd)
						{
							//ok. will break loop
						}
						else if (zlibError != ZLibReturnCode.Ok)
						{
							pSuccess = false;
							throw new ZLibException(zlibError, pZstream.lasterrormsg);
						}
					}

					pStream.Write(pWorkData, 0, pWorkDataPos);
					pBytesOut += pWorkDataPos;
					pWorkDataPos = 0;
					pZstream.next_out = workDataPtr;
					pZstream.avail_out = WORK_DATA_SIZE;
				}
			}

			this.pStream.Flush();
		}


		//public uint CRC32
		//{
		//    get
		//    {
		//        return pCrcValue;
		//    }
		//}


		// The compression ratio obtained (same for compression/decompression).
		public double CompressionRatio
		{
			get
			{
				if (pMode == CompressionMode.Compress)
					return ((pBytesIn == 0) ? 0.0 : (100.0 - ((double)pBytesOut * 100.0 / (double)pBytesIn)));
				else
					return ((pBytesOut == 0) ? 0.0 : (100.0 - ((double)pBytesIn * 100.0 / (double)pBytesOut)));
			}
		}

		/// <summary>Gets a value indicating whether the stream supports reading while decompressing a file.</summary>
		public override bool CanRead
		{
			get 
			{ 
				return pMode == CompressionMode.Decompress && pStream.CanRead;
			}
		}

		/// <summary>Gets a value indicating whether the stream supports writing.</summary>
		public override bool CanWrite
		{
			get
			{
				return pMode == CompressionMode.Compress && pStream.CanWrite;
			}
		}

		/// <summary>Gets a value indicating whether the stream supports seeking.</summary>
		public override bool CanSeek
		{
			get { return (false); }
		}

		/// <summary>Gets a reference to the underlying stream.</summary>
		public Stream BaseStream
		{
			get { return (this.pStream); }
		}

		/// <summary>This property is not supported and always throws a NotSupportedException.</summary>
		/// <param name="offset">The location in the stream.</param>
		/// <param name="origin">One of the SeekOrigin values.</param>
		/// <returns>A long value.</returns>
		public override long Seek(long offset, SeekOrigin origin)
		{
			throw new NotSupportedException("Seek not supported");
		}

		/// <summary>This property is not supported and always throws a NotSupportedException.</summary>
		/// <param name="value">The length of the stream.</param>
		public override void SetLength(long value)
		{
			throw new NotSupportedException("SetLength not supported");
		}

		/// <summary>This property is not supported and always throws a NotSupportedException.</summary>
		public override long Length
		{
			get
			{
				throw new NotSupportedException("Length not supported.");
			}
		}

		/// <summary>This property is not supported and always throws a NotSupportedException.</summary>
		public override long Position
		{
			get
			{
				throw new NotSupportedException("Position not supported.");
			}
			set
			{
				throw new NotSupportedException("Position not supported.");
			}
		}
	}
}