﻿using Dargon.PortableObjects;
using Dargon.Services.Server;
using Dargon.Services.Server.Phases;
using Dargon.Services.Server.Sessions;
using ItzWarty.Collections;
using ItzWarty.Networking;
using ItzWarty.Threading;
using NMockito;
using Xunit;

namespace Dargon.Services.Networking.Server.Phases {
   public class PhaseFactoryTests : NMockitoInstance {
      private readonly PhaseFactory testObj;

      [Mock] private readonly ICollectionFactory collectionFactory = null;
      [Mock] private readonly IThreadingProxy threadingProxy = null;
      [Mock] private readonly INetworkingProxy networkingProxy = null;
      [Mock] private readonly IHostSessionFactory hostSessionFactory = null;
      [Mock] private readonly IPofSerializer pofSerializer = null;
      [Mock] private readonly IServiceConfiguration serviceConfiguration = null;
      [Mock] private readonly IConnectorContext connectorContext = null;

      public PhaseFactoryTests() {
         testObj = new PhaseFactory(collectionFactory, threadingProxy, networkingProxy, hostSessionFactory, pofSerializer, serviceConfiguration, connectorContext);
      }

      [Fact]
      public void CreateIndeterminatePhaseTest() {
         var obj = testObj.CreateIndeterminatePhase();
         VerifyNoMoreInteractions();

         AssertTrue(obj is IndeterminatePhase);
      }

      [Fact]
      public void CreateHostPhaseTest() {
         var hostThread = CreateUntrackedMock<IThread>();
         var cancellationTokenSource = CreateUntrackedMock<ICancellationTokenSource>();
         When(threadingProxy.CreateThread(Any<ThreadEntryPoint>(), Any<ThreadCreationOptions>())).ThenReturn(hostThread);
         When(threadingProxy.CreateCancellationTokenSource()).ThenReturn(cancellationTokenSource);
         var listenerSocket = CreateMock<IListenerSocket>();
         var obj = testObj.CreateHostPhase(listenerSocket);
         Verify(threadingProxy, Once()).CreateThread(Any<ThreadEntryPoint>(), Any<ThreadCreationOptions>());
         Verify(threadingProxy, Once()).CreateCancellationTokenSource();
         VerifyNoMoreInteractions();

         AssertTrue(obj is HostPhase);
      }

      [Fact]
      public void CreateGuestPhaseTest() {
         var clientSocket = CreateMock<IConnectedSocket>();
         var obj = testObj.CreateGuestPhase(clientSocket);
         AssertTrue(obj is GuestPhase);
      }
   }
}
