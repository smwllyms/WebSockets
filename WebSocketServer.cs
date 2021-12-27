using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Text.RegularExpressions;

public class WebSocketServer
{
    public bool running { get; protected set; } 
    public int numClients { get; protected set; }
    public int port { get; protected set; }
    public Action<int> onClientConnect { get; set; }
    public Action<int, byte[], int> onClientMessage { get; set; }
    public Action<int> onClientDisconnect { get; set; }

    private TcpListener listener;
    private Action<String> logCallback = Console.WriteLine;
    private Dictionary<int, TcpClient> clients;
    public WebSocketServer() {
    }
    public void Start(int port) {
        try {
            listener = TcpListener.Create(port);
            listener.Start();
            // Success
            this.running = true;
            this.port = port;
            this.clients = new Dictionary<int, TcpClient>();
            this.numClients = 0;
            new Thread(AcceptClientLoop).Start();

            logCallback("Started Server on port " + port);
        }
        catch (Exception e) {
            logCallback("Could not start server: " + e);
        }
    }
    public void Send(int id, object message, int opcodeType) {
        TcpClient client = null;
        clients.TryGetValue(id, out client);
        if (client != null) {
            switch (opcodeType) {
                case 1:
                    SendString(client, (string)message);
                    break;
                case 2:
                    SendBytes(client, (byte[])message);
                    break;
                default: break;
            }
        }
    }
    private void AcceptClientLoop() {
        TcpClient client;
        try {
            while (running) {
                client = listener.AcceptTcpClient();
                new Thread(()=>InitializeClient(client)).Start();
            }                
        }            
        catch (Exception e) {
            // Shut down server
            e.ToString();
        }
    }

    private void InitializeClient(TcpClient client) {

        // Check for null client
        if (client == null) return; 

        // Handshake
        NetworkStream stream = client.GetStream();
        // Match for Get
        while (stream.DataAvailable && client.Available < 3);
        string msg = Encoding.ASCII.GetString(_Read(client));

        if (Regex.IsMatch(msg, "^GET")) {
            const string eol = "\r\n"; // HTTP/1.1 defines the sequence CR LF as the end-of-line marker

            Byte[] response = Encoding.UTF8.GetBytes("HTTP/1.1 101 Switching Protocols" + eol
                + "Connection: Upgrade" + eol
                + "Upgrade: websocket" + eol
                + "Sec-WebSocket-Accept: " + Convert.ToBase64String(
                    System.Security.Cryptography.SHA1.Create().ComputeHash(
                        Encoding.UTF8.GetBytes(
                            new System.Text.RegularExpressions.Regex("Sec-WebSocket-Key: (.*)").Match(msg).Groups[1].Value.Trim() + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"
                        )
                    )
                ) + eol
                + eol);

            _Write(client, response);

            // Client authenticated
            int id = numClients++;
            clients.Add(id, client);

            if (onClientConnect != null) onClientConnect(id);

            ClientConversation(client, id);
        }
    }

    private void ClientConversation(TcpClient client, int id) {

        NetworkStream stream = client.GetStream();

        bool fin, mask; int opcode = -1, msglen, offset;
        
        while (running) {
            try {
                while (client.Connected && !stream.DataAvailable);
            } catch (Exception e) { e.ToString(); }
            while (client.Connected && client.Available < 3);
            if (!client.Connected) break;
            byte[] bytes = _Read(client);

            fin = (bytes[0] & 0b10000000) != 0;
            mask = (bytes[1] & 0b10000000) != 0; // must be true, "All messages from the client to the server have this bit set"

            opcode = bytes[0] & 0b00001111; // expecting 1 - text message
            if (opcode == 8) break;
            msglen = bytes[1] - 128; // & 0111 1111
            offset = 2;

            if (msglen == 126) {
                // was ToUInt16(bytes, offset) but the result is incorrect
                msglen = BitConverter.ToUInt16(new byte[] { bytes[3], bytes[2] }, 0);
                offset = 4;
            } else if (msglen == 127) {
                logCallback("TODO: msglen == 127, needs qword to store msglen");
                // i don't really know the byte order, please edit this
                // msglen = BitConverter.ToUInt64(new byte[] { bytes[5], bytes[4], bytes[3], bytes[2], bytes[9], bytes[8], bytes[7], bytes[6] }, 0);
                // offset = 10;
            }

            if (msglen == 0)
                logCallback("msglen == 0");
            else if (mask) {
                byte[] decoded = new byte[msglen];
                byte[] masks = new byte[4] { bytes[offset], bytes[offset + 1], bytes[offset + 2], bytes[offset + 3] };
                offset += 4;

                for (int i = 0; i < msglen; ++i)
                    decoded[i] = (byte)(bytes[offset + i] ^ masks[i % 4]);

                string text = Encoding.UTF8.GetString(decoded);
                if (onClientMessage != null) onClientMessage(id, decoded, opcode);

            }
        }
        
        // Cleanup
        if (opcode == 8 && onClientDisconnect != null) onClientDisconnect(id);
        clients.Remove(id);

    }

    private static byte[] _Read(TcpClient client) {
        int available = client.Available;
        NetworkStream stream = client.GetStream();
        byte[] msg = new byte[available];
        int i = 0;
        while (i < available)
            i += stream.Read(msg, i, available - i);

        return msg;
    }

// Frame format:
//       0                   1                   2                   3
//       0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
//      +-+-+-+-+-------+-+-------------+-------------------------------+
//      |F|R|R|R| opcode|M| Payload len |    Extended payload length    |
//      |I|S|S|S|  (4)  |A|     (7)     |             (16/64)           |
//      |N|V|V|V|       |S|             |   (if payload len==126/127)   |
//      | |1|2|3|       |K|             |                               |
//      +-+-+-+-+-------+-+-------------+ - - - - - - - - - - - - - - - +
//      |     Extended payload length continued, if payload len == 127  |
//      + - - - - - - - - - - - - - - - +-------------------------------+
//      |                               |Masking-key, if MASK set to 1  |
//      +-------------------------------+-------------------------------+
//      | Masking-key (continued)       |          Payload Data         |
//      +-------------------------------- - - - - - - - - - - - - - - - +
//      :                     Payload Data continued ...                :
//      + - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - +
//      |                     Payload Data continued ...                |
//      +---------------------------------------------------------------+
    protected static void Send(TcpClient client, byte[] msg, int opcode) {
        // Special formatting
        int len = msg.Length, offset = 2;
        byte[] encoded = new byte[2 + 8 + len];
        encoded[0] = (byte)(128 | opcode); // fin high bit and opcode
        if (len < 126) {
            encoded[1] = (byte)len; // mask high bit
        }
        else if (len < 32767) {
            encoded[1] = 126; // 126 indicates int16
            Array.Copy(new Int16[] { (Int16) len }, 0, encoded, 2, sizeof(Int16));
            offset += 2;
        }
        else {
            encoded[1] = 127; // 127 indicates int64
            Array.Copy(new Int64[] { (Int64) len }, 0, encoded, 2, sizeof(Int64));
            offset += 8;            
        }

        Array.Copy(msg, 0, encoded, offset, len);
        _Write(client, encoded, offset + len);
    }
    protected static void SendString(TcpClient client, string msg) {
        Send(client, Encoding.ASCII.GetBytes(msg), 1);
    }
    protected static void SendBytes(TcpClient client, byte[] msg) {
        Send(client, msg, 1);
    }
    protected void SendToAll(object msg, int opcodeType) {
        switch (opcodeType) {
            case 8:
                foreach (var pair in clients) {
                    DisconnectClient(pair.Value);
                }
                break;
            case 1: 
                foreach (var pair in clients) {
                    SendString(pair.Value, (string)msg);
                }
                break;
            case 2: 
                foreach (var pair in clients) {
                    SendBytes(pair.Value, (byte[])msg);
                }
                break;
            default : break;
        }
    } 
    protected void DisconnectClient(TcpClient client) {
        _Write(client, new byte[] {128 | 8});
        client.Close();
    }
    private static void _Write(TcpClient client, byte[] msg) {
        _Write(client, msg, msg.Length);
    }
    private static void _Write(TcpClient client, byte[] msg, int len) {
        NetworkStream stream = client.GetStream();
        stream.Write(msg, 0, len);
    }

    public void Stop() {
        running = false;
        SendToAll(null, 8);
        listener.Stop();
        logCallback("Stopped Server");
    }

    public void SetLogCallback(Action<String> logCallback) {
        this.logCallback = logCallback;
    }
}