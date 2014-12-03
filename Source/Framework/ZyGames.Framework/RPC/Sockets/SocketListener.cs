﻿/****************************************************************************
Copyright (c) 2013-2015 scutgame.com

http://www.scutgame.com

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
****************************************************************************/
using System;
using System.Net.Sockets;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using ZyGames.Framework.Common.Log;
using NLog;
using ZyGames.Framework.RPC.IO;

namespace ZyGames.Framework.RPC.Sockets
{
    /// <summary>
    /// Socket listener.
    /// </summary>
    public class SocketListener : ISocket
    {
        #region 事件
        /// <summary>
        /// 连接事件
        /// </summary>
        public event ConnectionEventHandler Connected;
        private void OnConnected(ConnectionEventArgs e)
        {
            if (Connected != null)
            {
                Connected(this, e);
            }
        }
        /// <summary>
        /// 握手事件
        /// </summary>
        public event ConnectionEventHandler Handshaked;
        private void OnHandshaked(ConnectionEventArgs e)
        {
            if (Handshaked != null)
            {
                Handshaked(this, e);
            }
        }
        /// <summary>
        /// 断开连接事件
        /// </summary>
        public event ConnectionEventHandler Disconnected;
        private void OnDisconnected(ConnectionEventArgs e)
        {
            if (Disconnected != null)
            {
                Disconnected(this, e);
            }
        }
        /// <summary>
        /// 接收到数据包事件
        /// </summary>
        public event ConnectionEventHandler DataReceived;
        private void OnDataReceived(ConnectionEventArgs e)
        {
            if (DataReceived != null)
            {
                DataReceived(this, e);
            }
        }
        /// <summary>
        /// 
        /// </summary>
        public event ConnectionEventHandler OnPing;

        private void DoPing(ConnectionEventArgs e)
        {
            ConnectionEventHandler handler = OnPing;
            if (handler != null) handler(this, e);
        }

        /// <summary>
        /// 
        /// </summary>
        public event ConnectionEventHandler OnPong;

        private void DoPong(ConnectionEventArgs e)
        {
            ConnectionEventHandler handler = OnPong;
            if (handler != null) handler(this, e);
        }

        #endregion

        private Logger logger = LogManager.GetLogger("SocketListener");
        private BufferManager bufferManager;
        private Socket listenSocket;
        private Semaphore maxConnectionsEnforcer;
        private SocketSettings socketSettings;
        /// <summary>
        /// 
        /// </summary>
        protected RequestHandler requestHandler;
        private ThreadSafeStack<SocketAsyncEventArgs> acceptEventArgsPool;
        private ThreadSafeStack<SocketAsyncEventArgs> ioEventArgsPool;
        //Timer expireTimer;
        private bool _isStart;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="socketSettings"></param>
        public SocketListener(SocketSettings socketSettings)
            : this(socketSettings, new RequestHandler(new MessageHandler()))
        {

        }

        /// <summary>
        /// Initializes a new instance
        /// </summary>
        /// <param name="socketSettings">Socket settings.</param>
        /// <param name="requestHandler"></param>
        public SocketListener(SocketSettings socketSettings, RequestHandler requestHandler)
        {
            this.socketSettings = socketSettings;
            this.requestHandler = requestHandler;
            this.bufferManager = new BufferManager(this.socketSettings.BufferSize * this.socketSettings.NumOfSaeaForRecSend, this.socketSettings.BufferSize);

            this.ioEventArgsPool = new ThreadSafeStack<SocketAsyncEventArgs>(socketSettings.NumOfSaeaForRecSend);
            this.acceptEventArgsPool = new ThreadSafeStack<SocketAsyncEventArgs>(socketSettings.MaxAcceptOps);
            this.maxConnectionsEnforcer = new Semaphore(this.socketSettings.MaxConnections, this.socketSettings.MaxConnections);
            Init();
        }


        private void Init()
        {
            this.bufferManager.InitBuffer();

            for (int i = 0; i < this.socketSettings.MaxAcceptOps; i++)
            {
                this.acceptEventArgsPool.Push(CreateAcceptEventArgs());
            }

            SocketAsyncEventArgs ioEventArgs;
            for (int i = 0; i < this.socketSettings.NumOfSaeaForRecSend; i++)
            {
                ioEventArgs = new SocketAsyncEventArgs();
                this.bufferManager.SetBuffer(ioEventArgs);
                ioEventArgs.Completed += new EventHandler<SocketAsyncEventArgs>(IO_Completed);
                DataToken dataToken = new DataToken();
                dataToken.bufferOffset = ioEventArgs.Offset;
                ioEventArgs.UserToken = dataToken;
                this.ioEventArgsPool.Push(ioEventArgs);
            }
        }

        //private void AddClient(ExSocket socket)
        //{
        //    lock (clientSockets)
        //    {
        //        clientSockets.Add(socket);
        //    }
        //}

        private SocketAsyncEventArgs CreateAcceptEventArgs()
        {
            SocketAsyncEventArgs acceptEventArg = new SocketAsyncEventArgs();
            acceptEventArg.Completed += new EventHandler<SocketAsyncEventArgs>(Accept_Completed);
            return acceptEventArg;
        }
        /// <summary>
        /// Starts the listen.
        /// </summary>
        public void StartListen()
        {
            listenSocket = new Socket(this.socketSettings.LocalEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            listenSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listenSocket.Bind(this.socketSettings.LocalEndPoint);
            listenSocket.Listen(socketSettings.Backlog);
            _isStart = true;
            requestHandler.Bind(this);
            PostAccept();
        }

        private void PostAccept()
        {
            SocketAsyncEventArgs acceptEventArgs;
            if (this.acceptEventArgsPool.Count > 1)
            {
                try
                {
                    acceptEventArgs = this.acceptEventArgsPool.Pop();
                }
                catch
                {
                    acceptEventArgs = CreateAcceptEventArgs();
                }
            }
            else
            {
                acceptEventArgs = CreateAcceptEventArgs();
            }

            this.maxConnectionsEnforcer.WaitOne();
            if (!_isStart)
            {
                return;
            }
            bool willRaiseEvent = listenSocket.AcceptAsync(acceptEventArgs);
            if (!willRaiseEvent)
            {
                ProcessAccept(acceptEventArgs);
            }
        }

        private void Accept_Completed(object sender, SocketAsyncEventArgs acceptEventArgs)
        {
            try
            {
                ProcessAccept(acceptEventArgs);
            }
            catch (Exception ex)
            {
                logger.Error(string.Format("AcceptCompleted method error:{0}", ex));

                if (acceptEventArgs.AcceptSocket != null)
                {
                    acceptEventArgs.AcceptSocket.Close();
                    acceptEventArgs.AcceptSocket = null;
                }
                acceptEventArgsPool.Push(acceptEventArgs);
                maxConnectionsEnforcer.Release();
            }
        }

        private void IO_Completed(object sender, SocketAsyncEventArgs ioEventArgs)
        {
            DataToken ioDataToken = (DataToken)ioEventArgs.UserToken;
            try
            {
                ioDataToken.Socket.LastAccessTime = DateTime.Now;
                switch (ioEventArgs.LastOperation)
                {
                    case SocketAsyncOperation.Receive:
                        ProcessReceive(ioEventArgs);
                        break;
                    case SocketAsyncOperation.Send:
                        ProcessSend(ioEventArgs);
                        break;

                    default:
                        throw new ArgumentException("The last operation completed on the socket was not a receive or send");
                }
            }
            catch (ObjectDisposedException)
            {
                //modify disposed error ignore log
                //logger.Error(string.Format("IO_Completed error:{0}", error));
                ReleaseIOEventArgs(ioEventArgs);
            }
            catch (Exception ex)
            {
                logger.Error(string.Format("IP {0} IO_Completed unkown error:{1}", (ioDataToken != null && ioDataToken.Socket != null ? ioDataToken.Socket.RemoteEndPoint.ToString() : ""), ex));
            }
        }

        private void ProcessAccept(SocketAsyncEventArgs acceptEventArgs)
        {
            PostAccept();

            if (acceptEventArgs.SocketError != SocketError.Success)
            {
                HandleBadAccept(acceptEventArgs);
                return;
            }

            SocketAsyncEventArgs ioEventArgs = this.ioEventArgsPool.Pop();
            ioEventArgs.AcceptSocket = acceptEventArgs.AcceptSocket;
            acceptEventArgs.AcceptSocket = null;
            this.acceptEventArgsPool.Push(acceptEventArgs);
            var dataToken = (DataToken)ioEventArgs.UserToken;
            ioEventArgs.SetBuffer(dataToken.bufferOffset, socketSettings.BufferSize);
            var exSocket = new ExSocket(ioEventArgs.AcceptSocket);
            exSocket.LastAccessTime = DateTime.Now;
            dataToken.Socket = exSocket;

            //AddClient(exSocket);

            try
            {
                OnConnected(new ConnectionEventArgs { Socket = exSocket });
            }
            catch (Exception ex)
            {
                TraceLog.WriteError("OnConnected error:{0}", ex);
            }

            PostReceive(ioEventArgs);
        }

        private void ReleaseIOEventArgs(SocketAsyncEventArgs ioEventArgs)
        {
            if (ioEventArgs == null) return;

            var dataToken = (DataToken)ioEventArgs.UserToken;
            if (dataToken != null)
            {
                dataToken.Reset(true);
                dataToken.Socket = null;
            }
            ioEventArgs.AcceptSocket = null;
            ioEventArgsPool.Push(ioEventArgs);
        }
        /// <summary>
        /// 投递接收数据请求
        /// </summary>
        /// <param name="ioEventArgs"></param>
        private void PostReceive(SocketAsyncEventArgs ioEventArgs)
        {
            bool willRaiseEvent = ioEventArgs.AcceptSocket.ReceiveAsync(ioEventArgs);

            if (!willRaiseEvent)
            {
                ProcessReceive(ioEventArgs);
            }
        }

        /// <summary>
        /// 处理数据接收回调
        /// </summary>
        /// <param name="ioEventArgs"></param>
        private void ProcessReceive(SocketAsyncEventArgs ioEventArgs)
        {
            DataToken dataToken = (DataToken)ioEventArgs.UserToken;
            if (ioEventArgs.SocketError != SocketError.Success)
            {
                //Socket错误
                logger.Error("IP {0} ProcessReceive:{1}", (dataToken != null ? dataToken.Socket.RemoteEndPoint.ToString() : ""), ioEventArgs.SocketError);
                Closing(ioEventArgs);
                return;
            }

            if (ioEventArgs.BytesTransferred == 0)
            {
                //对方主动关闭socket
                //if (logger.IsDebugEnabled) logger.Debug("对方关闭Socket");
                Closing(ioEventArgs);
                return;
            }

            var exSocket = dataToken.Socket;
            List<DataMeaage> messages;
            bool hasHandshaked;
            bool needPostAnother = requestHandler.TryReceiveMessage(ioEventArgs, out messages, out hasHandshaked);
            if (hasHandshaked)
            {
                OnHandshaked(new ConnectionEventArgs { Socket = exSocket });
            }

            //modify reason:数据包接收事件触发乱序
            if (messages != null)
            {
                foreach (var message in messages)
                {
                    try
                    {
                        switch (message.OpCode)
                        {
                            case OpCode.Close:
                                var statusCode = requestHandler.MessageProcessor != null
                                    ? requestHandler.MessageProcessor.GetCloseStatus(message.Data)
                                    : OpCode.Empty;
                                Closing(ioEventArgs, statusCode);
                                needPostAnother = false;
                                break;
                            case OpCode.Ping:
                                DoPing(new ConnectionEventArgs { Socket = exSocket, Meaage = message });
                                break;
                            case OpCode.Pong:
                                DoPong(new ConnectionEventArgs { Socket = exSocket, Meaage = message });
                                break;
                            default:
                                OnDataReceived(new ConnectionEventArgs { Socket = exSocket, Meaage = message });
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        TraceLog.WriteError("OnDataReceived error:{0}", ex);
                    }
                }
            }
            if (needPostAnother)
            {
                PostReceive(ioEventArgs);
                //是否需要关闭连接
                if (exSocket.IsClosed)
                {
                    ResetSAEAObject(ioEventArgs);
                }
            }
        }

        private void TryDequeueAndPostSend(ExSocket socket, SocketAsyncEventArgs ioEventArgs)
        {
            bool isOwner = ioEventArgs == null;
            byte[] data;
            if (socket.TryDequeue(out data))
            {
                if (ioEventArgs == null)
                {
                    ioEventArgs = ioEventArgsPool.Pop();
                    ioEventArgs.AcceptSocket = socket.WorkSocket;
                }
                DataToken dataToken = (DataToken)ioEventArgs.UserToken;
                dataToken.Socket = socket;
                dataToken.byteArrayForMessage = data;
                dataToken.messageLength = data.Length;
                try
                {
                    PostSend(ioEventArgs);
                }
                catch
                {
                    if (isOwner)
                        ReleaseIOEventArgs(ioEventArgs);
                    throw;
                }
            }
            else
            {
                ReleaseIOEventArgs(ioEventArgs);
                socket.ResetSendFlag();
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="socket"></param>
        /// <param name="data"></param>
        /// <param name="offset"></param>
        /// <param name="count"></param>
        public override void PostSend(ExSocket socket, byte[] data, int offset, int count)
        {
            PostSend(socket, OpCode.Binary, data, offset, count);
        }

        /// <summary>
        /// Posts the send.
        /// </summary>
        /// <param name="socket">Socket.</param>
        /// <param name="opCode"></param>
        /// <param name="data">Data.</param>
        /// <param name="offset">Offset.</param>
        /// <param name="count">Count.</param>
        public override void PostSend(ExSocket socket, sbyte opCode, byte[] data, int offset, int count)
        {
            byte[] buffer = requestHandler.MessageProcessor.BuildMessagePack(socket, opCode, data, offset, count);
            SendAsync(socket, buffer);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="socket"></param>
        public override void Ping(ExSocket socket)
        {
            byte[] data = Encoding.UTF8.GetBytes("ping");
            PostSend(socket, OpCode.Ping, data, 0, data.Length);
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="socket"></param>
        public override void Pong(ExSocket socket)
        {
            byte[] data = Encoding.UTF8.GetBytes("pong");
            PostSend(socket, OpCode.Pong, data, 0, data.Length);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="socket"></param>
        /// <param name="reason"></param>
        public override void CloseHandshake(ExSocket socket, string reason)
        {
            requestHandler.SendCloseHandshake(socket, OpCode.Close, reason);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="socket"></param>
        /// <param name="buffer"></param>
        internal protected override bool SendAsync(ExSocket socket, byte[] buffer)
        {
            socket.Enqueue(buffer);
            if (socket.TrySetSendFlag())
            {
                try
                {
                    TryDequeueAndPostSend(socket, null);
                    return true;
                }
                catch (Exception ex)
                {
                    socket.ResetSendFlag();
                    TraceLog.WriteError("SendAsync {0} error:{1}", socket.RemoteEndPoint, ex);
                }
            }
            return false;
        }

        private void PostSend(SocketAsyncEventArgs ioEventArgs)
        {
            DataToken dataToken = (DataToken)ioEventArgs.UserToken;
            if (dataToken.messageLength - dataToken.messageBytesDone <= this.socketSettings.BufferSize)
            {
                ioEventArgs.SetBuffer(dataToken.bufferOffset, dataToken.messageLength - dataToken.messageBytesDone);
                Buffer.BlockCopy(dataToken.byteArrayForMessage, dataToken.messageBytesDone, ioEventArgs.Buffer, dataToken.bufferOffset, dataToken.messageLength - dataToken.messageBytesDone);
            }
            else
            {
                ioEventArgs.SetBuffer(dataToken.bufferOffset, this.socketSettings.BufferSize);
                Buffer.BlockCopy(dataToken.byteArrayForMessage, dataToken.messageBytesDone, ioEventArgs.Buffer, dataToken.bufferOffset, this.socketSettings.BufferSize);
            }

            var willRaiseEvent = ioEventArgs.AcceptSocket.SendAsync(ioEventArgs);
            if (!willRaiseEvent)
            {
                ProcessSend(ioEventArgs);
            }
        }

        private void ProcessSend(SocketAsyncEventArgs ioEventArgs)
        {
            DataToken dataToken = (DataToken)ioEventArgs.UserToken;
            if (ioEventArgs.SocketError == SocketError.Success)
            {
                dataToken.messageBytesDone += ioEventArgs.BytesTransferred;
                if (dataToken.messageBytesDone != dataToken.messageLength)
                {
                    PostSend(ioEventArgs);
                }
                else
                {
                    dataToken.Reset(true);
                    try
                    {
                        TryDequeueAndPostSend(dataToken.Socket, ioEventArgs);
                    }
                    catch
                    {
                        dataToken.Socket.ResetSendFlag();
                        throw;
                    }
                }
            }
            else
            {
                dataToken.Socket.ResetSendFlag();
                Closing(ioEventArgs);
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="ioEventArgs"></param>
        /// <param name="opCode"></param>
        /// <param name="reason"></param>
        internal protected override void Closing(SocketAsyncEventArgs ioEventArgs, sbyte opCode = OpCode.Close, string reason = "")
        {
            bool needClose = true;
            var dataToken = (DataToken)ioEventArgs.UserToken;
            //lock (clientSockets)
            //{
            //    needClose = clientSockets.Remove(dataToken.Socket);
            //}
            try
            {
                if (opCode != OpCode.Empty)
                {
                    CloseHandshake(dataToken.Socket, reason);
                }
                if (ioEventArgs.AcceptSocket != null)
                {
                    ioEventArgs.AcceptSocket.Shutdown(SocketShutdown.Both);
                }
            }
            catch (Exception)
            {
                needClose = false;
            }

            if (needClose)
            {
                //logger.InfoFormat("Socket：{0}关闭，关闭原因：SocketError：{1}。", dataToken.Socket.RemoteEndPoint, ioEventArgs.SocketError);
                maxConnectionsEnforcer.Release();
                try
                {
                    OnDisconnected(new ConnectionEventArgs { Socket = dataToken.Socket });
                }
                catch (Exception ex)
                {
                    TraceLog.WriteError("OnDisconnected error:{0}", ex);
                }
                ResetSAEAObject(ioEventArgs);
            }
            ReleaseIOEventArgs(ioEventArgs);
        }

        private void HandleBadAccept(SocketAsyncEventArgs acceptEventArgs)
        {
            ResetSAEAObject(acceptEventArgs);
            acceptEventArgs.AcceptSocket = null;
            acceptEventArgsPool.Push(acceptEventArgs);
            maxConnectionsEnforcer.Release();
        }


        /// <summary>
        /// Close this instance.
        /// </summary>
        public void Dispose()
        {
            _isStart = false;
            DisposeAllSaeaObjects();
            listenSocket.Close();
        }

        private void DisposeAllSaeaObjects()
        {
            SocketAsyncEventArgs eventArgs;
            while (this.acceptEventArgsPool.Count > 0)
            {
                eventArgs = acceptEventArgsPool.Pop();
                ResetSAEAObject(eventArgs);
            }
            while (this.ioEventArgsPool.Count > 0)
            {
                eventArgs = ioEventArgsPool.Pop();
                ResetSAEAObject(eventArgs);
            }
        }

        private static void ResetSAEAObject(SocketAsyncEventArgs eventArgs)
        {
            try
            {
                if (eventArgs.AcceptSocket != null)
                {
                    eventArgs.AcceptSocket.Close();
                }
            }
            catch (Exception)
            {
            }
            eventArgs.AcceptSocket = null;
        }
    }
}