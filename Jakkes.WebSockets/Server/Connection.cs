﻿using AwaitableQueue;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jakkes.WebSockets.Server
{
    public delegate void ConnectionStateChangedEventHandler(Connection conn, ConnectionState state);
    public delegate void TextReceivedEventHandler(Connection conn, string text);
    public delegate void BinaryReceivedEventHandler(Connection conn, byte[] binary);
    public delegate void ServerMessageSentEventHandler(Connection conn, ServerMessage msg);
    

    public class Connection 
    {
        private TcpClient _conn;
        private NetworkStream _stream;

        public event TextReceivedEventHandler TextReceived;
        public event BinaryReceivedEventHandler BinaryReceived;
        public event ServerMessageSentEventHandler MessageSent;
        public event ConnectionStateChangedEventHandler StateChanged;
        private ConnectionState _state = ConnectionState.Closed;
        public ConnectionState State
        {
            get { return _state; }
            private set
            {
                if(value != _state)
                {
                    _state = value;
                    StateChanged?.Invoke(this, State);
                }
            }
        }

        private OpCode currentOpCode;
        private List<byte> binary = new List<byte>();

        private AsyncPrioQueue<ServerMessage> _queue = new AsyncPrioQueue<ServerMessage>();

        internal Connection(TcpClient conn)
        {
            _conn = conn;
            _stream = conn.GetStream();
            _handshake();

            State = ConnectionState.Open;

            Task.Run(() => Read());
            Task.Run(() => _sendWorker(new CancellationToken()));
        }

        public void Send(string text) => Send(new ServerMessage(text));
        public void Send(string text, Action OnSuccess, Action OnFail)
            => Send(new ServerMessage(text,OnSuccess,OnFail));
        public void Send(byte[] binary) => Send(new ServerMessage(binary));
        public void Send(byte[] binary, Action OnSuccess, Action OnFail)
            => Send(new ServerMessage(binary,OnSuccess,OnFail));
        public void Send(ServerMessage msg)
        {
            _queue.Enqueue(msg);
        }
        private void SendPrioritized(ServerMessage msg)
        {
            _queue.EnqueuePrioritized(msg);
        }

        private async void _sendWorker(CancellationToken cancellationToken)
        {
            if (State == ConnectionState.Open && !cancellationToken.IsCancellationRequested)
            {
                ServerMessage msg = await _queue.DequeueAsync();
                _writeToStream(msg);
                MessageSent?.Invoke(this, msg);
                msg.OnSuccess();
            }

            Task.Run(() => _sendWorker(cancellationToken));
        }

        private void _writeToStream(ServerMessage msg)
        {
            if (State != ConnectionState.Open)
                if (State != ConnectionState.Closing && msg.opCode != OpCode.ConnectionClose)
                    throw new ConnectionClosedException();

            // TODO Rewrite this using class Frame and further down the road allowing streams using continuation frame
            var code = msg.opCode;
            var data = msg.Data;

            List<byte> bytes = new List<byte>();
            bytes.Add((byte)(0x80 + code));
            if (data.Length <= 125)
                bytes.Add((byte)(data.Length));
            else if (data.Length <= 65535)
            {
                bytes.Add((byte)126);
                bytes.Add((byte)((0xFF & (data.Length >> 8))));
                bytes.Add((byte)(0xFF & data.Length));
            }
            else
            {
                bytes.Add((byte)127);
                for (int i = 0; i < 8; i++)
                    bytes.Add((byte)(0xFF & ((long)(data.Length) >> (8 * (7 - i)))));
            }
            bytes.AddRange(data);

            _stream.Write(bytes.ToArray(), 0, bytes.Count);
        }

        private async Task Read()
        {
            if (State == ConnectionState.Closed)
                return;
            try
            {
                var frame = await GetFrame();
                HandleFrame(frame);
            }
            catch (UnmaskedMessageException) { Close(); }

#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
            Read();
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
        }

        public void Close()
        {
            SendPrioritized(new ServerMessage(new byte[0],OpCode.ConnectionClose));
            State = ConnectionState.Closing;
        }
        public void Kill()
        {
            _stream.Dispose();
            _conn.Dispose();
            State = ConnectionState.Closed;
        }
        private void HandleFrame(Frame frame)
        {
            if (frame.FIN)
            {
                switch (frame.OpCode)
                {
                    case OpCode.TextFrame:
                        binary.AddRange(frame.UnmaskedData);
                        TextReceived?.Invoke(this, Encoding.UTF8.GetString(binary.ToArray()));
                        break;
                    case OpCode.BinaryFrame:
                        binary.AddRange(frame.UnmaskedData);
                        BinaryReceived?.Invoke(this, binary.ToArray());
                        break;
                    case OpCode.ConnectionClose:
                        HandleCloseRequest(frame);
                        break;
                    case OpCode.Ping:
                        Pong(frame);
                        break;
                    case OpCode.ContinuationFrame:
                        if (currentOpCode == OpCode.TextFrame)
                            TextReceived?.Invoke(this, Encoding.UTF8.GetString(binary.ToArray()));
                        else
                            BinaryReceived?.Invoke(this, binary.ToArray());
                        break;
                    default:
                        Close();
                        break;
                }
                binary.Clear();
            } else
            {
                binary.AddRange(frame.UnmaskedData);
                currentOpCode = frame.OpCode;
            }
        }

        private void Pong(Frame frame)
        {
            SendPrioritized(new ServerMessage(frame.UnmaskedData,OpCode.Pong));
        }

        private void HandleCloseRequest(Frame frame)
        {
            if(State != ConnectionState.Closing){
                State = ConnectionState.Closing;
                MessageSent += (o,e) => {
                    if(e.opCode == OpCode.ConnectionClose)
                        _shutdown();
                };
                SendPrioritized(new ServerMessage(frame.UnmaskedData,OpCode.ConnectionClose));
            }
        }

        private void _shutdown(){
            _stream.Dispose();
            _conn.Dispose();
            State = ConnectionState.Closed;   
        }

        private async Task<Frame> GetFrame()
        {
            var re = new Frame();

            // Read until first 2 bytes are read
            int read = 0;
            byte[] buff = new byte[2];
            read += await _stream.ReadAsync(buff, 0, buff.Length);
            while (read < buff.Length)
                read += await _stream.ReadAsync(buff, read, buff.Length - read);

            // Check message is masked
            if ((buff[1] >> 7) != 1)
                throw new UnmaskedMessageException();

            // Check RSV1,2,3 are set 0
            if ((buff[0] & 0x70) != 0)
                throw new NotImplementedException("RSV1, RSV2, RSV3 must be set to 0.");

            // Extract opcode
            re.OpCode = (OpCode)(buff[0] & 0xF);

            // Extract FIN
            re.FIN = (buff[0] >> 7) == 1;

            // Extract length
            int len = buff[1] & 0x7F;
            if (len < 126) re.Length = len;
            else if (len == 126)
                re.Length = (_stream.ReadByte() << 8) + _stream.ReadByte();
            else
                for (int i = 0; i < 8; i++)
                    re.Length += _stream.ReadByte() << (8 * (7 - i));

            // Extract mask
            re.Mask = new byte[4];
            _stream.Read(re.Mask, 0, 4);

            // Extract payload
            re.Payload = new byte[re.Length];
            long count = 0;
            while (count < re.Length)
                count += _stream.Read(re.Payload, (int)count, (int)(re.Length - count));

            return re;
        }

        private void _handshake()
        {
            StreamReader reader = new StreamReader(_stream);
            Dictionary<string, string> dict = new Dictionary<string, string>();

            string line;
            while (!string.IsNullOrEmpty(line = reader.ReadLine()))
            {
                if (line.StartsWith("Sec-WebSocket-Key"))
                    dict.Add("Key", line.Split(':')[1].Trim());
                if (line.StartsWith("GET"))
                    dict.Add("Protocol", line.Split(' ')[1].Trim().Substring(1));
            }

            if (!dict.ContainsKey("Key"))
                throw new HandshakeException("Failed to receive the key necessary to upgrade the connection.");
            string acceptKey = Convert.ToBase64String(
                                    SHA1.Create().ComputeHash(
                                        Encoding.UTF8.GetBytes(
                                            dict["Key"] + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"
                                        )
                                    )
                                );

            string response = "HTTP/1.1 101 Switching Protocols" + Environment.NewLine;
            response += "Upgrade: websocket" + Environment.NewLine;
            response += "Connection: Upgrade" + Environment.NewLine;
            response += "Sec-WebSocket-Accept: " + acceptKey + Environment.NewLine;
            if (dict.ContainsKey("Protocol") && !string.IsNullOrEmpty(dict["Protocol"]))
                response += "Sec-WebSocket-Protocol: " + dict["Protocol"] + Environment.NewLine;
            response += Environment.NewLine;
            var bytes = Encoding.UTF8.GetBytes(response);
            _stream.Write(bytes, 0, bytes.Length);
            _stream.Flush();
        }

        private class Frame
        {
            public bool FIN { get; set; }
            public long Length { get; set; }
            public byte[] Mask { get; set; }
            public byte[] Payload { get; set; }
            public OpCode OpCode { get; set; }
            public byte[] UnmaskedData
            {
                get
                {
                    if (_unmaskedData == null)
                    {
                        var d = new byte[Payload.Length];
                        for (int i = 0; i < Payload.Length; i++)
                            d[i] = (byte)(Payload[i] ^ Mask[i % 4]);
                        _unmaskedData = d;
                    }
                    return _unmaskedData;
                }
            }

            private byte[] _unmaskedData;
        }
    }

    [Serializable]
    public class ConnectionClosedException : Exception
    {
        public ConnectionClosedException()
        {
        }

        public ConnectionClosedException(string message) : base(message)
        {
        }

        public ConnectionClosedException(string message, Exception innerException) : base(message, innerException)
        {
        }

        protected ConnectionClosedException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }

    [Serializable]
    public class UnmaskedMessageException : Exception
    {
        public UnmaskedMessageException()
        {
        }

        public UnmaskedMessageException(string message) : base(message)
        {
        }

        public UnmaskedMessageException(string message, Exception innerException) : base(message, innerException)
        {
        }

        protected UnmaskedMessageException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }

    public enum ConnectionState
    {
        Open,
        Closed,
        Closing
    }

    public class HandshakeException : Exception
    {
        public HandshakeException()
        {

        }
        public HandshakeException(string msg) : base(msg)
        {

        }
    }
}