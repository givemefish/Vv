using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

public class PmmClient : IDisposable
{
    private TcpClient _client;
    private NetworkStream _stream;
    private CancellationTokenSource _cts;
    private Task _receiveTask;

    // 定義兩個事件
    public event EventHandler<PmmResponseEventArgs> OnRP; // Request-Response (單次)
    public event EventHandler<PmmResponseEventArgs> OnPB; // Publish-Broadcast (持續)

    // 定義兩個字典來區分請求的生命週期
    // _pendingRequests: 單次請求，收到回應後移除
    private readonly ConcurrentDictionary<string, Type> _pendingRequests = new ConcurrentDictionary<string, Type>();
    
    // _activeSubscriptions: 訂閱請求，收到回應後不移除，持續監聽
    private readonly ConcurrentDictionary<string, Type> _activeSubscriptions = new ConcurrentDictionary<string, Type>();

    public bool IsConnected => _client != null && _client.Connected;

    public async Task ConnectAsync(string ip, int port)
    {
        if (IsConnected) return;

        _client = new TcpClient();
        await _client.ConnectAsync(ip, port);
        _stream = _client.GetStream();
        _cts = new CancellationTokenSource();

        // 啟動接收迴圈
        _receiveTask = Task.Run(ReceiveLoop, _cts.Token);
        Console.WriteLine($"[Client] Connected to {ip}:{port}");
    }

    /// <summary>
    /// 發送單次請求 (觸發 OnRP)
    /// </summary>
    public void Request<T>(T packet) where T : BasePacket
    {
        SendPacket(packet, isSubscription: false);
    }

    /// <summary>
    /// 發送訂閱請求 (觸發 OnPB)
    /// </summary>
    public void Subscribe<T>(T packet) where T : BasePacket
    {
        SendPacket(packet, isSubscription: true);
    }

    private void SendPacket<T>(T packet, bool isSubscription) where T : BasePacket
    {
        if (!IsConnected) throw new InvalidOperationException("Not connected.");

        if (string.IsNullOrEmpty(packet.CommandType)) 
            packet.CommandType = typeof(T).Name;

        // 關鍵分流：根據用途存入不同的字典
        if (isSubscription)
        {
            _activeSubscriptions.TryAdd(packet.RequestId, typeof(T));
        }
        else
        {
            _pendingRequests.TryAdd(packet.RequestId, typeof(T));
        }

        // 序列化並加上長度標頭 (Length-Prefix Framing)
        string json = JsonSerializer.Serialize(packet);
        byte[] data = Encoding.UTF8.GetBytes(json);
        byte[] lengthBytes = BitConverter.GetBytes(data.Length);

        lock (_stream) // 確保執行緒安全
        {
            _stream.Write(lengthBytes, 0, lengthBytes.Length);
            _stream.Write(data, 0, data.Length);
        }
    }

    // 背景接收迴圈
    private async Task ReceiveLoop()
    {
        byte[] lenBuf = new byte[4];
        try
        {
            while (!_cts.Token.IsCancellationRequested && IsConnected)
            {
                // 1. 讀取長度 (4 bytes)
                if (await ReadExactAsync(lenBuf, 4) == 0) break;
                int bodyLen = BitConverter.ToInt32(lenBuf, 0);

                // 2. 讀取內容
                byte[] bodyBuf = new byte[bodyLen];
                if (await ReadExactAsync(bodyBuf, bodyLen) == 0) break;

                // 3. 處理訊息
                string json = Encoding.UTF8.GetString(bodyBuf);
                ProcessMessage(json);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Client] Error in loop: {ex.Message}");
        }
        finally { Disconnect(); }
    }

    private void ProcessMessage(string json)
    {
        try
        {
            var response = JsonSerializer.Deserialize<PmmResponse>(json);
            if (response == null || response.RequestId == null) return;

            // 檢查 1: 是否為單次 Request 的回應？ (取後移除)
            if (_pendingRequests.TryRemove(response.RequestId, out Type reqType))
            {
                OnRP?.Invoke(this, new PmmResponseEventArgs { Response = response, RequestType = reqType });
            }
            // 檢查 2: 是否為訂閱 Subscribe 的推送？ (只取不移除)
            else if (_activeSubscriptions.TryGetValue(response.RequestId, out Type subType))
            {
                OnPB?.Invoke(this, new PmmResponseEventArgs { Response = response, RequestType = subType });
            }
            else
            {
                Console.WriteLine($"[Client] Warning: Received ID {response.RequestId} but no match found.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Client] Parse Error: {ex.Message}");
        }
    }

    private async Task<int> ReadExactAsync(byte[] buffer, int length)
    {
        int totalRead = 0;
        while (totalRead < length)
        {
            int read = await _stream.ReadAsync(buffer, totalRead, length - totalRead, _cts.Token);
            if (read == 0) return 0;
            totalRead += read;
        }
        return totalRead;
    }

    public void Disconnect()
    {
        _cts?.Cancel();
        _client?.Close();
    }

    public void Dispose() => Disconnect();
}
