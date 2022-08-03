using Makaretu.Dns;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace VolumeControl
{
    public class Server
    {
        private ClientListener m_clientListener;
        private TcpListener m_tcpListener;
        private Thread listenThread;
        private List<TcpClient> m_clients = new List<TcpClient>();
        private bool m_running = false;
        private ASCIIEncoding m_encoder = new ASCIIEncoding();
        private Thread mDNSThread;
       // private ServiceDiscovery sd;

        public Server(ClientListener clientListener, string address, int port)
        {
            var parsedAddress = IPAddress.Parse(address);
            m_tcpListener = new TcpListener(parsedAddress, port);
            listenThread = new Thread(new ThreadStart(ListenForClients));
            listenThread.Start();
            m_clientListener = clientListener;
            Console.WriteLine("Server listening on address: {0}:{1}", parsedAddress, port);

            // multicast DNS/service announcement stuff
           // var sd = new ServiceDiscovery();
            mDNSThread = new Thread(() => StartMulticastDNS((ushort)port, 5000, parsedAddress));
            mDNSThread.Start();
        }

        // Multicast DNS - used to broadcast/announce this server on the LAN for clients.
        // Broadcasts go out every interface to ipv4/ipv6 multicast addresses.
        // This sends out a broadcast on all network interfaces.
        // port is the port number, interval is the number of milliseconds between announcements.
        public void StartMulticastDNS(ushort port, int interval, IPAddress address)
        {
            var service = new ServiceProfile("_pcvolumecontrol", "_pcvolumecontrol._tcp.", port);
            service.AddProperty("pcvcaddress", address.ToString());
            service.AddProperty("pcvcport", port.ToString());

            var sd = new ServiceDiscovery();
    
            while (isRunning())
            {
                sd.Announce(service);
                Console.WriteLine("mDNS announcing address {0} port {1} every {2} milliseconds...", address, port, interval);
                System.Threading.Thread.Sleep(interval);
            }
            if (!isRunning()){
                Console.WriteLine("unadvertising all announced mDNS messages");
                //sd.Unadvertise();
            }
        }

        public bool isRunning()
        {
            return m_running;
        }

        public void stop()
        {
            m_running = false;

            m_tcpListener.Stop();

            //sd.Unadvertise(); // graceful exit, we can send the "we're gone now" update on the multicast address
            //mDNSThread.Abort();

            lock ( this )
            {
                foreach (var client in m_clients)
                {
                    Console.WriteLine("Closing client connection...");

                    try
                    {
                        client.Close();
                    }
                    catch (IOException e)
                    {

                    }
                    catch (ObjectDisposedException e)
                    {

                    }
                }
            }
        }

        private void ListenForClients()
        {
            this.m_tcpListener.Start();

            m_running = true;
            m_clientListener.onServerStart();

            while (m_running)
            {
                //blocks until a client has connected to the server
                try
                {
                    TcpClient client = m_tcpListener.AcceptTcpClient();
                    Console.WriteLine("connection accepted");
                    //create a thread to handle communication 
                    //with connected client
                    Thread clientThread = new Thread(new ParameterizedThreadStart(HandleClientComm));
                    clientThread.Start(client);
                }
                catch(SocketException e)
                {

                }
            }

            m_running = false;
            m_clientListener.onServerEnd();
        }

        private void HandleClientComm(object client)
        {
            Console.WriteLine("Client connected");

            TcpClient tcpClient = (TcpClient)client;
            lock (this)
            {
                m_clients.Add(tcpClient);
            }

            m_clientListener.onClientConnect();

            try
            {
                NetworkStream clientStream = tcpClient.GetStream();
                var bufferedStream = new BufferedStream(clientStream);
                var streamReader = new StreamReader(bufferedStream);

                while (tcpClient.Connected)
                {
                    string message;
                    try
                    {
                        //blocks until a client sends a message
                        //bytesRead = clientStream.Read(message, 0, 4096);
                        message = streamReader.ReadLine();
                    }
                    catch
                    {
                        //a socket error has occured
                        break;
                    }

                    // End of message
                    if (message != null)
                    {
                        Console.WriteLine("Message received");
                        if (m_clientListener != null)
                        {
                            m_clientListener.onClientMessage(message, tcpClient);
                        }
                        else
                        {
                            Console.WriteLine("Message missed, no listener");
                        }
                    }
                    else
                    {
                        Console.WriteLine("No message from client, close socket.");
                        break;
                    }
                }
            }
            catch(InvalidOperationException e)
            {

            }
            finally
            {
                lock (this)
                {
                    m_clients.Remove(tcpClient);
                }
                tcpClient.Close();
                tcpClient.Dispose();
                Console.WriteLine("Client disconnected");
            }
        }

        public void sendData(string data)
        {
            var finalData = data;
            if (data != null && data.Length > 0 && data[data.Length - 1] != '\n')
            {
                finalData += '\n';
            }

            List<TcpClient> clients;
            lock (this)
            {
                clients = m_clients.ToList();
            }

            byte[] buffer = m_encoder.GetBytes(finalData);

            foreach (var client in clients)
            {
                Console.WriteLine("Sending data to a client...");

                try
                {
                    NetworkStream clientStream = client.GetStream();

                    clientStream.Write(buffer, 0, buffer.Length);
                    clientStream.Flush();
                }
                catch(IOException e)
                {

                }
                catch (ObjectDisposedException e)
                {

                }
            }
        }
    }

    public interface ClientListener
    {
        void onClientMessage( string message, TcpClient tcpClient);
        void onClientConnect();

        void onServerStart();
        void onServerEnd();
    }
}
