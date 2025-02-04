﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DotNetty.Codecs;
using DotNetty.Handlers.Timeout;
using DotNetty.Handlers.Tls;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using DotNetty.Transport.Libuv;
using KhaosLog.NettyProvider.Handlers;
using Microsoft.Extensions.Logging;
using OpenNetQ.Logging;
using OpenNetQ.Remoting.Abstractions;
using OpenNetQ.Remoting.Common;
using OpenNetQ.Remoting.Netty.Abstractions;
using OpenNetQ.Remoting.Netty.Handlers;
using OpenNetQ.Remoting.Protocol;
using OpenNetQ.TaskSchedulers;
using Timer = System.Timers.Timer;

namespace OpenNetQ.Remoting.Netty
{
    public class NettyRemotingServer:AbstractNettyRemoting,IRemotingServer
    {
        private static readonly ILogger<NettyRemotingServer> _logger = OpenNetQLoggerFactory.CreateLogger<NettyRemotingServer>();
        private readonly RemotingServerOption _option;
        private readonly bool _useTls;
        private readonly OpenNetQTaskScheduler _serverCallbackSchedluer;
        private readonly Timer _timer = new Timer(3000)
        {
            AutoReset = true,
            Enabled = true
        };

        // 主工作线程组，设置为1个线程
        private IEventLoopGroup bossGroup;

        // 工作线程组，默认为内核数*2的线程数
        private IEventLoopGroup workerGroup;

        /// <summary>
        /// 服务启动
        /// </summary>
        private ServerBootstrap bootstrap;

        private MessagePackEncoder _encoder;
        private MessagePackDecoder _decoder;
        private NettyServerConnectManagerHandler _connectManagerHandler;
        private NettyServerHandler _nettyServerHandler;
        public NettyRemotingServer(RemotingServerOption option) : base(option.PermitsOneway,option.PermitsAsync)
        {
            _option = option;
            _useTls = option.UseTls();
            //libuv
            var useLibuv = true;
            if (useLibuv)
            {
                var dispatcher = new DispatcherEventLoopGroup();
                bossGroup = dispatcher;
                workerGroup = new WorkerEventLoopGroup(dispatcher);
            }
            else
            {
                bossGroup = new MultithreadEventLoopGroup(1);
                workerGroup = new MultithreadEventLoopGroup();
            }

            var threads = Math.Max(4,_option.ServerCallbackExecutorThreads);
            _serverCallbackSchedluer = new OpenNetQTaskScheduler(threads, threads);
        }

        public override OpenNetQTaskScheduler GetCallbackExecutor()
        {
            return _serverCallbackSchedluer;
        }

        public async Task StartAsync()
        {
            InitSharableHandlers();
            //声明一个服务端Bootstrap，每个Netty服务端程序，都由ServerBootstrap控制，
            //通过链式的方式组装需要的参数
            bootstrap = new ServerBootstrap();
            var childHandler = bootstrap
                .Group(bossGroup, workerGroup) // 设置主和工作线程组
                .Channel<TcpServerChannel>() // 设置通道模式为TcpSocket Libuv
                // .Channel<TcpServerSocketChannel>() // 设置通道模式为TcpSocket
                .Option(ChannelOption.TcpNodelay, true)
                .Option(ChannelOption.SoReuseaddr, true)
                .Option(ChannelOption.SoBacklog, 100) // 看最下面解释
                .Option(ChannelOption.SoKeepalive, true) //保持连接
                .Option(ChannelOption.ConnectTimeout, TimeSpan.FromSeconds(3)) //连接超时
                .Option(ChannelOption.RcvbufAllocator, new AdaptiveRecvByteBufAllocator(1024, 1024, 65536))
                .ChildHandler(new ActionChannelInitializer<IChannel>(channel =>
                {
                    //工作线程连接器 是设置了一个管道，服务端主线程所有接收到的信息都会通过这个管道一层层往下传输
                    //同时所有出栈的消息 也要这个管道的所有处理器进行一步步处理
                    IChannelPipeline pipeline = channel.Pipeline;
                    if (_useTls)
                    {
                        pipeline.AddLast("tls", TlsHandler.Server(_option.TlsCertificate));
                    }
                    // pipeline.AddLast("tls", new TlsHandler(stream => new SslStream(stream, true, (sender, certificate, chain, errors) => true), new ClientTlsSettings(_targetHost)));
                    pipeline.AddLast("framing-dec", new LengthFieldBasedFrameDecoder(int.MaxValue, 0, 4, 0, 4));
                    pipeline.AddLast(_decoder);
                    pipeline.AddLast("framing-enc", new LengthFieldPrepender(4, false));
                    //实体类编码器,心跳管理器,连接管理器
                    pipeline.AddLast(_encoder
                        , new IdleStateHandler(0, 0, _option.AllIdleTime),
                        _connectManagerHandler, _nettyServerHandler);
                }));
            try
            {
                _logger.LogInformation("OpenNetQ开始启动----------");
                // bootstrap绑定到指定端口的行为 就是服务端启动服务，同样的Serverbootstrap可以bind到多个端口
                await bootstrap.BindAsync(_option.Port);
            }
            catch (Exception ex)
            {
                _logger.LogInformation($"异常:{ex.Message}");
            }
            
            NettyEventExecutor.Start();
            this._timer.Elapsed += (sender, args) =>
            {
                try
                {
                    ScanResponseTables();
                }
                catch (Exception e)
                {
                    _logger.LogError(e,"ScanResponseTables exception");
                }
            };
            _timer.Start();
            _logger.LogInformation($"OpenNetQ启动完成监听端口:{_option.Port}----------");
        }

        private void InitSharableHandlers()
        {
            _encoder = new MessagePackEncoder();
            _decoder = new MessagePackDecoder();
            _connectManagerHandler = new NettyServerConnectManagerHandler();
            _connectManagerHandler.OnNettyEventTrigger += OnNettyEventTrigger;
            _nettyServerHandler = new NettyServerHandler();
            _nettyServerHandler.OnProcessMessageReceived += OnProcessMessageReceived;
        }
        

        public async Task StopAsync()
        {
            try
            {
                this._timer.Stop();
                _logger.LogInformation("OpenNetQ开始停止----------");
                await bossGroup.ShutdownGracefullyAsync(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1));
                await workerGroup.ShutdownGracefullyAsync(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1));
                _logger.LogInformation("OpenNetQ已停止----------");
                NettyEventExecutor.Shutdown();
            }
            catch (Exception e)
            {
                _logger.LogError(e,"NettyRemotingServer shutdown exception");
            }

            try
            {
                _serverCallbackSchedluer.Dispose();
            }
            catch (Exception e)
            {
                _logger.LogError(e,"NettyRemotingServer shutdown task scheduler exception");
            }
        }

        public void RegisterRPCHook(IRPCHook? hook)
        {
            if (hook != null && !RpcHooks.Contains(hook))
            {
                RpcHooks.Add(hook);
            }
        }

        public void RegisterProcessor(int requestCode, INettyRequestProcessor processor, OpenNetQTaskScheduler? scheduler)
        {
            ProcessorTables.Add(requestCode,(processor,scheduler??_serverCallbackSchedluer));
        }

        public void RegisterDefaultProcessor(INettyRequestProcessor processor, OpenNetQTaskScheduler scheduler)
        {
            DefaultRequestProcessor = (processor, scheduler);
        }


        public (INettyRequestProcessor, OpenNetQTaskScheduler)? GetProcessorPair(int requestCode)
        {
            if (ProcessorTables.TryGetValue(requestCode, out var r))
            {
                return r;
            }

            return null;
        }

        public Task<RemotingCommand> InvokeAsync(IChannel channel, RemotingCommand request, long timeoutMillis)
        {
            return InvokeSyncImpl(channel, request, timeoutMillis);
        }

        public Task InvokeCallbackAsync(IChannel channel, RemotingCommand request, long timeoutMillis, Action<ResponseTask> callback)
        {
            return InvokeCallbackAsync(channel, request, timeoutMillis, callback);
        }

        public Task InvokeOnewayAsync(IChannel channel, RemotingCommand request, long timeoutMillis)
        {
            return InvokeOnewayImpl(channel, request, timeoutMillis);
        }
    }
}
