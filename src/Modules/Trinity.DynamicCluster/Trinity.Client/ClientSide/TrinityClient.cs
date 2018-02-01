﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Trinity.Client.ServerSide;
using Trinity.Configuration;
using Trinity.Core.Lib;
using Trinity.Daemon;
using Trinity.Diagnostics;
using Trinity.Network;
using Trinity.Network.Messaging;
using Trinity.Storage;
using Trinity.Utilities;

namespace Trinity.Client
{
    public class TrinityClient : CommunicationInstance, IMessagePassingEndpoint
    {
        private IClientConnectionFactory m_clientfactory = null;
        private IMessagePassingEndpoint m_client;
        private CancellationTokenSource m_tokensrc;
        private Task m_polltask;
        private readonly string m_endpoint;
        private int m_id;
        private int m_cookie;

        public TrinityClient(string endpoint)
            : this(endpoint, null) { }

        public TrinityClient(string endpoint, IClientConnectionFactory clientConnectionFactory)
        {
            m_endpoint = endpoint;
            m_clientfactory = clientConnectionFactory;
            RegisterCommunicationModule<TrinityClientModule.TrinityClientModule>();
            ExtensionConfig.Instance.Priority.Add(new ExtensionPriority { Name = typeof(ClientMemoryCloud).AssemblyQualifiedName, Priority = int.MaxValue });
            ExtensionConfig.Instance.Priority.Add(new ExtensionPriority { Name = typeof(HostMemoryCloud).AssemblyQualifiedName, Priority = int.MinValue });
            ExtensionConfig.Instance.Priority = ExtensionConfig.Instance.Priority; // trigger update of priority table
        }

        protected override sealed RunningMode RunningMode => RunningMode.Client;

        public unsafe void SendMessage(byte* message, int size)
            => m_client.SendMessage(message, size);

        public unsafe void SendMessage(byte* message, int size, out TrinityResponse response)
            => m_client.SendMessage(message, size, out response);

        public unsafe void SendMessage(byte** message, int* sizes, int count)
            => m_client.SendMessage(message, sizes, count);

        public unsafe void SendMessage(byte** message, int* sizes, int count, out TrinityResponse response)
            => m_client.SendMessage(message, sizes, count, out response);

        protected override sealed void DispatchHttpRequest(HttpListenerContext ctx, string handlerName, string url)
            => throw new NotSupportedException();

        protected override sealed void RootHttpHandler(HttpListenerContext ctx)
            => throw new NotSupportedException();

        protected override void StartCommunicationListeners()
        {
            if (m_clientfactory == null) { ScanClientConnectionFactory(); }
            m_client = m_clientfactory.ConnectAsync(m_endpoint, this).Result;
            ClientMemoryCloud.BeginInitialize(m_client, this);
            this.Started += OnStarted;
        }

        private void OnStarted()
        {
            ClientMemoryCloud.EndInitialize();
            m_tokensrc = new CancellationTokenSource();
            m_id = Global.CloudStorage.MyInstanceId;
            m_cookie = GetCommunicationModule<TrinityClientModule.TrinityClientModule>().MyCookie;
            m_polltask = PollProc(m_tokensrc.Token);
        }

        private async Task PollProc(CancellationToken token)
        {
            TrinityMessage poll_req = _AllocPollMsg(m_id, m_cookie);
            while (!token.IsCancellationRequested)
            {
                try
                {
                    _PollImpl(poll_req);
                    await Task.Delay(100);
                }
                catch (Exception ex)
                {
                    Log.WriteLine(LogLevel.Error, $"{nameof(TrinityClient)}: error occured during polling: {{0}}", ex.ToString());
                }
            }
            poll_req.Dispose();
        }

        private unsafe void _PollImpl(TrinityMessage poll_req)
        {
            m_client.SendMessage(poll_req, out var poll_rsp);
            var sp = PointerHelper.New(poll_rsp.Buffer + poll_rsp.Offset);
            var payload_len = poll_rsp.Size - TrinityProtocol.TrinityMsgHeader;
            if (payload_len < sizeof(long) + sizeof(int)) { throw new IOException("Poll response corrupted."); }
            var pctx = *sp.lp++;
            var msg_len = *sp.ip++;
            if (msg_len < 0) return; // no events
            MessageBuff msg_buff = new MessageBuff{ Buffer = sp.bp, BytesReceived = (uint)msg_len };
            MessageDispatcher(&msg_buff);
            poll_rsp.Dispose();
            // !Note, void-response messages are not acknowledged. 
            // Server would not be aware of client side error in this case.
            // This is by-design and an optimization to reduce void-response
            // message delivery latency. In streaming use cases this will be
            // very useful.
            if (pctx != 0) _PostResponseImpl(pctx, &msg_buff);
            Memory.free(msg_buff.Buffer);
        }

        private unsafe void _PostResponseImpl(long pctx, MessageBuff* messageBuff)
        {
            int msglen                                      = TrinityProtocol.MsgHeader + sizeof(int) + sizeof(int) + sizeof(long) + (int)messageBuff->BytesToSend;
            byte* buf                                       = (byte*)Memory.malloc((ulong)msglen);
            PointerHelper sp                                = PointerHelper.New(buf);
            *sp.ip                                          = msglen - TrinityProtocol.SocketMsgHeader;
            *(sp.bp + TrinityProtocol.MsgTypeOffset)        = (byte)TrinityMessageType.SYNC;
            *(ushort*)(sp.bp + TrinityProtocol.MsgIdOffset) = (ushort)TSL.CommunicationModule.TrinityClientModule.SynReqMessageType.PostResponse;
            sp.bp                                          += TrinityProtocol.MsgHeader;
            *sp.ip++                                        = m_id;
            *sp.ip++                                        = m_cookie;
            *sp.lp++                                        = pctx;
            Memory.memcpy(sp.bp, messageBuff->Buffer, messageBuff->BytesToSend);
            TrinityMessage post_msg = new TrinityMessage(buf, msglen);
            try { m_client.SendMessage(post_msg); }
            finally { post_msg.Dispose(); }
        }

        private unsafe TrinityMessage _AllocPollMsg(int myInstanceId, int myCookie)
        {
            int msglen                                      = sizeof(int) + sizeof(int) + TrinityProtocol.MsgHeader;
            byte* buf                                       = (byte*)Memory.malloc((ulong)msglen);
            PointerHelper sp                                = PointerHelper.New(buf);
            *sp.ip                                          = msglen - TrinityProtocol.SocketMsgHeader;
            *(sp.bp + TrinityProtocol.MsgTypeOffset)        = (byte)TrinityMessageType.SYNC_WITH_RSP;
            *(ushort*)(sp.bp + TrinityProtocol.MsgIdOffset) = (ushort)TSL.CommunicationModule.TrinityClientModule.SynReqRspMessageType.PollEvents;
            sp.bp                                          += TrinityProtocol.MsgHeader;
            *sp.ip++                                        = myInstanceId;
            *sp.ip++                                        = myCookie;
            return new TrinityMessage(buf, msglen);
        }

        private void ScanClientConnectionFactory()
        {
            Log.WriteLine(LogLevel.Info, $"{nameof(TrinityClient)}: scanning for client connection factory.");
            var rank = ExtensionConfig.Instance.ResolveTypePriorities();
            Func<Type, int> rank_func = t =>
            {
                if(rank.TryGetValue(t, out var r)) return r;
                else return 0;
            };
            m_clientfactory = AssemblyUtility.GetBestClassInstance<IClientConnectionFactory, DefaultClientConnectionFactory>(null, rank_func);
        }

        protected override void StopCommunicationListeners()
        {
            m_tokensrc.Cancel();
            m_polltask.Wait();
            m_polltask = null;
            m_clientfactory.DisconnectAsync(m_client).Wait();
        }
    }
}