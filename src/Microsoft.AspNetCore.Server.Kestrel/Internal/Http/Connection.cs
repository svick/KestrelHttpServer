// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Server.Kestrel.Filter;
using Microsoft.AspNetCore.Server.Kestrel.Filter.Internal;
using Microsoft.AspNetCore.Server.Kestrel.Internal.Infrastructure;
using Microsoft.AspNetCore.Server.Kestrel.Internal.Networking;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Server.Kestrel.Internal.Http
{
    public class Connection : ConnectionContext, IConnectionControl
    {
        // Base32 encoding - in ascii sort order for easy text based sorting
        private static readonly string _encode32Chars = "0123456789ABCDEFGHIJKLMNOPQRSTUV";

        private static readonly Action<UvStreamHandle, int, object> _readCallback =
            (handle, status, state) => ReadCallback(handle, status, state);
        private static readonly Func<UvStreamHandle, int, object, Libuv.uv_buf_t> _allocCallback =
            (handle, suggestedsize, state) => AllocCallback(handle, suggestedsize, state);

        // Seed the _lastConnectionId for this application instance with
        // the number of 100-nanosecond intervals that have elapsed since 12:00:00 midnight, January 1, 0001
        // for a roughly increasing _requestId over restarts
        private static long _lastConnectionId = DateTime.UtcNow.Ticks;

        private readonly UvStreamHandle _socket;
        private readonly Frame _frame;
        private ConnectionFilterContext _filterContext;
        private LibuvStream _libuvStream;
        private FilteredStreamAdapter _filteredStreamAdapter;
        private Task _readInputTask;

        private TaskCompletionSource<object> _socketClosedTcs = new TaskCompletionSource<object>();
        private BufferSizeControl _bufferSizeControl;

        private long _lastTimestamp;
        private long _timeoutTimestamp = long.MaxValue;

        public Connection(ListenerContext context, UvStreamHandle socket) : base(context)
        {
            _socket = socket;
            socket.Connection = this;
            ConnectionControl = this;

            ConnectionId = GenerateConnectionId(Interlocked.Increment(ref _lastConnectionId));

            if (ServiceContext.ServerOptions.Limits.MaxRequestBufferSize.HasValue)
            {
                _bufferSizeControl = new BufferSizeControl(ServiceContext.ServerOptions.Limits.MaxRequestBufferSize.Value, this, ListenerContext.Thread);
            }

            SocketInput = new SocketInput(ListenerContext.Thread.Memory, ServiceContext.ThreadPool, _bufferSizeControl);
            SocketOutput = new SocketOutput(ListenerContext.Thread, _socket, this, ConnectionId, ServiceContext.Log, ServiceContext.ThreadPool);

            var tcpHandle = _socket as UvTcpHandle;
            if (tcpHandle != null)
            {
                RemoteEndPoint = tcpHandle.GetPeerIPEndPoint();
                LocalEndPoint = tcpHandle.GetSockIPEndPoint();
            }

            _frame = ServiceContext.FrameFactory(this);
            _lastTimestamp = ListenerContext.Thread.Loop.Now();
        }

        // Internal for testing
        internal Connection()
        {
        }

        public ServiceContext ServiceContext => ListenerContext.ServiceContext;

        public void Start()
        {
            ServiceContext.Log.ConnectionStart(ConnectionId);

            // Start socket prior to applying the ConnectionFilter
            _socket.ReadStart(_allocCallback, _readCallback, this);

            if (ServiceContext.ServerOptions.ConnectionFilter == null)
            {
                _frame.Start();
            }
            else
            {
                _libuvStream = new LibuvStream(SocketInput, SocketOutput);

                _filterContext = new ConnectionFilterContext
                {
                    Connection = _libuvStream,
                    Address = ListenerContext.ServerAddress
                };

                try
                {
                    ServiceContext.ServerOptions.ConnectionFilter.OnConnectionAsync(_filterContext).ContinueWith((task, state) =>
                    {
                        var connection = (Connection)state;

                        if (task.IsFaulted)
                        {
                            connection.ServiceContext.Log.LogError(0, task.Exception, "ConnectionFilter.OnConnection");
                            connection.ConnectionControl.End(ProduceEndType.SocketDisconnect);
                        }
                        else if (task.IsCanceled)
                        {
                            connection.ServiceContext.Log.LogError("ConnectionFilter.OnConnection Canceled");
                            connection.ConnectionControl.End(ProduceEndType.SocketDisconnect);
                        }
                        else
                        {
                            connection.ApplyConnectionFilter();
                        }
                    }, this);
                }
                catch (Exception ex)
                {
                    ServiceContext.Log.LogError(0, ex, "ConnectionFilter.OnConnection");
                    ConnectionControl.End(ProduceEndType.SocketDisconnect);
                }
            }
        }

        public Task StopAsync()
        {
            _frame.Stop();
            _frame.SocketInput.CompleteAwaiting();

            return _socketClosedTcs.Task;
        }

        public virtual void Abort(Exception error = null)
        {
            // Frame.Abort calls user code while this method is always
            // called from a libuv thread.
            ServiceContext.ThreadPool.Run(() =>
            {
                _frame.Abort(error);
            });
        }

        // Called on Libuv thread
        public virtual void OnSocketClosed()
        {
            if (_filteredStreamAdapter != null)
            {
                _readInputTask.ContinueWith((task, state) =>
                {
                    var connection = (Connection)state;
                    connection._filterContext.Connection.Dispose();
                    connection._filteredStreamAdapter.Dispose();
                }, this);
            }

            SocketInput.Dispose();
            _socketClosedTcs.TrySetResult(null);
        }

        // Called on Libuv thread
        public void Tick(long timestamp)
        {
            if (timestamp > _timeoutTimestamp)
            {
                StopAsync();
            }

            Interlocked.Exchange(ref _lastTimestamp, timestamp);
        }

        private void ApplyConnectionFilter()
        {
            if (_filterContext.Connection != _libuvStream)
            {
                _filteredStreamAdapter = new FilteredStreamAdapter(ConnectionId, _filterContext.Connection, ListenerContext.Thread.Memory, ServiceContext.Log, ServiceContext.ThreadPool, _bufferSizeControl);

                _frame.SocketInput = _filteredStreamAdapter.SocketInput;
                _frame.SocketOutput = _filteredStreamAdapter.SocketOutput;

                _readInputTask = _filteredStreamAdapter.ReadInputAsync();
            }

            _frame.ConnectionContext.PrepareRequest = _filterContext.PrepareRequest;

            _frame.Start();
        }

        private static Libuv.uv_buf_t AllocCallback(UvStreamHandle handle, int suggestedSize, object state)
        {
            return ((Connection)state).OnAlloc(handle, suggestedSize);
        }

        private Libuv.uv_buf_t OnAlloc(UvStreamHandle handle, int suggestedSize)
        {
            var result = SocketInput.IncomingStart();

            return handle.Libuv.buf_init(
                result.DataArrayPtr + result.End,
                result.Data.Offset + result.Data.Count - result.End);
        }

        private static void ReadCallback(UvStreamHandle handle, int status, object state)
        {
            ((Connection)state).OnRead(handle, status);
        }

        private void OnRead(UvStreamHandle handle, int status)
        {
            if (status == 0)
            {
                // A zero status does not indicate an error or connection end. It indicates
                // there is no data to be read right now.
                // See the note at http://docs.libuv.org/en/v1.x/stream.html#c.uv_read_cb.
                // We need to clean up whatever was allocated by OnAlloc.
                SocketInput.IncomingDeferred();
                return;
            }

            var normalRead = status > 0;
            var normalDone = status == Constants.EOF;
            var errorDone = !(normalDone || normalRead);
            var readCount = normalRead ? status : 0;

            if (normalRead)
            {
                ServiceContext.Log.ConnectionRead(ConnectionId, readCount);
            }
            else
            {
                _socket.ReadStop();

                if (normalDone)
                {
                    ServiceContext.Log.ConnectionReadFin(ConnectionId);
                }
            }

            IOException error = null;
            if (errorDone)
            {
                Exception uvError;
                handle.Libuv.Check(status, out uvError);
                ServiceContext.Log.ConnectionError(ConnectionId, uvError);
                error = new IOException(uvError.Message, uvError);
            }

            SocketInput.IncomingComplete(readCount, error);

            if (errorDone)
            {
                Abort(error);
            }
        }

        void IConnectionControl.Pause()
        {
            ServiceContext.Log.ConnectionPause(ConnectionId);
            _socket.ReadStop();
        }

        void IConnectionControl.Resume()
        {
            ServiceContext.Log.ConnectionResume(ConnectionId);
            try
            {
                _socket.ReadStart(_allocCallback, _readCallback, this);
            }
            catch (UvException)
            {
                // ReadStart() can throw a UvException in some cases (e.g. socket is no longer connected).
                // This should be treated the same as OnRead() seeing a "normalDone" condition.
                ServiceContext.Log.ConnectionReadFin(ConnectionId);
                SocketInput.IncomingComplete(0, null);
            }
        }

        void IConnectionControl.End(ProduceEndType endType)
        {
            switch (endType)
            {
                case ProduceEndType.ConnectionKeepAlive:
                    ServiceContext.Log.ConnectionKeepAlive(ConnectionId);
                    break;
                case ProduceEndType.SocketShutdown:
                case ProduceEndType.SocketDisconnect:
                    ServiceContext.Log.ConnectionDisconnect(ConnectionId);
                    ((SocketOutput)SocketOutput).End(endType);
                    break;
            }
        }

        void IConnectionControl.SetTimeout(long milliseconds)
        {
            Debug.Assert(_timeoutTimestamp == long.MaxValue, "Concurrent timeouts are not supported");

            // Add KestrelThread.HeartbeatMilliseconds extra milliseconds since this can be called right before the next heartbeat.
            Interlocked.Exchange(ref _timeoutTimestamp, _lastTimestamp + milliseconds + KestrelThread.HeartbeatMilliseconds);
        }

        void IConnectionControl.CancelTimeout()
        {
            Interlocked.Exchange(ref _timeoutTimestamp, long.MaxValue);
        }

        private static unsafe string GenerateConnectionId(long id)
        {
            // The following routine is ~310% faster than calling long.ToString() on x64
            // and ~600% faster than calling long.ToString() on x86 in tight loops of 1 million+ iterations
            // See: https://github.com/aspnet/Hosting/pull/385

            // stackalloc to allocate array on stack rather than heap
            char* charBuffer = stackalloc char[13];

            charBuffer[0] = _encode32Chars[(int)(id >> 60) & 31];
            charBuffer[1] = _encode32Chars[(int)(id >> 55) & 31];
            charBuffer[2] = _encode32Chars[(int)(id >> 50) & 31];
            charBuffer[3] = _encode32Chars[(int)(id >> 45) & 31];
            charBuffer[4] = _encode32Chars[(int)(id >> 40) & 31];
            charBuffer[5] = _encode32Chars[(int)(id >> 35) & 31];
            charBuffer[6] = _encode32Chars[(int)(id >> 30) & 31];
            charBuffer[7] = _encode32Chars[(int)(id >> 25) & 31];
            charBuffer[8] = _encode32Chars[(int)(id >> 20) & 31];
            charBuffer[9] = _encode32Chars[(int)(id >> 15) & 31];
            charBuffer[10] = _encode32Chars[(int)(id >> 10) & 31];
            charBuffer[11] = _encode32Chars[(int)(id >> 5) & 31];
            charBuffer[12] = _encode32Chars[(int)id & 31];

            // string ctor overload that takes char*
            return new string(charBuffer, 0, 13);
        }
    }
}
