﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dargon.Commons;
using Dargon.Commons.Pooling;
using Dargon.Courier.TransportTier.Udp.Vox;
using NLog;
using static Dargon.Commons.Channels.ChannelsExtensions;

namespace Dargon.Courier.TransportTier.Udp {
   public class MultiPartPacketReassembler {
      private static readonly Logger logger = LogManager.GetCurrentClassLogger();

      private static readonly TimeSpan kSomethingExpiration = TimeSpan.FromMinutes(5);

      private readonly ConcurrentDictionary<Guid, ChunkReassemblyContext> chunkReassemblerContextsByMessageId = new ConcurrentDictionary<Guid, ChunkReassemblyContext>();
      private readonly IObjectPool<InboundDataEvent> inboundDataEventPool = ObjectPool.CreateConcurrentQueueBacked(() => new InboundDataEvent());
      private UdpDispatcher dispatcher;

      public void SetUdpDispatcher(UdpDispatcher dispatcher) {
         this.dispatcher = dispatcher;
      }

      public void HandleInboundMultiPartChunk(MultiPartChunkDto chunk) {
         bool isAdded = false;
         var chunkReassemblyContext = chunkReassemblerContextsByMessageId.GetOrAdd(
            chunk.MultiPartMessageId,
            add => {
//               logger.Info(Thread.CurrentThread.ManagedThreadId + ": " + "NEW " + chunk.MultiPartMessageId + " " + this.GetHashCode());

               isAdded = true;
               return new ChunkReassemblyContext(chunk.ChunkCount);
            });

//         if (isAdded) {
//            logger.Info(Thread.CurrentThread.ManagedThreadId + ": " + chunkReassemblyContext.GetHashCode() + " " + new ChunkReassemblyContext(0).GetHashCode() + "");
//         }

         if (isAdded) {
            Go(async () => {
               await Task.Delay(kSomethingExpiration).ConfigureAwait(false);

               RemoveAssemblerFromCache(chunk.MultiPartMessageId);
            });
         }

         var completedChunks = chunkReassemblyContext.AddChunk(chunk);
         if (completedChunks != null) {
            ReassembleChunksAndDispatch(completedChunks);
         }
      }

      private void ReassembleChunksAndDispatch(IReadOnlyList<MultiPartChunkDto> chunks) {
         Console.Title = ("Got to reassemble!");
         RemoveAssemblerFromCache(chunks.First().MultiPartMessageId);

         var payloadLength = chunks.Sum(c => c.BodyLength);
         var payloadBytes = new byte[payloadLength];
         for (int i = 0, offset = 0; i < chunks.Count; i++) {
            var chunk = chunks[i];
            Buffer.BlockCopy(chunk.Body, 0, payloadBytes, offset, chunk.BodyLength);
            offset += chunk.BodyLength;
         }

         Go(async () => {
            var e = inboundDataEventPool.TakeObject();
            e.Data = payloadBytes;

            await dispatcher.HandleInboundDataEventAsync(e).ConfigureAwait(false);

            e.Data = null;
            inboundDataEventPool.ReturnObject(e);
         }).Forget();
      }

      private void RemoveAssemblerFromCache(Guid multiPartMessageId) {
         ChunkReassemblyContext throwaway;
         chunkReassemblerContextsByMessageId.TryRemove(multiPartMessageId, out throwaway);
      }
   }

   public class ChunkReassemblyContext {
      private readonly MultiPartChunkDto[] x;
      private int chunksRemaining;

      public ChunkReassemblyContext(int chunkCount) {
         x = new MultiPartChunkDto[chunkCount];
         chunksRemaining = chunkCount;
      }

      public MultiPartChunkDto[] AddChunk(MultiPartChunkDto chunk) {
         if (Interlocked.CompareExchange(ref x[chunk.ChunkIndex], chunk, null) == null) {
            var newChunksRemaining = Interlocked.Decrement(ref chunksRemaining);
            if (newChunksRemaining == 0) {
               return x;
            }
         }
         return null;
      }
   }
}
