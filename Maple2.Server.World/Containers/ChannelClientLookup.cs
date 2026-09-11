using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using Grpc.Core;
using Grpc.Health.V1;
using Grpc.Net.Client;
using Maple2.Database.Extensions;
using Maple2.Model.Game;
using Maple2.Server.Core.Constants;
using Serilog;
using ChannelClient = Maple2.Server.Channel.Service.Channel.ChannelClient;

namespace Maple2.Server.World.Containers;

public class ChannelClientLookup : IEnumerable<(int, ChannelClient)>, IDisposable {
#if DEBUG
    private static readonly TimeSpan MonitorInterval = TimeSpan.FromSeconds(1);
#else
    private static readonly TimeSpan MonitorInterval = TimeSpan.FromSeconds(5);
#endif
    private static readonly TimeSpan RpcTimeout = TimeSpan.FromSeconds(5);
    private const int MaxNormalChannels = 100;

    private WorldServer worldServer = null!;
    private PlayerInfoLookup playerInfoLookup = null!;

    private enum ChannelStatus {
        Active,
        Inactive,
        Pending,
    }

    public void InjectDependencies(WorldServer worldSv, PlayerInfoLookup playerInfo) {
        worldServer = worldSv;
        playerInfoLookup = playerInfo;
    }

    private readonly ConcurrentDictionary<int, Channel> channels = [];
    // ponytail: serialize registration/status changes; split per slot only if channel churn warrants it.
    private readonly object sync = new();
    private bool disposed;

    private readonly ILogger logger = Log.ForContext<ChannelClientLookup>();

    public int Count => channels.Values.Count(ch => ch is { InstancedContent: false, Status: ChannelStatus.Active });

    public IEnumerable<int> Keys {
        get {
            foreach (Channel channel in channels.Values.Where(ch => ch is { InstancedContent: false, Status: ChannelStatus.Active })) {
                yield return channel.Id;
            }
        }
    }

    public (ushort gamePort, int grpcPort, int channel) FindOrCreateChannelByIp(string gameIp, string grpcGameIp, bool instancedContent) {
        if (!IPAddress.TryParse(gameIp, out IPAddress? gameAddress) || Uri.CheckHostName(grpcGameIp) is UriHostNameType.Unknown) {
            logger.Warning("Invalid channel registration: game IP {GameIp}, gRPC host {GrpcGameIp}", gameIp, grpcGameIp);
            throw new RpcException(new Status(StatusCode.InvalidArgument, "A game IP address and a gRPC hostname or IP address are required."));
        }

        string grpcHost = new UriBuilder(Uri.UriSchemeHttp, grpcGameIp).Uri.IdnHost;
        Channel? previous;
        Channel channel;
        lock (sync) {
            if (disposed) {
                throw new RpcException(new Status(StatusCode.Unavailable, "World channel registry is shutting down."));
            }

            previous = channels.Values.FirstOrDefault(entry =>
                entry.Endpoint.Address.Equals(gameAddress) &&
                string.Equals(entry.GrpcHost, grpcHost, StringComparison.OrdinalIgnoreCase) &&
                entry.InstancedContent == instancedContent);

            int channelId = previous?.Id ?? (instancedContent ? 0 : 1);
            if (previous is null) {
                while (!instancedContent && channelId <= MaxNormalChannels && channels.ContainsKey(channelId)) {
                    channelId++;
                }
                if (channels.ContainsKey(channelId) || channelId > MaxNormalChannels) {
                    logger.Error("No channel slot available for gRPC host {GrpcHost} (instanced: {InstancedContent})", grpcHost, instancedContent);
                    throw new RpcException(new Status(instancedContent ? StatusCode.AlreadyExists : StatusCode.ResourceExhausted,
                        instancedContent ? "Instanced channel ID 0 is registered to another endpoint." : "No game channel slots available."));
                }
            }

            ushort gamePort = previous?.GamePort ?? (ushort) (Target.BaseGamePort + channelId);
            int grpcPort = previous?.GrpcPort ?? Target.BaseGrpcChannelPort + channelId;
            channel = new Channel(channelId, instancedContent, new IPEndPoint(gameAddress, gamePort),
                new UriBuilder(Uri.UriSchemeHttp, grpcHost, grpcPort).Uri);

            PlayerInfo[] offlinePlayers = previous is null ? [] : TakePlayersOffline(previous.Id);
            if (previous is not null) {
                previous.Status = ChannelStatus.Inactive;
                logger.Information("Replacing registration for channel {Channel} at {GrpcHost}, retaining ports {GamePort}/{GrpcPort}",
                    channelId, grpcHost, gamePort, grpcPort);
            }
            channels[channelId] = channel;
            channel.MonitorTask = Task.Run(() => MonitorChannel(channel, offlinePlayers));
        }

        previous?.Dispose();
        return (channel.GamePort, channel.GrpcPort, channel.Id);
    }

    /// Gets the ID of the first active, non-instanced content channel.
    /// <returns>
    /// The channel ID if an active, non-instanced content channel is found; otherwise returns -1.
    /// </returns>
    public int FirstChannel() {
        foreach (Channel channel in channels.Values.Where(ch => ch.Status is ChannelStatus.Active && !ch.InstancedContent)) {
            return channel.Id;
        }

        return -1;
    }

    public bool TryGetInstancedChannelId(out int channelId) {
        foreach (Channel channel in channels.Values.Where(ch => ch.Status is ChannelStatus.Active && ch.InstancedContent)) {
            channelId = channel.Id;
            return true;
        }

        channelId = -1;
        return false;
    }

    public bool ValidChannel(int channel) {
        return channels.TryGetValue(channel, out Channel? entry) && entry.Status is ChannelStatus.Active;
    }

    public bool TryGetClient(int channel, [NotNullWhen(true)] out ChannelClient? client) {
        if (!channels.TryGetValue(channel, out Channel? entry) || entry.Status is not ChannelStatus.Active) {
            client = null;
            return false;
        }

        client = entry.Client;
        return true;
    }

    public bool TryGetActiveEndpoint(int channelId, [NotNullWhen(true)] out IPEndPoint? endpoint) {
        if (!channels.TryGetValue(channelId, out Channel? channel) || channel.Status is not ChannelStatus.Active) {
            endpoint = null;
            return false;
        }

        endpoint = channel.Endpoint;
        return true;
    }

    private bool IsCurrent(Channel channel) {
        lock (sync) {
            return !disposed && channels.TryGetValue(channel.Id, out Channel? current) && ReferenceEquals(current, channel);
        }
    }

    private async Task MonitorChannel(Channel channel, PlayerInfo[] offlinePlayers) {
        CancellationToken cancellationToken = channel.CancellationToken;
        logger.Information("Begin monitoring game channel: {Channel} for {EndPoint}", channel.Id, channel.Endpoint);

        try {
            UpdateAllPlayersToOffline(channel, offlinePlayers);
            UpdateChannels(channel);
            while (!cancellationToken.IsCancellationRequested && IsCurrent(channel)) {
                try {
                    using AsyncUnaryCall<HealthCheckResponse> check = channel.Health.CheckAsync(new HealthCheckRequest(),
                        deadline: DateTime.UtcNow.Add(RpcTimeout), cancellationToken: cancellationToken);
                    HealthCheckResponse response = await check.ResponseAsync;
                    if (response.Status is HealthCheckResponse.Types.ServingStatus.Serving) {
                        Active(channel);
                    } else {
                        Inactive(channel);
                    }
                } catch (RpcException ex) when (!cancellationToken.IsCancellationRequested) {
                    if (ex.StatusCode != StatusCode.Unavailable) {
                        logger.Warning(ex, "Health check failed for channel {Channel}", channel.Id);
                    }
                    Inactive(channel);
                }

                await Task.Delay(MonitorInterval, cancellationToken);
            }
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
        } catch (RpcException) when (cancellationToken.IsCancellationRequested) {
        } catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) {
        } catch (Exception ex) {
            logger.Error(ex, "Monitor failed for channel {Channel}", channel.Id);
            Inactive(channel);
        } finally {
            // Inactive slots remain reserved for the same endpoint; a retired monitor never removes its replacement.
            channel.Dispose();
            logger.Information("End monitoring game channel: {Channel} for {EndPoint}", channel.Id, channel.Endpoint);
        }
    }

    public IEnumerator<(int, ChannelClient)> GetEnumerator() {
        foreach (Channel channel in channels.Values.Where(ch => ch.Status is ChannelStatus.Active)) {
            yield return (channel.Id, channel.Client);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() {
        return GetEnumerator();
    }

    private void Inactive(Channel channel) {
        PlayerInfo[] offlinePlayers;
        lock (sync) {
            if (!IsCurrent(channel) || channel.Status is ChannelStatus.Inactive) {
                return;
            }
            channel.Status = ChannelStatus.Inactive;
            offlinePlayers = TakePlayersOffline(channel.Id);
            logger.Information("Channel {Channel} has become inactive", channel.Id);
        }
        UpdateAllPlayersToOffline(channel, offlinePlayers);
        UpdateChannels(channel);
    }

    private void Active(Channel channel) {
        lock (sync) {
            if (!IsCurrent(channel) || channel.Status is ChannelStatus.Active) {
                return;
            }
            channel.Status = ChannelStatus.Active;
            logger.Information("Channel {Channel} has become active", channel.Id);
        }
        UpdateChannels(channel);
        // Load custom string boards
        foreach ((int id, string message) in worldServer.GetCustomStringBoards()) {
            Notify(channel, channel, (client, options) => client.Admin(new AdminRequest {
                AddStringBoard = new AdminRequest.Types.AddStringBoard {
                    Id = id,
                    Message = message,
                },
            }, options));
        }
    }

    private PlayerInfo[] TakePlayersOffline(int channelId) {
        PlayerInfo[] players = playerInfoLookup.GetPlayersOnChannel(channelId);
        foreach (PlayerInfo player in players) {
            player.Channel = -1;
        }
        return players;
    }

    private void UpdateAllPlayersToOffline(Channel source, PlayerInfo[] offlinePlayers) {
        foreach (PlayerInfo playerInfo in offlinePlayers) {
            foreach (Channel channel in channels.Values) {
                if (channel.Id == source.Id) {
                    continue;
                }
                Notify(source, channel, (client, options) => client.UpdatePlayer(new PlayerUpdateRequest {
                    AccountId = playerInfo.AccountId,
                    CharacterId = playerInfo.CharacterId,
                    LastOnlineTime = DateTime.UtcNow.ToEpochSeconds(),
                    Channel = -1,
                    Async = true,
                }, options));
            }
        }
    }

    private void UpdateChannels(Channel source) {
        foreach (Channel channel in channels.Values) {
            if (channel.Id == source.Id) {
                continue;
            }
            Notify(source, channel, (client, options) => client.UpdateChannels(new Maple2.Server.Channel.Service.ChannelsUpdateRequest {
                Channels = {
                    Keys,
                },
            }, options));
        }
    }

    private void Notify(Channel source, Channel target, Action<ChannelClient, CallOptions> notify) {
        if (!IsCurrent(source) || !IsCurrent(target) || target.Status is not ChannelStatus.Active || source.CancellationToken.IsCancellationRequested) {
            return;
        }
        try {
            notify(target.Client, new CallOptions(deadline: DateTime.UtcNow.Add(RpcTimeout), cancellationToken: source.CancellationToken));
        } catch (RpcException ex) {
            if (!source.CancellationToken.IsCancellationRequested) {
                logger.Warning(ex, "Failed to notify channel {Channel} about channel {SourceChannel}", target.Id, source.Id);
            }
        } catch (ObjectDisposedException) {
            logger.Debug("Channel {Channel} was replaced during a notification from channel {SourceChannel}", target.Id, source.Id);
        }
    }

    public void Dispose() {
        Channel[] snapshot;
        lock (sync) {
            if (disposed) {
                return;
            }
            disposed = true;
            snapshot = channels.Values.ToArray();
            foreach (Channel channel in snapshot) {
                channel.Status = ChannelStatus.Inactive;
            }
            channels.Clear();
        }
        foreach (Channel channel in snapshot) {
            channel.Dispose();
        }
    }

    private sealed class Channel : IDisposable {
        public volatile ChannelStatus Status = ChannelStatus.Pending;

        public readonly int Id;
        public readonly bool InstancedContent;

        public readonly IPEndPoint Endpoint;
        public readonly ushort GamePort;
        public readonly int GrpcPort;
        public readonly string GrpcHost;

        public readonly GrpcChannel Transport;
        public readonly ChannelClient Client;
        public readonly Health.HealthClient Health;
        private readonly CancellationTokenSource cancellation = new();
        public readonly CancellationToken CancellationToken;
        public Task MonitorTask = Task.CompletedTask;
        private int disposed;

        public Channel(int id, bool instancedContent, IPEndPoint endpoint, Uri grpcUri) {
            Id = id;
            InstancedContent = instancedContent;
            Endpoint = endpoint;
            GamePort = (ushort) endpoint.Port;
            GrpcPort = grpcUri.Port;
            GrpcHost = grpcUri.IdnHost;
            Transport = GrpcChannel.ForAddress(grpcUri);
            Client = new ChannelClient(Transport);
            Health = new Health.HealthClient(Transport);
            CancellationToken = cancellation.Token;
        }

        public void Dispose() {
            if (Interlocked.Exchange(ref disposed, 1) != 0) {
                return;
            }
            cancellation.Cancel();
            Transport.Dispose();
            cancellation.Dispose();
        }
    }
}
