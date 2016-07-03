﻿using Dargon.Commons;
using Dargon.Commons.Pooling;
using Dargon.Courier.AuditingTier;
using Dargon.Courier.TransportTier.Udp.Vox;
using Dargon.Vox;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dargon.Courier.AsyncPrimitives;
using Dargon.Courier.Vox;
using Nito.AsyncEx;
using static Dargon.Commons.Channels.ChannelsExtensions;

namespace Dargon.Courier.TransportTier.Udp {
   public class PacketSender {
      private readonly PayloadSender payloadSender;
      private readonly AcknowledgementCoordinator acknowledgementCoordinator;
      private readonly CancellationToken shutdownCancellationToken;
      private readonly AuditAggregator<int> resendsAggregator;
      private readonly IObjectPool<MemoryStream> outboundMemoryStreamPool = ObjectPool.CreateStackBacked(() => new MemoryStream(new byte[UdpConstants.kMaximumTransportSize], 0, UdpConstants.kMaximumTransportSize, true, true));
      private readonly UdpClient udpClient;
      private readonly AuditCounter multiPartChunksSentCounter;

      public PacketSender(PayloadSender payloadSender, AcknowledgementCoordinator acknowledgementCoordinator, CancellationToken shutdownCancellationToken, AuditAggregator<int> resendsAggregator, UdpClient udpClient, AuditCounter multiPartChunksSentCounter) {
         this.payloadSender = payloadSender;
         this.acknowledgementCoordinator = acknowledgementCoordinator;
         this.shutdownCancellationToken = shutdownCancellationToken;
         this.resendsAggregator = resendsAggregator;
         this.udpClient = udpClient;
         this.multiPartChunksSentCounter = multiPartChunksSentCounter;
      }

      public async Task SendAsync(PacketDto x) {
         await TaskEx.YieldToThreadPool();

         var ms = outboundMemoryStreamPool.TakeObject();

         bool isMultiPartPacket = false;
         try {
            Serialize.To(ms, x);
         } catch (NotSupportedException) {
            // surpassed memory stream size limit - send a large message.
            isMultiPartPacket = true;
         }

         if (isMultiPartPacket) {
            Interlocked.Increment(ref DebugRuntimeStats.out_mpp);
            await SendMultiPartAsync(x).ConfigureAwait(false);
            Interlocked.Increment(ref DebugRuntimeStats.out_mpp_done);
         } else {
            if (!x.IsReliable()) {
               Interlocked.Increment(ref DebugRuntimeStats.out_nrs);
               await payloadSender.SendAsync(x).ConfigureAwait(false);
               Interlocked.Increment(ref DebugRuntimeStats.out_nrs_done);
            } else {
               using (var acknowledgedCts = new CancellationTokenSource())
               using (var acknowledgedOrShutdownCts = CancellationTokenSource.CreateLinkedTokenSource(acknowledgedCts.Token, shutdownCancellationToken)) {
                  Interlocked.Increment(ref DebugRuntimeStats.out_rs);
                  var expectation = acknowledgementCoordinator.Expect(x.Id, shutdownCancellationToken);
                  var expectationCancelledAcknowledgeSignal = new AsyncLatch();
                  Go(async () => {
                     await expectation.ConfigureAwait(false);
                     acknowledgedCts.Cancel();
                     expectationCancelledAcknowledgeSignal.Set();
                  }).Forget();

                  const int resendDelayBase = 500000;
                  int sendCount = 0;
                  while (!expectation.IsCompleted && !shutdownCancellationToken.IsCancellationRequested) {
                     Interlocked.Increment(ref DebugRuntimeStats.out_sent);
                     try {
                        sendCount++;
                        await payloadSender.SendAsync(x).ConfigureAwait(false);

                        var resendDelay = Math.Min(8, (1 << (sendCount - 1))) * resendDelayBase;
                        await Task.Delay(resendDelay, acknowledgedCts.Token).ConfigureAwait(false);
                     } catch (TaskCanceledException) {
                        // It's on the Task.Delay
                     }
                  }
                  await expectationCancelledAcknowledgeSignal.WaitAsync().ConfigureAwait(false);
                  Interlocked.Increment(ref DebugRuntimeStats.out_rs_done);

                  resendsAggregator.Put(sendCount);
               }
            }
         }

         ms.SetLength(0);
         outboundMemoryStreamPool.ReturnObject(ms);
      }

      private async Task SendMultiPartAsync(PacketDto packet) {
         using (var ms = new MemoryStream()) {
            Serialize.To(ms, packet);
            var someLengthThing = ms.Position;
            var multiPartMessageId = Guid.NewGuid();

            var chunkCount = (int)((someLengthThing - 1) / UdpConstants.kMultiPartChunkSize + 1);
            var chunks = new List<MultiPartChunkDto>();
            for (var i = 0; i < chunkCount; i++) {
               var startIndexInclusive = UdpConstants.kMultiPartChunkSize * i;
               var endIndexExclusive = Math.Min(someLengthThing, startIndexInclusive + UdpConstants.kMultiPartChunkSize);
               var chunk = new MultiPartChunkDto {
                  Body = ms.GetBuffer(),
                  BodyLength = (int)(endIndexExclusive - startIndexInclusive),
                  BodyOffset = startIndexInclusive,
                  MultiPartMessageId = multiPartMessageId,
                  ChunkCount = chunkCount,
                  ChunkIndex = i
               };
               chunks.Add(chunk);
            }

            var packets = chunks.Map(chunk => new PacketDto {
               Id = Guid.NewGuid(),
               Flags = packet.Flags,
               Message = new MessageDto {
                  Body = chunk,
                  ReceiverId = packet.ReceiverId,
                  SenderId = packet.SenderId
               },
               ReceiverId = packet.ReceiverId,
               SenderId = packet.SenderId
            });

            const int kConcurrencyLimit = 32;
            var sema = new AsyncSemaphore(kConcurrencyLimit);
            for (var i = 0; i < packets.Length; i++) {
               await sema.WaitAsync(shutdownCancellationToken).ConfigureAwait(false);
               SendAsync(packets[i]).ContinueWith(
                  t => sema.Release(),
                  shutdownCancellationToken).Forget();
               multiPartChunksSentCounter.Increment();
            }
         }
      }
   }
}