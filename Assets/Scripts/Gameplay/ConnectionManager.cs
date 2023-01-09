using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using UnityEngine;
using UnityEngine.Assertions;

using DarkRift;
using DarkRift.Client;
using DarkRift.Client.Unity;
using DarkRift.Server;
using DarkRift.Server.Unity;

using Lockstep;

namespace Lockstep
{
    [RequireComponent(typeof(UnityClient), typeof(XmlUnityServer))]
    public class ConnectionManager : MonoBehaviour
    {
        public static ConnectionManager Instance { get; private set; }

        const float m_SetupServerRetryIntervalSec = 1f;
        const float m_ConnectClientRetryIntervalSec = 1f;

        XmlUnityServer m_SelfServer;
        UnityClient m_SelfClient; // Connection from self client (us: reader) to peer server (writer)
        IClient m_PeerClient; // Connection from peer client (reader) to self server (us: writer)

        bool m_SetupComplete = false;
        bool m_PeerSetupCompleteReceived = false;

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this.gameObject);
                return;
            }

            Instance = this;

            m_SelfClient = GetComponent<UnityClient>();
            m_SelfServer = GetComponent<XmlUnityServer>();

            AddOnMessageReceived(OnMessageReceived);
        }

        public IEnumerator Setup()
        {
            if (m_SetupComplete)
            {
                Debug.LogError("Setup called multiple times");
            }

            m_SetupComplete = false;
            m_PeerSetupCompleteReceived = false;

            // Setup self server
            yield return SetupServer(Settings.SelfPort);

            // Connect self client to peer server
            yield return ConnectClient(Settings.PeerAddress, Settings.PeerPort);

            // Wait until peer client is connected to self server
            yield return new WaitUntil(() => m_PeerClient != null && (m_PeerClient.ConnectionState == ConnectionState.Connected));

            // TODO unnecessary?
            // Send client setup completion to peer and wait for theirs
            yield return SetupCompleteSync();

            Debug.Log("Setup complete");

            m_SetupComplete = true;
        }

        // This should be called as early as possible to ensure that no messages are missed
        public void AddOnMessageReceived(EventHandler<DarkRift.Client.MessageReceivedEventArgs> handler)
        {
            // If m_SelfClient is null, then this has been called before this class' Awake()
            Assert.IsTrue(m_SelfClient != null);
            m_SelfClient.MessageReceived += handler;
        }

        public void RemoveOnMessageReceived(EventHandler<DarkRift.Client.MessageReceivedEventArgs> handler)
        {
            Assert.IsTrue(m_SelfClient != null);
            m_SelfClient.MessageReceived -= handler;
        }

        public void SendMessage(Message message, SendMode sendMode)
        {
            /*
            DO NOT PASS MESSAGES INTO COROUTINES

            Removing this solved a bug related to malformed messages. I presume
            that messages are automatically disposed of in the process of
            the coroutine's execution.

            https://www.darkriftnetworking.com/DarkRift2/Docs/2.10.1/advanced/recycling.html
            */

            /*
            ARTIFICIAL PACKET LOSS MUST BE SENDER-SIDE

            Artificial packet loss must be done while sending, not receiving.

            Doing it after receipt undermines TCP packets. When you drop a
            reliably-sent packet from the application layer, it won't
            trigger the reliability mechanisms (resending) since they already
            completed their job by pushing the packet to the application layer.
            */

            /*
            TODO probably put Send() functions in the message classes themselves too

            TODO re-implement below

            // Artifical latency
            if (Settings.ArtificialLatencyMs > 0)
            {
                yield return new WaitForSecondsRealtime(Settings.ArtificialLatencyMs / 1000f);
            }

            // Artificial packet loss
            if (sendMode == SendMode.Unreliable && RandomService.ReturnTrueWithProbability(Settings.ArtificialPacketLossPc))
            {
                Debug.Log("Packet artifically dropped");
                yield break;
            }
            */

            Assert.IsTrue(m_PeerClient != null);
            Assert.IsTrue(m_PeerClient.ConnectionState == ConnectionState.Connected);

            if (!m_PeerClient.SendMessage(message, sendMode))
            {
                Debug.Log("Failed to send message");
            }
        }

        IEnumerator SetupServer(int port)
        {
            while (!TrySetupServer(port))
            {
                Debug.LogError($"Failed to setup self server on port {Settings.SelfPort}, retrying...");
                yield return new WaitForSecondsRealtime(m_SetupServerRetryIntervalSec);
            }
        }

        bool TrySetupServer(int port)
        {
            /*
            PORT IN USE ERROR

            When creating a server, a health check service is also created
            on a fixed port (default=10666).

            https://www.darkriftnetworking.com/DarkRift2/Docs/2.10.1/advanced/internal_plugins/http_health_check.html

            Hence trying to create two servers on the same IP will
            result in a port conflict on the health check port *regardless of
            the actual server port*.

            To solve this, disable the health check plugin.

            You can see ports and their PIDs on Windows by running
            "netstat -ano" in a terminal with admin permissions.
            Both the actual server port and the health check port are
            associated with the same PID.
            */

            try
            {
                NameValueCollection configVariables = new NameValueCollection();
                configVariables.Add("port", port.ToString());
                m_SelfServer.Create(configVariables);

                m_SelfServer.Server.ClientManager.ClientConnected += OnClientConnected;
                m_SelfServer.Server.ClientManager.ClientDisconnected += OnClientDisconnected;

                return true;
            }
            catch (Exception e)
            {
                Debug.LogError(e);
                m_SelfServer.Close();
                return false;
            }
        }

        IEnumerator ConnectClient(string peerAddress, int peerPort)
        {
            while (true)
            {
                if (TryConnectClient(peerAddress, peerPort))
                {
                    Assert.IsTrue(m_SelfClient.ConnectionState == ConnectionState.Connecting || m_SelfClient.ConnectionState == ConnectionState.Connected);

                    yield return new WaitUntil(() => m_SelfClient.ConnectionState != ConnectionState.Connecting);

                    // Successfully connected
                    if (m_SelfClient.ConnectionState == ConnectionState.Connected)
                    {
                        break;
                    }
                }

                Debug.LogError($"Failed to connect self client to peer server at {peerAddress}:{peerPort}, retrying...");
                yield return new WaitForSecondsRealtime(m_ConnectClientRetryIntervalSec);
            }
        }

        // Warning: "localhost" doesn't always work whereas 127.0.0.1 is consistent
        bool TryConnectClient(string peerAddress, int peerPort)
        {
            try
            {
                m_SelfClient.Connect(host: peerAddress, port: peerPort, noDelay: true);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError(e);
                return false;
            }
        }

        IEnumerator SetupCompleteSync()
        {
            SendSetupComplete();

            yield return new WaitUntil(() => m_PeerSetupCompleteReceived);
        }

        void SendSetupComplete()
        {
            using (Message msg = SetupCompleteMsg.CreateMessage())
            {
                SendMessage(msg, SendMode.Reliable);
            }
        }

        void OnClientConnected(object sender, ClientConnectedEventArgs e)
        {
            if (m_PeerClient != null && (m_PeerClient.ConnectionState == ConnectionState.Connected))
            {
                Debug.LogError("Multiple peer clients connected");
            }

            m_PeerClient = e.Client;
        }

        void OnClientDisconnected(object sender, ClientDisconnectedEventArgs e)
        {
            Debug.LogError("Peer client disconnected from self server");
        }

        void OnMessageReceived(object sender, DarkRift.Client.MessageReceivedEventArgs e)
        {
            using (Message message = e.GetMessage() as Message)
            {
                if (message.Tag == Tags.SetupComplete)
                {
                    HandleSetupCompleteMsg(sender, e);
                }
            }
        }

        void HandleSetupCompleteMsg(object sender, DarkRift.Client.MessageReceivedEventArgs e)
        {
            using (Message message = e.GetMessage())
            {
                m_PeerSetupCompleteReceived = true;
            }
        }
    }
}
